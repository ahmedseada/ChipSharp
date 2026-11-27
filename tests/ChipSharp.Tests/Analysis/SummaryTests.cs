using ChipSharp.Frontend;
using Calls = ChipSharp.Frontend.Analysis.LibraryCalls;
using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// The summary the error gives: one line per feature with how many places, where, and the hint once; each library
/// method a feature of its own, and the library calls there are listed once, at the end.
/// </summary>
public sealed class SummaryTests
{
    private const string Randoms = "the type System.Random arrives later";
    private const string RandomsHint = "For now, use bool, the whole-number types (sbyte to ulong, nint, nuint), float, double, " +
        "string (for fixed text), and the program's own structs and enums.";
    private const string Checked = "For now, leave out checked: ChipSharp's arithmetic wraps around on overflow, like unchecked C#.";

    [Fact]
    public void Several_places_of_one_feature_are_one_entry_with_their_count()
    {
        var analysis = Check("""
            using System;

            class Program
            {
                static Random counter;

                static void Main()
                {
                    counter = null;
                    Console.WriteLine(counter == null);
                    Console.WriteLine(counter != null);
                }
            }
            """);

        var group = Assert.Single(analysis.Groups);
        Assert.Equal("the type System.Random", group.Feature);
        Assert.Equal(3, group.Problems.Count);
        Assert.Equal(["Program.Main"], group.Methods.Select(method => method.DisplayName));
        Assert.False(group.IsLibraryCall);
        Assert.Equal($"""
            Test.dll uses 1 C# feature that ChipSharp does not compile yet (3 places):
              {Randoms}: 3 places in Program.Main() (IL_0001, IL_0006 and IL_0013). {RandomsHint}
            For every place, run: chipsharp check Test.dll --details
            """.ReplaceLineEndings("\n"), analysis.Summary);
    }

    [Fact]
    public void Features_are_in_the_order_they_first_appear_and_name_every_method()
    {
        // the methods are checked in the order Main calls them: First, Second, Third
        var analysis = Check("""
            using System;

            class Program
            {
                static int Third(int x) => checked(x * 3);
                static int Second() => new Random().Next();
                static int First(int x) => checked(x + 1);

                static void Main()
                {
                    Console.WriteLine(First(1) + Second() + Third(2));
                }
            }
            """);

        Assert.Equal(["checked arithmetic", "the library type System.Random", "the library method System.Random.Next()"],
            analysis.Groups.Select(group => group.Feature));
        Assert.Equal(["Program.First", "Program.Third"], analysis.Groups[0].Methods.Select(method => method.DisplayName));
        Assert.Equal(
        [
            $"checked arithmetic arrives later: 2 places in Program.First(int) (IL_0002) and Program.Third(int) (IL_0002). {Checked}",
            "the library type System.Random is not available yet: 1 place in Program.Second() (IL_0000).",
            "the library method System.Random.Next() is not available yet: 1 place in Program.Second() (IL_0005).",
        ], analysis.Groups.Select(group => group.Line));
    }

    [Fact]
    public void Past_a_few_places_or_methods_a_line_says_how_many_more()
    {
        var analysis = Check("""
            using System;

            class Program
            {
                static Random a, b, c, d, e;

                static int One() => a == null ? 1 : 0;
                static int Two() => b == null ? 1 : 0;
                static int Three() => c == null ? 1 : 0;
                static int Four() => d == null ? 1 : 0;
                static int Five() => e == null ? 1 : 0;

                static void Main()
                {
                    a = null;
                    b = null;
                    c = null;
                    d = null;
                    e = null;
                    Console.WriteLine(One() + Two() + Three() + Four() + Five());
                }
            }
            """);

        var group = Assert.Single(analysis.Groups);
        Assert.Equal(10, group.Problems.Count);
        Assert.Equal($"{Randoms}: 10 places in Program.Main() (IL_0001, IL_0007, IL_000d and 2 more), Program.One() (IL_0000), "
            + $"Program.Two() (IL_0000) and 3 more methods. {RandomsHint}",
            group.Line);
    }

