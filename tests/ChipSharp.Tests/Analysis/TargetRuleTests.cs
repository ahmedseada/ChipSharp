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
        // the class itself compiles (its fields and methods too); making an object of it takes the heap
        Assert.Equal(
        [
            "Program.Main() at IL_0000 (newobj): creating objects (new ...) needs a heap, and the target 'tiny' (2 KB RAM) " +
                "has no heap. Use structs and static methods.",
        ], Problems(Objects, Tiny));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pico")]
    [InlineData("host")]
    public void On_a_target_with_a_heap_and_without_one_objects_compile(string? target)
    {
        var analysis = Check(Objects, target is null ? null : Shipped.Load(target));

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void A_feature_of_objects_still_to_come_needs_a_heap_too()
    {
        // an object's ToString compiles where there is a heap (it may make a string); without one no object is ever made,
        // so a call of it, like one of its virtual methods, is only its null check
        const string ToText = """
            class Program
            {
                static int count;
                static bool same;

                static void Main()
                {
                    var counter = new Counter();
                    count = counter.Count();
                    same = counter.ToString() == null;
                }
            }

            class Counter
            {
                public virtual int Count() => 3;
            }
            """;

        Assert.Equal(
        [
            "Program.Main() at IL_0000 (newobj): creating objects (new ...) needs a heap, and the target 'tiny' (2 KB RAM) " +
                "has no heap. Use structs and static methods.",
        ], Problems(ToText, Tiny));
        Assert.True(Check(ToText, Shipped.Load("pico")).IsSupported);
    }

    [Fact]
    public void The_summary_for_a_target_without_a_heap_says_so_and_asks_for_the_details_for_that_target()
    {
        var analysis = Check(Objects, Tiny);

        Assert.Equal("""
            Test.dll uses 1 C# feature that ChipSharp does not compile yet (1 place):
              creating objects (new ...) needs a heap, and the target 'tiny' (2 KB RAM) has no heap: 1 place in Program.Main() (IL_0000). Use structs and static methods.
            For every place, run: chipsharp check Test.dll -t tiny --details
            """.ReplaceLineEndings("\n"), analysis.Summary);
        Assert.Equal("", Check(Objects, Shipped.Load("pico")).Summary);
    }

    [Fact]
    public void A_reference_to_text_needs_no_heap()
    {
        var problems = Problems("""
            class Program
            {
                static int result;

                static void Main()
                {
                    result = Code(out var text);
                }

                static int Code(out string text)
                {
                    text = "a";
                    return 1;
                }
            }
            """, Tiny);

        // an out string is the address of a variable that holds a string (ObjectReferences), on any target
        Assert.Empty(problems);
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

        Assert.Equal(2, problems.Length);
        Assert.StartsWith("Program.Main() at IL_0000 (newobj): creating objects (new ...) needs a heap", problems[0]);
        Assert.StartsWith("Program.Main() at IL_000a (call): Console.Write and Console.WriteLine need a console", problems[1]);
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
