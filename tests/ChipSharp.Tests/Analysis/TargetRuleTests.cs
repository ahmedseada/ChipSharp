using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// The whole-program pass for a target: what a program may use depends on the target's capabilities. The made-up chip
/// "tiny" (see <see cref="TinyChip"/>, 2 KB of RAM) has no heap and no console; the Pico and the PC have both; checked
/// without a target, a program may use what any target has.
/// </summary>
public sealed class TargetRuleTests : IDisposable
{
    /// <summary>A class: an object made with new, and its field read.</summary>
    private const string Objects = """
        class Program
        {
            static int count;

            static void Main()
            {
                count = new Counter().Count;
            }
        }

        class Counter
        {
            public int Count = 3;
        }
        """;

    private const string Printing = """
        using System;

        class Program
        {
            static void Main()
            {
                Console.Write("answer: ");
                Console.WriteLine(42);
            }
        }
        """;

    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private TargetDescription Tiny => new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name);

    [Fact]
    public void On_a_target_without_a_heap_objects_need_a_heap_the_target_does_not_have()
    {
        Assert.Equal(
        [
            "Program.Main() at IL_0000 (newobj): creating objects (new ...) needs a heap, and the target 'tiny' (2 KB RAM) " +
                "has no heap. Use structs and static methods.",
            "Program.Main() at IL_0005 (ldfld): instance fields of classes (fields without static) need a heap, and the " +
                "target 'tiny' (2 KB RAM) has no heap. Make the fields static, or make the class a struct.",
        ], Problems(Objects, Tiny));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pico")]
    [InlineData("host")]
    public void On_a_target_with_a_heap_and_without_one_objects_arrive_with_M3(string? target)
    {
        Assert.Equal(
        [
            "Program.Main() at IL_0000 (newobj): creating objects (new ...) arrives with M3 (the heap). For now, use " +
                "structs and static methods.",
            "Program.Main() at IL_0005 (ldfld): instance fields of classes (fields without static) arrive with M3 (the " +
                "heap). For now, make the fields static, or make the class a struct.",
        ], Problems(Objects, target is null ? null : Shipped.Load(target)));
    }

    [Fact]
    public void The_summary_for_a_target_without_a_heap_says_so_and_asks_for_the_details_for_that_target()
    {
        var analysis = Check(Objects, Tiny);

        Assert.Equal("""
            Test.dll uses 2 C# features that ChipSharp does not compile yet (2 places):
              creating objects (new ...) needs a heap, and the target 'tiny' (2 KB RAM) has no heap: 1 place in Program.Main() (IL_0000). Use structs and static methods.
              instance fields of classes (fields without static) need a heap, and the target 'tiny' (2 KB RAM) has no heap: 1 place in Program.Main() (IL_0005). Make the fields static, or make the class a struct.
            For every place, run: chipsharp check Test.dll -t tiny --details
            """.ReplaceLineEndings("\n"), analysis.Summary);
        Assert.EndsWith("For every place, run: chipsharp check Test.dll --details", Check(Objects, Shipped.Load("pico")).Summary);
    }

    [Fact]
    public void Features_that_arrive_with_M3_but_need_no_heap_arrive_on_a_target_without_one_too()
    {
        var problems = Problems("""
            class Program
            {
                static int result;

                static void Main()
                {
                    result = Code('a');
                }

                static int Code(char letter) => letter;
            }
            """, Tiny);

        Assert.Equal(
            ["Program.Code(char), parameter 'letter' (char): characters (char) arrive with M3 (the heap). For now, use " +
                "whole numbers."],
            problems);
    }

    [Fact]
    public void On_a_target_without_a_console_every_Console_call_is_a_problem()
    {
        var analysis = Check(Printing, Tiny);

        Assert.Equal(
        [
            "Program.Main() at IL_0005 (call): Console.Write and Console.WriteLine need a console, and the target 'tiny' " +
                "has none. Show what the program does another way, e.g. on a pin with ChipSharp.Hardware.Pin.",
            "Program.Main() at IL_000c (call): Console.Write and Console.WriteLine need a console, and the target 'tiny' " +
                "has none. Show what the program does another way, e.g. on a pin with ChipSharp.Hardware.Pin.",
        ], analysis.Problems.Select(problem => problem.Line));
        Assert.Equal(
            "Console.Write and Console.WriteLine need a console, and the target 'tiny' has none: 2 places in " +
            "Program.Main() (IL_0005 and IL_000c). Show what the program does another way, e.g. on a pin with " +
            "ChipSharp.Hardware.Pin.",
            Assert.Single(analysis.Groups).Line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pico")]
    [InlineData("host")]
    public void On_a_target_with_a_console_and_without_a_target_Console_calls_compile(string? target)
    {
        Assert.True(Check(Printing, target is null ? null : Shipped.Load(target)).IsSupported);
    }

    [Fact]
    public void The_calls_of_Console_come_after_the_problems_of_the_methods()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(new Counter().Count);
                }
            }

            class Counter
            {
                public int Count = 3;
            }
            """, Tiny);

        Assert.Equal(3, problems.Length);
        Assert.StartsWith("Program.Main() at IL_0000 (newobj): creating objects (new ...) needs a heap", problems[0]);
        Assert.StartsWith("Program.Main() at IL_0005 (ldfld): instance fields of classes", problems[1]);
        Assert.StartsWith("Program.Main() at IL_000a (call): Console.Write and Console.WriteLine need a console", problems[2]);
    }

    [Fact]
    public void A_library_method_of_Console_that_ChipSharp_does_not_have_is_only_not_available()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static void Main() => Console.Beep();
            }
            """, Tiny);

        Assert.Equal(["Program.Main() at IL_0000 (call): the library method System.Console.Beep() is not available yet."],
            problems);
    }

    [Fact]
    public void The_rules_of_interrupt_handlers_still_hold_for_the_target()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load(InterruptCheckTests.BlinkSource), Tiny);

        Assert.Equal(
            "Program.Toggle(): [Interrupt] methods need interrupts, and target 'tiny' has none. Interrupts are a chip's, " +
            "not a PC's: build the program for a chip, e.g. with -t pico.",
            Assert.Single(analysis.Problems).Line);
        Assert.EndsWith("For every place, run: chipsharp check Test.dll --details", analysis.Summary);
    }

    [Fact]
    public void A_program_that_needs_neither_compiles_for_the_target()
    {
        var analysis = Check("""
            class Program
            {
                static int total;

                static void Main()
                {
                    for (var i = 1; i <= 10; i++)
                    {
                        total += i;
                    }
                }
            }
            """, Tiny);

        Assert.True(analysis.IsSupported);
        Assert.Equal(["Program.Main"], analysis.Methods.Select(method => method.DisplayName));
    }

    private static AnalysisResult Check(string source, TargetDescription? target) =>
        WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile(source), "Test.dll"), target);

    /// <summary>The problems one per place, as the details list them.</summary>
    private static string[] Problems(string source, TargetDescription? target)
    {
        var analysis = Check(source, target);
        Assert.False(analysis.IsSupported);
        return [.. analysis.Problems.Select(problem => problem.Line)];
    }
}