    [Fact]
    public void Four_places_or_methods_are_all_named()
    {
        var analysis = Check("""
            using System;

            class Program
            {
                static Random a;

                static int One() => a == null ? 1 : 0;
                static int Two() => a == null ? 1 : 0;
                static int Three() => a == null ? 1 : 0;

                static void Main()
                {
                    a = null;
                    a = a ?? a;
                    Console.WriteLine(One() + Two() + Three());
                }
            }
            """);

        Assert.Equal($"{Randoms}: 7 places in Program.Main() (IL_0001, IL_0006, IL_000f and IL_0014), Program.One() (IL_0000), "
            + $"Program.Two() (IL_0000) and Program.Three() (IL_0000). {RandomsHint}",
            Assert.Single(analysis.Groups).Line);
    }

    [Fact]
    public void Every_library_method_is_a_feature_of_its_own_and_the_library_calls_there_are_come_once_at_the_end()
    {
        var analysis = Check("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(int.Abs(-3));
                    Console.WriteLine(int.Max(1, 2));
                    Console.WriteLine(int.Abs(-4));
                }
            }
            """);

        Assert.Equal(["the library method int.Abs(int)", "the library method int.Max(int, int)"],
            analysis.Groups.Select(group => group.Feature));
        Assert.All(analysis.Groups, group => Assert.True(group.IsLibraryCall));
        Assert.Equal([2, 1], analysis.Groups.Select(group => group.Problems.Count));
        Assert.Equal($"""
            Test.dll uses 2 C# features that ChipSharp does not compile yet (3 places):
              the library method int.Abs(int) is not available yet: 2 places in Program.Main() (IL_0002 and IL_001a).
              the library method int.Max(int, int) is not available yet: 1 place in Program.Main() (IL_000e).
            {LibraryCalls}
            For every place, run: chipsharp check Test.dll --details
            """.ReplaceLineEndings("\n"), analysis.Summary);
    }

    [Fact]
    public void Top_level_statements_and_local_functions_go_by_their_CSharp_names_and_the_details_keep_the_real_ones()
    {
        var analysis = Check("""
            using System;

            static int Twice(int x) => checked(x * 2);

            Console.WriteLine(Twice(21));
            Console.WriteLine(new Random().Next() + Twice(1));
            """);

        Assert.Equal(
        [
            "the library type System.Random is not available yet: 1 place in the top-level statements (IL_000c).",
            "the library method System.Random.Next() is not available yet: 1 place in the top-level statements (IL_0011).",
            $"checked arithmetic arrives later: 1 place in local function Twice (IL_0002). {Checked}",
        ], analysis.Groups.Select(group => group.Line).Take(3));
        Assert.Contains("\n  Program.<Main>$(string[]) at IL_0011 (callvirt): the library method System.Random.Next() is not " +
            "available yet.", analysis.Details);
        Assert.Contains("\n  Program.<<Main>$>g__Twice|0_0(int) at IL_0002 (mul.ovf): checked arithmetic arrives later.",
            analysis.Details);
    }

    [Fact]
    public void Types_M0_has_no_words_for_are_named_one_by_one()
    {
        var analysis = Check("""
            using System;

            class Program
            {
                static int Next(Random random, Guid id, Random other) => 0;

                static void Main() => Console.WriteLine(Next(null, default, null));
            }
            """);

        var types = analysis.Groups.Where(group => group.Feature.StartsWith("the type ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["the type System.Guid", "the type System.Random"], types.Select(group => group.Feature));
        Assert.Equal("the type System.Random arrives later: 2 places in Program.Next(System.Random, System.Guid, "
            + "System.Random) (parameter 'random' and parameter 'other'). For now, use bool, the whole-number types "
            + "(sbyte to ulong, nint, nuint), float, double, string (for fixed text), and the program's own structs and "
            + "enums.", types[1].Line);
    }

    [Fact]
    public void A_SquareDemo_like_program_gets_a_short_summary()
    {
        // top-level statements, float arrays and local functions, and a $"..." that one of them returns: kept text, a
        // string on the heap, fine for a target with one (and checked without a target); see StringCheckTests for one
        // without a heap
        var analysis = Check("""
            using System;

            static float Logit(float[] weights, float[] features)
            {
                return (weights[0] * features[0]) + (weights[1] * features[1]);
            }

            static string Report(float result) => $"Logit result: {result}";

