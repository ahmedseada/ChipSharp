using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;
using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// Arrays in static fields (see StaticArrays): a field its initializer gives a new array of a constant size, once, is a
/// constant table when nothing in the program writes an element (also not the methods it is passed to), else a static
/// fixed array; every other static field of an array type is assigned while the program runs, on the heap (see
/// HeapArrays), so on a chip without a heap it is rejected, with a message in C# words.
/// </summary>
public sealed class StaticArrayCheckTests : IDisposable
{
    // what breaks the rule is on the heap, which the chip "tiny" does not have (see ProblemsWithoutHeap)
    private const string NoHeap = "the target 'tiny' (2 KB RAM) has no heap.";

    private const string Assigned = "an array in a static field that is assigned again, or anywhere but in its field "
        + $"initializer or static constructor needs a heap, and {NoHeap} Assign it once, where the field is "
        + "declared (static int[] buffer = new int[16];), and change its elements instead.";

    private const string NotConstant = "an array in a static field that is not made with a constant size and constant "
        + $"elements needs a heap, and {NoHeap} Make it where the field is declared, with a constant size (new "
        + "int[16]) or constant elements ({ 1, 2, 3 }); a method that Main calls first can set the elements.";

    private const string Null = "a static field of an array type that holds null (it is never given an array) needs "
        + $"a heap, and {NoHeap} Give it an array where the field is declared (static int[] buffer = new int[16];).";

    private const string References = "references to a static field that holds an array (ref, out) need a heap, and "
        + $"{NoHeap} Use the field itself: it always holds the same array.";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void A_table_nothing_writes_is_constant_and_one_that_is_written_is_fixed()
    {
        var arrays = Arrays(Program("""
            buffer[3] = Digits[2] + Small[1];
            Console.WriteLine(buffer[3] + Bumped[0] + Squares.Length);
            Bump(Bumped);
            """, """
            static readonly byte[] Digits = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };
            static readonly int[] Small = { 5, 6 };
            static readonly int[] Bumped = { 1, 2, 3 };
            static int[] buffer = new int[16];
            static readonly long[] Squares = new long[4];

            static void Bump(int[] values) => values[0]++;
            """));

        // Digits from the initializer's data, Small from its element stores, Squares all zero
        Assert.Equal(
        [
            "Program.Digits: constant [10 x i8] = 63, 6, 91, 79, 102, 109, 125, 7, 127, 111",
            "Program.Small: constant [2 x i32] = 5, 6",
            "Program.Bumped: fixed [3 x i32] = 1, 2, 3",
            "Program.buffer: fixed [16 x i32] = zero",
            "Program.Squares: constant [4 x i64] = zero",
        ], arrays);
    }

    [Fact]
    public void Writing_in_a_callee_of_a_callee_makes_a_fixed_array_and_reading_there_keeps_a_table()
    {
        var arrays = Arrays(Program("Console.WriteLine(Outer(Written) + Outer(Read) + Sum(Read));", """
            static readonly int[] Written = { 1, 2, 3 };
            static readonly int[] Read = { 4, 5, 6 };

            static int Outer(int[] values) => Middle(values, values.Length > 5);

            static int Middle(int[] values, bool clear)
            {
                if (clear)
                {
                    Clear(values);
                }

                return Sum(values);
            }

            static int Sum(int[] values)
            {
                int total = 0;
                foreach (int value in values)
                {
                    total += value;
                }

                return total;
            }

            static void Clear(int[] values) => values[0] = 0;
            """));

        // whether the write runs is not asked: Middle may clear what Outer gives it, and Outer gets both arrays
        Assert.Equal(
        [
            "Program.Written: fixed [3 x i32] = 1, 2, 3",
            "Program.Read: fixed [3 x i32] = 4, 5, 6",
        ], arrays);

        var onlyRead = Arrays(Program("Console.WriteLine(Sum(Read) + Twice(Read));", """
            static readonly int[] Read = { 4, 5, 6 };

            static int Twice(int[] values) => Sum(values) * 2;

            static int Sum(int[] values) => values[0] + values[values.Length - 1];
            """));

        Assert.Equal(["Program.Read: constant [3 x i32] = 4, 5, 6"], onlyRead);
    }

