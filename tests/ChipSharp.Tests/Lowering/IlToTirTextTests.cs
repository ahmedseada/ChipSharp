using ChipSharp.Frontend;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// The TIR that fixed text becomes (see FixedText): a constant global per distinct literal, holding its record, whose
/// address is the string; null is a null ptr; a static string field is a ptr global; Console.Write and
/// Console.WriteLine of text take the ptr.
/// </summary>
public sealed class IlToTirTextTests
{
    [Fact]
    public void A_literal_is_the_address_of_its_record_one_constant_for_each_distinct_text()
    {
        // "road : " twice is one constant, as .NET interns literals; string.Empty is the literal "", the same one too;
        // a static readonly field is a ptr global that its initializer, run at the start, fills
        var module = Lowered.Module("""
            using System;

            class Program
            {
                static readonly string Name = "road";

                static void Main()
                {
                    Console.Write("road : ");
                    Console.WriteLine(1);
                    Console.Write("road : ");
                    Console.WriteLine(Name);
                    Console.WriteLine(string.Empty);
                    Console.WriteLine("");
                }
            }
            """);

        Assert.Equal("""
            module Test
            entrypoint <Start>

            constant [15 x i8] @<text 0> = c"\07\00\00\00\07\00\00\00road : "
            global ptr @Program.Name
            constant [8 x i8] @<text 1> = c"\00\00\00\00\00\00\00\00"
            constant [12 x i8] @<text 2> = c"\04\00\00\00\04\00\00\00road"

            function void Program.Main()
            IL_0000:
              %0 = addr @<text 0>
              call void ChipSharp.Runtime.WriteString(%0)
              %1 = const i32 1
              call void ChipSharp.Runtime.WriteLineInt32(%1)
              %2 = addr @<text 0>
              call void ChipSharp.Runtime.WriteString(%2)
              %3 = addr @Program.Name
              %4 = load ptr [%3]
              call void ChipSharp.Runtime.WriteLineString(%4)
              %5 = addr @<text 1>
              call void ChipSharp.Runtime.WriteLineString(%5)
              %6 = addr @<text 1>
              call void ChipSharp.Runtime.WriteLineString(%6)
              return

            function void Program..cctor()
            IL_0000:
              %0 = addr @<text 2>
              %1 = addr @Program.Name
              store ptr [%1], %0
              return

            external function void ChipSharp.Runtime.WriteString(ptr %value)

            external function void ChipSharp.Runtime.WriteLineInt32(i32 %value)

            external function void ChipSharp.Runtime.WriteLineString(ptr %value)

            function void <Start>()
            entry:
              call void Program..cctor()
              call void Program.Main()
              return

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module));
    }

    [Fact]
    public void Null_is_a_null_ptr_and_text_compares_with_it_as_an_address()
    {
        const string Source = """
            static class C
            {
                static string Pick(bool b) => b ? "x" : null;
                static bool IsSet(string s) => s != null;
                static string OrNone(string s) => s ?? "none";
            }
            """;

        Assert.Equal("""
            function ptr C.Pick(bool %b)
            IL_0000:
              branch %b, IL_0005, IL_0003
            IL_0003:
              %0 = zero ptr
              return %0
            IL_0005:
              %1 = addr @<text 0>
              return %1

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.Pick"));

        // s != null is cgt.un: an address above null
        Assert.Equal("""
            function bool C.IsSet(ptr %s)
            IL_0000:
              %0 = zero ptr
              %1 = ugt ptr %s, %0
              return %1

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.IsSet"));

        // ?? is dup and brtrue: the text waits in a stack slot while it is tested
        Assert.Equal("""
            function ptr C.OrNone(ptr %s)
              $stack0_ptr: ptr
            IL_0000:
              %0 = zero ptr
              %1 = ne ptr %s, %0
              store $stack0_ptr, %s
              branch %1, IL_000a, IL_0004
            IL_0004:
              %2 = addr @<text 0>
              store $stack0_ptr, %2
              jump IL_000a
            IL_000a:
              %3 = load ptr $stack0_ptr
              return %3

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.OrNone"));
    }

    [Fact]
    public void A_literal_holds_one_byte_per_char_below_U0100_and_else_its_UTF16_code_units()
    {
        // "caf\u00e9" is Latin-1: 4 chars, a byte each; a smiley is 2 UTF-16 code units (a surrogate pair), little-endian,
        // and so is a lone surrogate, as it is
        Assert.True(Frontend.Strings.IsNarrow("caf\u00e9"));
        Assert.Equal([(byte)'c', (byte)'a', (byte)'f', 0xE9], Frontend.Strings.Elements("caf\u00e9"));
        Assert.False(Frontend.Strings.IsNarrow("\U0001F642"));
        Assert.Equal([0x3D, 0xD8, 0x42, 0xDE], Frontend.Strings.Elements("\U0001F642"));
        Assert.Equal([0x00, 0xD8], Frontend.Strings.Elements("\uD800"));
        Assert.Equal([0xAC, 0x20, (byte)'5', 0], Frontend.Strings.Elements("\u20ac5"));
        Assert.Equal([0], Frontend.Strings.Elements("\0"));
        Assert.Empty(Frontend.Strings.Elements(""));
        Assert.True(Frontend.Strings.IsNarrow(""));
    }

    [Fact]
    public void Text_becomes_valid_LLVM_for_the_PC_and_the_Pico()
    {
        var module = Lowered.Module("""
            class Program
            {
                static void Main() => System.Console.WriteLine("caf\u00e9");
            }
            """);

        var host = Assert.Single(new ChipSharp.Backend.Llvm.LlvmCodeGenerator().Generate(module, Llvm.LlvmTargets.Host).Files);
        var pico = Assert.Single(new ChipSharp.Backend.Llvm.LlvmCodeGenerator().Generate(module, Llvm.LlvmTargets.Pico).Files);

        const string Constant = """@_text_0_ = private unnamed_addr constant [13 x i8] c"\04\00\00\00\05\00\00\00caf\C3\A9", align 1""";
        Assert.Contains(Constant, host.Content);
        Assert.Contains(Constant, pico.Content);
        Assert.Contains("call void @ChipSharp_Runtime_WriteLineString(ptr @_text_0_)", host.Content);
        Assert.Contains("declare void @ChipSharp_Runtime_WriteLineString(ptr) nounwind", pico.Content);
    }
}
