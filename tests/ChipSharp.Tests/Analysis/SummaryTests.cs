using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// The summary the error gives: one line per feature with how many places, where, and the hint once; each library
/// method a feature of its own, and the library calls there are listed once, at the end.
/// </summary>
public sealed class SummaryTests
{
    private const string Fields = "fields arrive after M0";
    private const string Checked = "For now, leave out checked: M0 arithmetic wraps around on overflow, like unchecked C#.";

    [Fact]
    public void Several_places_of_one_feature_are_one_entry_with_their_count()
    {
        var analysis = Check("""
            using System;

            class Program
            {
                static int counter;

                static void Main()
                {
                    counter = counter + 1;
                    Console.WriteLine(counter);
                }
            }
            """);

        var group = Assert.Single(analysis.Groups);
        Assert.Equal("fields", group.Feature);
        Assert.Equal(3, group.Problems.Count);
        Assert.Equal(["Program.Main"], group.Methods.Select(method => method.DisplayName));
        Assert.False(group.IsLibraryCall);
        Assert.Equal($"""
            Test.dll uses 1 C# feature that M0 does not compile yet (3 places):
              {Fields}: 3 places in Program.Main() (IL_0000, IL_0007 and IL_000c). For now, keep values in local variables and pass them as parameters.
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
                static int Second() => "two".Length;
                static int First(int x) => checked(x + 1);

                static void Main()
                {
                    Console.WriteLine(First(1) + Second() + Third(2));
                }
            }
            """);

        Assert.Equal(["checked arithmetic", "text strings", "the library method string.get_Length()"],
            analysis.Groups.Select(group => group.Feature));
        Assert.Equal(["Program.First", "Program.Third"], analysis.Groups[0].Methods.Select(method => method.DisplayName));
        Assert.Equal(
        [
            $"checked arithmetic arrives after M0: 2 places in Program.First(int) (IL_0002) and Program.Third(int) (IL_0002). {Checked}",
            "text strings arrive after M0: 1 place in Program.Second() (IL_0000). For now, print numbers and bools with Console.WriteLine.",
            "the library method string.get_Length() is not available in M0: 1 place in Program.Second() (IL_0005).",
        ], analysis.Groups.Select(group => group.Line));
    }

    [Fact]
    public void Past_a_few_places_or_methods_a_line_says_how_many_more()
    {
        var analysis = Check("""
            using System;

            class Program
            {
                static int a, b, c, d, e;

                static int One() => a;
                static int Two() => b;
                static int Three() => c;
                static int Four() => d;
                static int Five() => e;

                static void Main()
                {
                    a = 1;
                    b = 2;
                    c = 3;
                    d = 4;
                    e = 5;
                    Console.WriteLine(One() + Two() + Three() + Four() + Five());
                }
            }
            """);

        var group = Assert.Single(analysis.Groups);
        Assert.Equal(10, group.Problems.Count);
        Assert.Equal($"{Fields}: 10 places in Program.Main() (IL_0001, IL_0007, IL_000d and 2 more), Program.One() (IL_0000), "
            + "Program.Two() (IL_0000) and 3 more methods. For now, keep values in local variables and pass them as parameters.",
            group.Line);
    }