    [Fact]
    public void Writing_through_a_reference_to_an_element_makes_a_fixed_array_and_reading_through_one_does_not()
    {
        var arrays = Arrays(Program("""
            Increment(ref Counts[1]);
            ref int first = ref Totals[0];
            first += 2;
            Console.WriteLine(Peek(in Limits[0]) + Counts[1] + Totals[0]);
            Points[1].X = 5;
            Moves[0].Move();
            Console.WriteLine(Origins[1].X + Origins[0].Sum() + Points[1].X + Moves[0].X);
            """, """
            static int[] Counts = new int[2];
            static int[] Totals = new int[2];
            static readonly int[] Limits = { 10, 20 };
            static Point[] Points = new Point[3];
            static readonly Point[] Origins = new Point[2];
            static Point[] Moves = new Point[1];

            static void Increment(ref int value) => value++;

            static int Peek(in int value) => value;

            struct Point
            {
                public int X;
                public int Y;

                public readonly int Sum() => X + Y;

                public void Move() => X++;
            }
            """));

        Assert.Equal(
        [
            "Program.Counts: fixed [2 x i32] = zero",
            "Program.Totals: fixed [2 x i32] = zero",
            "Program.Limits: constant [2 x i32] = 10, 20",
            "Program.Points: fixed [3 x Program.Point] = zero",
            "Program.Origins: constant [2 x Program.Point] = zero",
            "Program.Moves: fixed [1 x Program.Point] = zero",
        ], arrays);
    }

    [Fact]
    public void Every_element_type_of_arrays_on_the_stack_starts_with_its_constants()
    {
        var arrays = Arrays(Program("""
            Console.WriteLine(Bools[0]);
            Console.WriteLine(SBytes[0] + Shorts[0] + UInts[0] + (long)ULongs[1] + Nints[1] + (int)Colors[1]);
            Console.WriteLine(Floats[1] + Doubles[1]);
            """, """
            static readonly bool[] Bools = { true, false, true };
            static readonly sbyte[] SBytes = { -1, 2 };
            static readonly short[] Shorts = { -300, 1, 2 };
            static readonly uint[] UInts = { 1, 4000000000 };
            static readonly ulong[] ULongs = { 1, 0xFFFFFFFFFFFFFFFF };
            static readonly nint[] Nints = { 1, -2 };
            static readonly Color[] Colors = { Color.Green, Color.Blue, Color.Red };
            static readonly float[] Floats = { 1.5f, -2.25f, 0f };
            static readonly double[] Doubles = { 0.1, -0.0 };

            enum Color : byte { Red, Green, Blue }
            """));

        // a bool is a byte, 0 or 1; numbers keep their bits (uint 4000000000 is the i32 -294967296)
        Assert.Equal(
        [
            "Program.Bools: constant [3 x i8] = 1, 0, 1",
            "Program.SBytes: constant [2 x i8] = -1, 2",
            "Program.Shorts: constant [3 x i16] = -300, 1, 2",
            "Program.UInts: constant [2 x i32] = 1, -294967296",
            "Program.ULongs: constant [2 x i64] = 1, -1",
            "Program.Nints: constant [2 x nint] = 1, -2",
            "Program.Colors: constant [3 x i8] = 1, 2, 0",
            "Program.Floats: constant [3 x f32] = 1.5, -2.25, 0",
            "Program.Doubles: constant [2 x f64] = 0.1, -0",
        ], arrays);
    }