            float[] weights = { 0.5f, -0.25f };
            float[] features = { 1.0f, 2.0f };

            float result = Logit(weights, features);
            Console.WriteLine(Report(result));
            """);

        Assert.True(analysis.IsSupported);
        Assert.Equal("", analysis.Summary);
    }

    [Fact]
    public void A_supported_program_has_no_summary()
    {
        var analysis = Check("""
            class Program
            {
                static void Main() => System.Console.WriteLine(42);
            }
            """);

        Assert.True(analysis.IsSupported);
        Assert.Empty(analysis.Problems);
        Assert.Empty(analysis.Groups);
        Assert.Equal("", analysis.Summary);
        Assert.Equal("", analysis.Details);
        Assert.Equal(["Program.Main"], analysis.ToProgram().Methods.Select(method => method.DisplayName));
    }

    [Fact]
    public void Methods_of_one_type_that_take_the_same_parameters_share_them_and_text_is_called_text()
    {
        // Write takes what WriteLine takes, so they share; Beep takes other parameters than Write, so names do
        string[] shared =
        [
            "System.Console.Write(string)", "System.Console.Write(int)", "System.Console.WriteLine()",
            "System.Console.WriteLine(string)", "System.Console.WriteLine(int)",
        ];
        string[] apart = ["System.Console.Write(string)", "System.Console.Beep(int, int)"];

        Assert.Equal("""
            ChipSharp can call these library methods (--details lists each one with its parameters):
              Console.Write and Console.WriteLine of (nothing), text, int
            """.ReplaceLineEndings("\n"), Calls.Summarize(shared));
        Assert.Equal("""
            ChipSharp can call these library methods (--details lists each one with its parameters):
              Console: Write, Beep
            """.ReplaceLineEndings("\n"), Calls.Summarize(apart));
        Assert.Contains("\n  Console.Write and Console.WriteLine of (nothing), text, char, int, uint, long, ulong, bool, "
            + "float, double\n", Calls.Summary);
    }

    [Fact]
    public void The_library_calls_come_from_the_known_methods_so_a_new_one_shows_up_on_its_own()
    {
        string[] methods =
        [
            "System.Console.WriteLine()", "System.Console.WriteLine(int)", "System.Math.Abs(double)",
            "System.Math.Clamp(int, int, int)", "System.MathF.Abs(float)", "System.TimeSpan.FromMilliseconds(double)",
            "System.TimeSpan.FromSeconds(double)", "System.TimeSpan.Ticks", "System.TimeSpan.new TimeSpan(long)",
            "System.TimeSpan.operator <(System.TimeSpan, System.TimeSpan)", "ChipSharp.Hardware.Pin.Toggle(int)",
            "ChipSharp.Hardware.Timing.Delay(System.TimeSpan)",
        ];

        Assert.Equal("""
            ChipSharp can call these library methods (--details lists each one with its parameters):
              Console.WriteLine of (nothing), int
              Math: Abs, Clamp
              MathF.Abs of float
              TimeSpan: FromMilliseconds, FromSeconds, its constructors, properties and operators
              ChipSharp.Hardware: Pin, Timing (see the README)
            """.ReplaceLineEndings("\n"), Calls.Summarize(methods));
        Assert.Equal("""
            ChipSharp can call these library methods:
              System.Console: WriteLine(), WriteLine(int)
              System.Math: Abs(double), Clamp(int, int, int)
              System.MathF: Abs(float)
              System.TimeSpan: FromMilliseconds(double), FromSeconds(double), Ticks, new TimeSpan(long), operator <(System.TimeSpan, System.TimeSpan)
              ChipSharp.Hardware.Pin: Toggle(int)
              ChipSharp.Hardware.Timing: Delay(System.TimeSpan)
            """.ReplaceLineEndings("\n"), Calls.List(methods));
        Assert.Equal(
            Calls.Summarize(KnownExternals.All.Select(function => function.LibraryMethod).Concat(ArrayMethods.Members)
                .Concat(LibrarySubstitutes.Members).Concat(Spans.Members).Concat(StringMethods.Members)
                .OrderBy(method => method.StartsWith("ChipSharp.", StringComparison.Ordinal))),
            Calls.Summary);
    }
}
