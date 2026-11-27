using ChipSharp.Abstractions;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// The TIR that printed text becomes (see PrintedText): each hole is kept in a slot where the text takes it, and the
/// Console.Write or Console.WriteLine of the text writes the literals and those slots one after another, so every hole
/// is worked out before anything is written; aligned and formatted parts go through the ChipSharp.Text functions.
/// </summary>
public sealed class IlToTirPrintedTextTests
{
    [Fact]
    public void A_hole_waits_in_a_slot_and_the_print_writes_the_parts_one_after_another()
    {
        // $"..." (a DefaultInterpolatedStringHandler, whose local is an unused ptr slot) and + (string.Concat of
        // lane.ToString(), which reads lane through its address): no string, no handler, only the runtime's writes
        var main = Lowered.Function("""
            using System;

            class Program
            {
                static void Main()
                {
                    int lane = 2;
                    Console.WriteLine($"road : {lane}");
                    Console.Write("lane " + lane + "!");
                }
            }
            """, "Program.Main");

        Assert.Equal("""
            function void Program.Main()
              $V_0: i32
              $V_1: ptr
              $part_IL_001a: i32
              $part_IL_0032: i32
            IL_0000:
              %0 = const i32 2
              store $V_0, %0
              %1 = load i32 $V_0
              store $part_IL_001a, %1
              %2 = addr @<text 0>
              call void ChipSharp.Runtime.WriteString(%2)
              %3 = load i32 $part_IL_001a
              call void ChipSharp.Runtime.WriteLineInt32(%3)
              %4 = addr @<text 1>
              %5 = addr $V_0
              %6 = load i32 [%5]
              store $part_IL_0032, %6
              %7 = addr @<text 1>
              call void ChipSharp.Runtime.WriteString(%7)
              %8 = load i32 $part_IL_0032
              call void ChipSharp.Runtime.WriteInt32(%8)
              %9 = addr @<text 2>
              call void ChipSharp.Runtime.WriteString(%9)
              return
            """.ReplaceLineEndings("\n"), main.TrimEnd().ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_hole_is_worked_out_before_anything_is_written(bool debug)
    {
        // as in .NET, where Next prints before the line it is in: both calls, then the writes
        const string Source = """
            using System;

            class Program
            {
                static int calls;

                static int Next()
                {
                    Console.Write("[next]");
                    return ++calls;
                }

                static void Main()
                {
                    Console.WriteLine($"{Next()} and {Next(),4}");
                    Console.WriteLine("a" + Next() + "b" + Next() + "c" + Next());
                }
            }
            """;
        var module = debug ? Lowered.Lower(Lowered.Program(Lowered.CompileDebug(Source, "Test"))) : Lowered.Module(Source);
        var lines = TirPrinter.Print(module.Functions.Single(f => f.Name == "Program.Main")).Split('\n');

        // each print: three or two calls of Next, each kept in a slot, and only then the writes
        var calls = Indexes(lines, "call i32 Program.Next");
        var stores = Indexes(lines, "store $part_");
        var writes = Indexes(lines, "call void ChipSharp.");
        Assert.Equal(5, calls.Count);
        Assert.Equal(5, stores.Count);
        Assert.True(stores[1] < writes[0] && writes[0] < calls[2], string.Join('\n', lines));
        Assert.True(stores[4] < writes.First(write => write > calls[2]), string.Join('\n', lines));
    }

    [Fact]
    public void A_part_that_comes_in_on_several_ways_waits_in_a_stack_slot_with_the_text()
    {
        // "lane " + lane + (cut ? " cut" : ""): lane.ToString() is kept before the branch, the text stands on the
        // stack (a null ptr in a stack slot, which nothing reads; as null it goes into the ref slot too, where an object
        // could meet it) while the third part, a string, comes in on two ways
        var main = Lowered.Function("""
            using System;

            class Program
            {
                static void Main()
                {
                    int lane = 2;
                    bool cut = lane > 1;
                    Console.WriteLine("lane " + lane + (cut ? " cut" : ""));
                }
            }
            """, "Program.Main");

        Assert.Equal("""
            function void Program.Main()
              $V_0: i32
              $V_1: bool
              $part_IL_000e: i32
              $stack1_ptr: ptr
              $stack0_ref: ref
              $stack1_ref: ref
              $stack2_ref: ref
              $part_IL_0022_2: ref
            IL_0000:
              %0 = const i32 2
              store $V_0, %0
              %1 = load i32 $V_0
              %2 = const i32 1
              %3 = gt i32 %1, %2
              store $V_1, %3
              %4 = addr @<text 0>
              %5 = addr $V_0
              %6 = load i32 [%5]
              store $part_IL_000e, %6
              %7 = load bool $V_1
              %8 = zero ptr
              %9 = zero ref
              store $stack0_ref, %4
              store $stack1_ptr, %8
              store $stack1_ref, %9
              branch %7, IL_001d, IL_0016
            IL_0016:
              %10 = addr @<text 1>
              %11 = zero ptr
              %12 = zero ref
              store $stack1_ptr, %11
              store $stack1_ref, %12
              store $stack2_ref, %10
              jump IL_0022
            IL_001d:
              %13 = addr @<text 2>
              %14 = zero ptr
              %15 = zero ref
              store $stack1_ptr, %14
              store $stack1_ref, %15
              store $stack2_ref, %13
              jump IL_0022
            IL_0022:
              %16 = load ref $stack2_ref
              store $part_IL_0022_2, %16
              %17 = addr @<text 0>
              call void ChipSharp.Runtime.WriteString(%17)
              %18 = load i32 $part_IL_000e
              call void ChipSharp.Runtime.WriteInt32(%18)
              %19 = load ref $part_IL_0022_2
              call void ChipSharp.Runtime.WriteLineString(%19)
              return
            """.ReplaceLineEndings("\n"), main.TrimEnd().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Aligned_and_formatted_parts_are_written_by_the_text_functions()
    {
        // a hex number's bits of its own width (FF for an sbyte -1), a signed number made 64 bits, an unsigned one
        // zero-extended; the runtime writes the hex digits
        var module = Lowered.Module("""
            using System;

            class Program
            {
                static string name = "east";

                static void Main()
                {
                    sbyte small = -1;
                    int x = -42;
                    uint u = 7;
                    bool on = true;
                    Console.WriteLine($"{small:X} {x,6} {x:D4} {u,-4:d2} {x:x} {name,6} {on,-6}");
                }
            }
            """);
        var main = TirPrinter.Print(module.Functions.Single(f => f.Name == "Program.Main"));

        Assert.Equal(
        [
            "ChipSharp.Text.Digits", "ChipSharp.Text.HexDigits", "ChipSharp.Text.Length", "ChipSharp.Text.Repeat",
            "ChipSharp.Text.WriteAligned", "ChipSharp.Text.WriteBool", "ChipSharp.Text.WriteDecimal",
            "ChipSharp.Text.WriteHex", "ChipSharp.Text.WriteSigned",
        ], module.Functions.Select(f => f.Name).Where(name => name.StartsWith("ChipSharp.Text.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal));
        Assert.Contains("and i32 %", main);
        Assert.Contains("call void ChipSharp.Text.WriteHex(", main);
        Assert.Contains("call void ChipSharp.Text.WriteSigned(", main);
        Assert.Contains("call void ChipSharp.Text.WriteDecimal(", main);
        Assert.Contains("call void ChipSharp.Text.WriteAligned(", main);
        Assert.Contains("call void ChipSharp.Text.WriteBool(", main);
        Assert.Contains(module.Functions, function => function.Name == "ChipSharp.Runtime.WriteHexDigits");
    }

    [Fact]
    public void Built_text_that_is_kept_is_a_string_made_where_the_text_is_finished()
    {
        // the parts of the text wait in their slots, then the runtime puts their strings together
        var module = IlToTir.Lower(Lowered.Program(Lowered.Compile("""
            using System;

            class Program
            {
                static string Make(int lane) => $"road : {lane}";

                static void Main() => Console.WriteLine(Make(1));
            }
            """)));
        var make = TirPrinter.Print(module.Functions.Single(function => function.Name == "Program.Make"));

        Assert.Contains("$parts: [2 x ref]", make);
        Assert.Contains("call ref ChipSharp.Runtime.StringOfInt64(", make);
        Assert.Contains("call ref ChipSharp.Runtime.StringConcat(", make);
        Assert.True(module.Functions.Single(function => function.Name == "ChipSharp.Runtime.StringConcat").MakesObjects);
    }

    /// <summary>The indexes of the lines that contain <paramref name="text"/>.</summary>
    private static List<int> Indexes(string[] lines, string text) =>
        [.. lines.Select((line, index) => (line, index)).Where(pair => pair.line.Contains(text, StringComparison.Ordinal))
            .Select(pair => pair.index)];
}