    [Fact]
    public void Empty_arrays_and_a_static_constructor_that_does_more_are_accepted()
    {
        var analysis = Check(Program("Console.WriteLine(None.Length + Zero.Length + Late[1] + count);", """
            static readonly int[] None = [];
            static readonly int[] Zero = new int[0];
            static readonly int[] Late;
            static int count;

            static Program()
            {
                count = Twice(3);
                Late = new[] { 7, 8 };
                Console.WriteLine(Late[0]);
            }

            static int Twice(int value) => value * 2;
            """));

        Assert.Empty(analysis.Problems);
        Assert.Equal(
        [
            "Program.None: constant [0 x i32] = zero",
            "Program.Zero: constant [0 x i32] = zero",
            "Program.Late: constant [2 x i32] = 7, 8",
        ], Describe(analysis.ToProgram().StaticArrays));
    }

    [Fact]
    public void A_static_array_assigned_again_or_outside_its_initializer_is_rejected_at_every_use()
    {
        var problems = ProblemsWithoutHeap(Program("Reset(); Console.WriteLine(table.Length + other.Length);", """
            static int[] table = new int[4];
            static int[] other;

            static void Reset()
            {
                table = new int[4];
                other = new int[2];
            }
            """));

        Assert.Equal(
        [
            $"Program.Main() at IL_0005 (ldsfld): {Assigned}",
            $"Program.Main() at IL_000c (ldsfld): {Assigned}",
            $"Program..cctor() at IL_0006 (stsfld): {Assigned}",
            $"Program.Reset() at IL_0006 (stsfld): {Assigned}",
            $"Program.Reset() at IL_0011 (stsfld): {Assigned}",
        ], problems);
    }

    [Theory]
    [InlineData("in a loop", "for (int i = 0; i < 2; i++) { table = new int[4]; }")]
    [InlineData("on one path only", "if (limit > 0) { table = new int[4]; }")]
    public void A_static_constructor_that_may_assign_the_array_more_than_once_or_not_at_all_is_rejected(string what, string body)
    {
        var problems = ProblemsWithoutHeap(Program("Console.WriteLine(table.Length);", $$"""
            static int limit = 2;
            static int[] table;

            static Program()
            {
                {{body}}
            }
            """));

        Assert.True(problems.Length == 2 && problems.All(problem => problem.EndsWith(Assigned, StringComparison.Ordinal)),
            $"{what}: {string.Join("\n", problems)}");
    }

    [Fact]
    public void A_static_array_of_another_types_initializer_is_rejected()
    {
        var problems = ProblemsWithoutHeap(Program("Console.WriteLine(Tables.Squares.Length);", """
            static Program()
            {
                Tables.Squares = new int[3];
            }

            static class Tables
            {
                public static int[] Squares;
            }
            """));

        Assert.Equal(
        [
            $"Program.Main() at IL_0000 (ldsfld): {Assigned}",
            $"Program..cctor() at IL_0006 (stsfld): {Assigned}",
        ], problems);
    }

    [Fact]
    public void A_static_array_that_is_never_given_one_or_given_null_is_rejected()
    {
        // C# leaves "= null" out: the field is null anyway
        var problems = ProblemsWithoutHeap(Program("Console.WriteLine(missing.Length + empty.Length);", """
            static int[] missing;
            static int[] empty = null;
            """));

        Assert.Equal(
        [
            $"Program.Main() at IL_0000 (ldsfld): {Null}",
            $"Program.Main() at IL_0007 (ldsfld): {Null}",
        ], problems);
    }

