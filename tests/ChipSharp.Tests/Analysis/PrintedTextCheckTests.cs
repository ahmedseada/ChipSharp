using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;
using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// Printed text in the whole-program pass: text built with $"...", + and ToString and printed straight away compiles,
/// in Release and in Debug, on any target with a console, a heap or not; text that is kept (in a variable or a field,
/// returned, passed on, measured, chosen where ways join) needs the heap; and what a hole cannot be yet says what to
/// write instead.
/// </summary>
public sealed class PrintedTextCheckTests : IDisposable
{
    private const string Kept = "keeping built text in a variable arrives with M3 (the heap). For now, print it straight "
        + "away: Console.WriteLine($\"...\").";

    private const string Formats = "For now, write whole numbers with D, D<n>, X or X<n> (x for lowercase hex digits), "
        + "or leave the format out.";

    /// <summary>Every form C# makes for printed text, in Release and in Debug.</summary>
    private const string Printed = """
        using System;

        struct Point
        {
            public int X;
        }

        class Program
        {
            static int calls;

            static int Next() => ++calls;

            static string Name(int road) => road == 1 ? "north" : null;

            static void Main()
            {
                int lane = 2;
                bool cut = lane > 1;
                Point point = new Point { X = 3 };
                int[] values = { 4, 5 };
                Console.WriteLine($"road : {lane}");
                Console.Write($"    {Name(1)} {-lane}");
                Console.WriteLine("road : " + lane);
                Console.WriteLine("lane " + lane + (cut ? " cut" : "") + Name(2) + 1.5f + 2.5 + 3L + 4u + 5UL + true);
                Console.WriteLine($"{Next()} {Next():D3} {lane,5} {lane,-5:X4} {Name(1),8} {cut,-6} {0.1}");
                Console.WriteLine($"{Name(1)}{Name(2)}{Name(1)}{Name(2)}{Name(1)}");
                Console.WriteLine("" + lane);
                Console.WriteLine(lane.ToString() + lane.ToString("x2") + cut.ToString() + point.X + values[1]);
                Console.WriteLine($"{lane}" + "!" + $"{(cut ? 1 : 2)} {lane switch { 1 => 10, _ => 20 }}");
                Console.WriteLine("lane " + lane + (lane switch { 1 => " one", _ => " more" }));
                string text = $"road : {lane}";
                Console.WriteLine(text);
            }
        }
        """;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(OptimizationLevel.Release)]
    [InlineData(OptimizationLevel.Debug)]
    public void Text_printed_straight_away_compiles(OptimizationLevel optimization)
    {
        var analysis = WholeProgramPass.Check(Load(Printed, optimization));

        Assert.True(analysis.IsSupported, analysis.Details);
        Assert.Equal(["Program.Main", "Program.Name", "Program.Next"], analysis.Methods.Select(m => m.DisplayName));
    }

    [Fact]
    public void Built_text_that_is_kept_needs_the_heap_where_it_is_kept()
    {
        // stored in a static field, passed to a method of the program, returned, measured, chosen where ways join,
        // compared: only Console.Write and Console.WriteLine of it straight away print it
        var problems = Problems("""
            using System;

            class Program
            {
                static string field;

                static string Make(int lane) => $"road : {lane}";

                static void Show(string text) => Console.WriteLine(text);

                static void Main()
                {
                    int lane = 2;
                    bool cut = lane > 1;
                    field = "lane " + lane;
                    Show($"road : {lane}");
                    Console.WriteLine(Make(lane));
                    Console.WriteLine(("lane " + lane).Length);
                    Console.WriteLine(cut ? $"road : {lane}" : "none");
                    Console.WriteLine(lane.ToString() == "2");
                }
            }
            """);

        Assert.Equal(
        [
            $"Program.Main() at IL_0017 (stsfld): {Kept}",
            $"Program.Main() at IL_0040 (call): {Kept}",
            $"Program.Main() at IL_0061 (callvirt): {Kept}",
            $"Program.Main() at IL_0098 (call): {Kept}",
            $"Program.Main() at IL_00a9 (call): {Kept}",
            $"Program.Make(int) at IL_0024 (ret): {Kept}",
        ], problems);
    }

    [Theory]
    [InlineData(OptimizationLevel.Release, "IL_0026 (stloc.1)")]
    [InlineData(OptimizationLevel.Debug, "IL_0029 (stloc.1)")]
    public void A_variable_read_more_than_once_keeps_the_text(OptimizationLevel optimization, string place)
    {
        // read once, the variable is a temporary like those C# makes (around a switch expression, and in Debug for
        // this one, which Release leaves out): the text is printed straight away (see Text_printed_straight_away_compiles)
        var analysis = WholeProgramPass.Check(Load("""
            using System;

            class Program
            {
                static void Main()
                {
                    int lane = 2;
                    string text = $"road : {lane}";
                    if (lane > 1)
                    {
                        Console.WriteLine(text);
                    }
                    else
                    {
                        Console.Write(text);
                    }
                }
            }
            """, optimization));

        Assert.Equal([$"Program.Main() at {place}: {Kept}"], analysis.Problems.Select(problem => problem.Line));
    }

    [Fact]
    public void A_hole_that_cannot_be_printed_yet_says_what_to_write_instead()
    {
        // an enum (.NET prints its name), a struct, a float with a format or an alignment, the formats of whole numbers
        // that are not D, X or G, a char, a nint
        var problems = Problems("""
            using System;

            enum Color { Red, Green }

            struct Point
            {
                public int X;
            }

            class Program
            {
                static void Main()
                {
                    Color color = Color.Green;
                    Point point = default;
                    float f = 1.5f;
                    double d = 2.5;
                    int n = 3;
                    nint size = 4;
                    Console.WriteLine($"{color}");
                    Console.WriteLine("color " + color);
                    Console.WriteLine($"{point}");
                    Console.WriteLine($"{f:F2} {d,8}");
                    Console.WriteLine($"{n:N0} {n:C}");
                    Console.WriteLine(n.ToString("E3"));
                    Console.WriteLine($"{'c'}");
                    Console.WriteLine($"{size}");
                }
            }
            """);

        const string Enums = "enum names arrive with M3 (the heap). For now, print the number: "
            + "Console.WriteLine($\"{(int)color}\").";
        Assert.Equal(
        [
            $"Program.Main() at IL_002d (call): {Enums}",
            $"Program.Main() at IL_0045 (constrained.): {Enums}",
            $"Program.Main() at IL_004b (callvirt): {Enums}",
            "Program.Main() at IL_0066 (call): the text of a Point ($\"{value}\", value.ToString()) arrives with M3 (the "
                + "heap). For now, print the numbers, bools and text it holds one after another.",
            $"Program.Main() at IL_0088 (call): the format \"F2\" in printed text arrives later. {Formats}",
            "Program.Main() at IL_009d (call): aligning a float or a double ($\"{x,8}\") arrives later. For now, leave "
                + "out the alignment of floats and doubles, or print them on their own.",
            $"Program.Main() at IL_00c0 (call): the format \"N0\" in printed text arrives later. {Formats}",
            $"Program.Main() at IL_00da (call): the format \"C\" in printed text arrives later. {Formats}",
            $"Program.Main() at IL_00f2 (call): the format \"E3\" in printed text arrives later. {Formats}",
            "Program.Main() at IL_0109 (call): characters (char) arrive with M3 (the heap). For now, use whole numbers.",
            "Program.Main() at IL_0127 (call): the text of a nint or nuint ($\"{size}\") arrives later. For now, "
                + "convert it: $\"{(long)size}\".",
        ], problems);
    }

    [Fact]
    public void On_a_target_without_a_heap_printed_text_compiles_and_kept_text_needs_a_heap()
    {
        // printed text needs no heap, only a console; enum names need none either: they still arrive with M3
        var targets = TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""));
        var target = new TargetLoader(targets).Load("pico-no-heap");
        Assert.True(WholeProgramPass.Check(Load(Printed, OptimizationLevel.Release), target).IsSupported);

        var analysis = WholeProgramPass.Check(Load("""
            using System;

            enum Color { Red, Green }

            class Program
            {
                static string Make(int lane) => $"road : {lane}";

                static void Main()
                {
                    Console.WriteLine(Make(1));
                    Console.WriteLine($"{Color.Green}");
                }
            }
            """, OptimizationLevel.Release), target);

        Assert.Equal(
        [
            "Program.Main() at IL_0017 (call): enum names arrive with M3 (the heap). For now, print the number: "
                + "Console.WriteLine($\"{(int)color}\").",
            "Program.Make(int) at IL_0024 (ret): keeping built text in a variable needs a heap, and the target "
                + "'pico-no-heap' (264 KB RAM) has no heap. Print it straight away: Console.WriteLine($\"...\").",
        ], analysis.Problems.Select(problem => problem.Line));
    }

    [Fact]
    public void On_a_target_without_a_console_printed_text_is_a_call_of_Console_like_any_other()
    {
        var targets = TinyChip.Targets(_scratch);
        var analysis = WholeProgramPass.Check(Load("""
            using System;

            class Program
            {
                static void Main()
                {
                    int lane = 2;
                    Console.WriteLine($"road : {lane}");
                    Console.WriteLine("lane " + lane);
                }
            }
            """, OptimizationLevel.Release), new TargetLoader(targets).Load(TinyChip.Name));

        Assert.Equal(
            "Console.Write and Console.WriteLine need a console, and the target 'tiny' has none: 2 places in "
            + "Program.Main() (IL_0026 and IL_003c). Show what the program does another way, e.g. on a pin with "
            + "ChipSharp.Hardware.Pin.",
            Assert.Single(analysis.Groups).Line);
    }

    private static IlAssembly Load(string source, OptimizationLevel optimization) => IlAssembly.Load(
        CSharpCompiler.Compile(source, "Test", OutputKind.ConsoleApplication, optimization), "Test.dll");
}