    [Fact]
    public void Four_places_or_methods_are_all_named()
    {
        var analysis = Check("""
            using System;

            class Program
            {
                static int a;

                static int One() => a;
                static int Two() => a;
                static int Three() => a;

                static void Main()
                {
                    a = 1;
                    a = a + a;
                    Console.WriteLine(One() + Two() + Three());
                }
            }
            """);

        Assert.Equal($"{Fields}: 7 places in Program.Main() (IL_0001, IL_0006, IL_000b and IL_0011), Program.One() (IL_0000), "
            + "Program.Two() (IL_0000) and Program.Three() (IL_0000). For now, keep values in local variables and pass them as parameters.",
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
                    Console.WriteLine(Math.Abs(-3));
                    Console.WriteLine(Math.Max(1, 2));
                    Console.WriteLine(Math.Abs(-4));
                }
            }
            """);

        Assert.Equal(["the library method System.Math.Abs(int)", "the library method System.Math.Max(int, int)"],
            analysis.Groups.Select(group => group.Feature));
        Assert.All(analysis.Groups, group => Assert.True(group.IsLibraryCall));
        Assert.Equal([2, 1], analysis.Groups.Select(group => group.Problems.Count));
        Assert.Equal($"""
            Test.dll uses 2 C# features that M0 does not compile yet (3 places):
              the library method System.Math.Abs(int) is not available in M0: 2 places in Program.Main() (IL_0002 and IL_001a).
              the library method System.Math.Max(int, int) is not available in M0: 1 place in Program.Main() (IL_000e).
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
            Console.WriteLine("done");
            """);

        Assert.Equal(
        [
            "text strings arrive after M0: 1 place in the top-level statements (IL_000c). "
                + "For now, print numbers and bools with Console.WriteLine.",
            "the library method System.Console.WriteLine(string) is not available in M0: 1 place in the top-level statements (IL_0011).",
            $"checked arithmetic arrives after M0: 1 place in local function Twice (IL_0002). {Checked}",
        ], analysis.Groups.Select(group => group.Line));
        Assert.Contains("\n  Program.<Main>$(string[]) at IL_000c (ldstr): text strings arrive after M0.", analysis.Details);
        Assert.Contains("\n  Program.<<Main>$>g__Twice|0_0(int) at IL_0002 (mul.ovf): checked arithmetic arrives after M0.",
            analysis.Details);
    }

    [Fact]
    public void Types_M0_has_no_words_for_are_named_one_by_one()
    {
        var analysis = Check("""
            using System;

            class Program
            {
                static int Next(Random random, TimeSpan when, Random other) => 0;

                static void Main() => Console.WriteLine(Next(null, default, null));
            }
            """);

        var types = analysis.Groups.Where(group => group.Feature.StartsWith("the type ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["the type System.TimeSpan", "the type System.Random"], types.Select(group => group.Feature));
        Assert.Equal("the type System.Random arrives after M0: 2 places in Program.Next(System.Random, System.TimeSpan, "
            + "System.Random) (parameter 'random' and parameter 'other'). M0 supports bool, the whole-number types "
            + "(sbyte to ulong, nint, nuint) and void.", types[1].Line);
    }

    [Fact]
    public void A_SquareDemo_like_program_gets_a_short_summary()
    {
        // top-level statements, float arrays, a local function and string interpolation: 31 places, 4 features (an
        // array of floats is floating point)
        var analysis = Check("""
            using System;

            static float Logit(float[] weights, float[] features)
            {
                return (weights[0] * features[0]) + (weights[1] * features[1]);
            }

            float[] weights = { 0.5f, -0.25f };
            float[] features = { 1.0f, 2.0f };

            float result = Logit(weights, features);
            Console.WriteLine($"Logit result: {result}");
            """);

        Assert.Equal(["floating point (float/double)", "string interpolation ($\"...\")", "text strings",
            "the library method System.Console.WriteLine(string)"], analysis.Groups.Select(group => group.Feature));
        Assert.Equal([20, 9, 1, 1], analysis.Groups.Select(group => group.Problems.Count));
        Assert.Equal($"""
            Test.dll uses 4 C# features that M0 does not compile yet (31 places):
              floating point (float/double) arrives after M0: 20 places in the top-level statements (IL_0001, IL_0008, IL_000d and 10 more) and local function Logit (return type, parameter 'weights', parameter 'features' and 4 more). For now, use whole numbers: int, long and the other integer types.
              string interpolation ($"...") arrives after M0: 9 places in the top-level statements (IL_0036, IL_003b, IL_0040 and 6 more). For now, print numbers and bools with Console.WriteLine.
              text strings arrive after M0: 1 place in the top-level statements (IL_0042). For now, print numbers and bools with Console.WriteLine.
              the library method System.Console.WriteLine(string) is not available in M0: 1 place in the top-level statements (IL_005b).
            {LibraryCalls}
            For every place, run: chipsharp check Test.dll --details
            """.ReplaceLineEndings("\n"), analysis.Summary);
        Assert.InRange(analysis.Summary.Split('\n').Length, 1, 11);
        Assert.Equal(33, analysis.Details.Split('\n').Length);
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
}