    [Fact]
    public void A_static_array_whose_size_or_elements_are_computed_is_rejected()
    {
        var problems = ProblemsWithoutHeap(Program("Console.WriteLine(Sized.Length + Computed[0] + Corners[0].X + Copy.Length);", """
            static int size = 4;
            static readonly int[] Sized = new int[size];
            static readonly int[] Computed = { size, 2 };
            static readonly Point[] Corners = { new Point(1, 2) };
            static readonly int[] Copy = Computed;

            struct Point
            {
                public int X;

                public Point(int x, int y) => X = x + y;
            }
            """));

        Assert.Equal(
        [
            $"Program.Main() at IL_0000 (ldsfld): {NotConstant}",
            $"Program.Main() at IL_0007 (ldsfld): {NotConstant}",
            $"Program.Main() at IL_000f (ldsfld): {NotConstant}",
            $"Program.Main() at IL_0020 (ldsfld): {NotConstant}",
            "Program..cctor() at IL_000b (newarr): an array whose size is known only when the program runs needs a heap, "
                + $"and {NoHeap} Give the array a fixed size: a constant, such as new int[16].",
            $"Program..cctor() at IL_0010 (stsfld): {NotConstant}",
            $"Program..cctor() at IL_0027 (stsfld): {NotConstant}",
            $"Program..cctor() at IL_0040 (stsfld): {NotConstant}",
            $"Program..cctor() at IL_0045 (ldsfld): {NotConstant}",
            $"Program..cctor() at IL_004a (stsfld): {NotConstant}",
        ], problems);
    }

    [Fact]
    public void A_reference_to_a_static_array_field_is_rejected()
    {
        // a reference to an array is no heap matter: not supported on any target (see HeapArrays)
        var problems = ProblemsWithoutHeap(Program("Fill(ref buffer); Console.WriteLine(buffer[0]);", """
            static int[] buffer = new int[4];

            static void Fill(ref int[] values) => values[0] = 1;
            """), compilesWithHeap: false);

        Assert.Contains($"Program.Main() at IL_0000 (ldsflda): {References}", problems);
        Assert.Contains($"Program.Main() at IL_000a (ldsfld): {References}", problems);
        Assert.Contains($"Program..cctor() at IL_0006 (stsfld): {References}", problems);
    }

    [Fact]
    public void A_static_array_keeps_the_rules_of_arrays_on_the_stack_otherwise()
    {
        // compared with null it is never null; kept outside its field it is not (it has no object around it, see
        // HeapArrays); an array of strings is an array of objects, which compiles
        var problems = Problems(Program("""
            Console.WriteLine(Table == null);
            Console.WriteLine(Get().Length + Names.Length);
            """, """
            static readonly int[] Table = { 1, 2, 3 };
            static readonly string[] Names = { "a" };

            static int[] Get() => Table;
            """));

        Assert.Equal(
        [
            "Program.Get() at IL_0005 (ret): an array in a static field that needs no heap (a constant table, or a fixed "
                + "array) kept outside its field: returned, stored in another field or array, or passed to a method that "
                + "keeps it arrives later. For now, read it through its field where it is needed, or copy its elements "
                + "into an array made with new and keep that.",
        ], problems);
    }

    [Fact]
    public void The_summary_groups_the_uses_of_a_static_array_that_breaks_the_rule()
    {
        var analysis = CheckWithoutHeap(Program("Reset(); Console.WriteLine(table[0]);", """
            static int[] table = new int[4];

            static void Reset() => table = new int[4];
            """));

        var group = Assert.Single(analysis.Groups, group => group.Problems.All(IsAboutArrays));
        Assert.Equal("an array in a static field that is assigned again, or anywhere but in its field initializer or "
            + "static constructor needs a heap, and the target 'tiny' (2 KB RAM) has no heap: 3 places in Program.Main() "
            + "(IL_0005), the static field initializers of Program (IL_0006) and Program.Reset() (IL_0006). Assign it "
            + "once, where the field is declared (static int[] buffer = new int[16];), and change its elements instead.",
            group.Line);
    }

    [Fact]
    public void A_chip_without_a_heap_has_static_arrays_too()
    {
        var tiny = new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name);
        var assembly = Load(Program("""
            for (int i = 0; i < Digits.Length; i++)
            {
                buffer[i] = Digits[i];
            }

            total = Sum(buffer);
            """, """
            static readonly byte[] Digits = { 0x3F, 0x06, 0x5B, 0x4F };
            static int[] buffer = new int[4];
            static int total;

            static int Sum(int[] values) => values[0] + values[3];
            """));

        var analysis = WholeProgramPass.Check(assembly, tiny);

        Assert.Empty(analysis.Problems);
        Assert.Equal(["Program.Digits: constant [4 x i8] = 63, 6, 91, 79", "Program.buffer: fixed [4 x i32] = zero"],
            Describe(analysis.ToProgram().StaticArrays));

        // and a static array that breaks the rule needs the heap the chip does not have
        var broken = WholeProgramPass.Check(Load(Program("buffer = new int[2]; Console.WriteLine(buffer[0]);", """
            static int[] buffer = new int[4];
            """)), tiny);
        Assert.Contains("Program.Main() at IL_0006 (stsfld): an array in a static field that is assigned again, or "
            + "anywhere but in its field initializer or static constructor needs a heap, and the target 'tiny' (2 KB RAM) "
            + "has no heap. Assign it once, where the field is declared (static int[] buffer = new int[16];), and change "
            + "its elements instead.", broken.Problems.Select(problem => problem.Line));
    }

    /// <summary>
    /// The program in <paramref name="source"/> checked for the chip "tiny", which has no heap; checked without a target
    /// (for any target, the PC's and the Pico's with a heap) it compiles: what breaks the rule of static arrays is on the
    /// heap (see HeapArrays).
    /// </summary>
    private AnalysisResult CheckWithoutHeap(string source, bool compilesWithHeap = true)
    {
        Assert.True(Check(source).IsSupported == compilesWithHeap, Check(source).Details);
        var analysis = WholeProgramPass.Check(Load(source), new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name));
        Assert.False(analysis.IsSupported);
        return analysis;
    }

    /// <summary>The problems of <see cref="CheckWithoutHeap"/>, one per place.</summary>
    private string[] ProblemsWithoutHeap(string source, bool compilesWithHeap = true) =>
        [.. CheckWithoutHeap(source, compilesWithHeap).Problems.Where(IsAboutArrays).Select(problem => problem.Line)];

    /// <summary>True for a problem about arrays: the chip "tiny" has no console either, which is a problem of its own.</summary>
    private static bool IsAboutArrays(Problem problem) =>
        !problem.Feature.StartsWith("Console.Write", StringComparison.Ordinal);

    /// <summary>The static arrays of the program in <paramref name="source"/>, which the whole-program pass accepts.</summary>
    private static string[] Arrays(string source)
    {
        var analysis = Check(source);
        Assert.Empty(analysis.Problems);
        return Describe(analysis.ToProgram().StaticArrays);
    }

    /// <summary>Each array: "Program.Digits: constant [10 x i8] = 63, 6, ..." ("fixed" in RAM, "zero" for zeros).</summary>
    private static string[] Describe(StaticArrays arrays) =>
    [
        .. arrays.Arrays.Select(array =>
            $"{array.Field.DeclaringType.FullName}.{array.Field.Name}: {(array.IsConstant ? "constant" : "fixed")} " +
            $"[{array.Length} x {array.ElementType}] = " +
            (array.Elements is { } elements ? string.Join(", ", elements.Select(Value)) : "zero")),
    ];

    private static string Value(ChipSharp.Ir.TirConst constant) => constant.Type.IsFloat
        ? constant.FloatValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        : constant.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static IlAssembly Load(string source) => IlAssembly.Load(CSharpCompiler.Compile(source), "Test.dll");

    /// <summary>A class Program with <paramref name="main"/> as its Main and <paramref name="members"/> beside it.</summary>
    private static string Program(string main, string members = "") => $$"""
        using System;

        class Program
        {
            {{members.Replace("\n", "\n    ", StringComparison.Ordinal)}}

            static void Main()
            {
                {{main.Replace("\n", "\n        ", StringComparison.Ordinal)}}
            }
        }
        """;
}
