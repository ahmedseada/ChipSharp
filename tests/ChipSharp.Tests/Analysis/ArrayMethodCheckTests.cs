using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Runtime.Windows;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// The methods of System.Array in the whole-program pass (see ChipSharp.Frontend's ArrayMethods): which compile (on any
/// target, or only with a heap), and what the others say: those of a delegate arrive with delegates, Sort and
/// BinarySearch take numbers and bools, IndexOf what it can compare as .NET does, a copy arrays of one element type.
/// </summary>
public sealed class ArrayMethodCheckTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void Every_method_ChipSharp_compiles_is_fine_on_arrays_of_every_element_type()
    {
        var analysis = WholeProgram.Check(Program("""
            int[] numbers = { 3, 1, 2 };
            Color[] colors = new Color[3];
            Point[] points = new Point[3];
            Node[] nodes = { new Node(), null };
            int[][] rows = { numbers };
            Array.Copy(numbers, 0, numbers, 1, 2);
            Array.Copy(colors, colors, 3L);
            Array.ConstrainedCopy(points, 0, points, 1, 1);
            nodes.CopyTo(nodes, 0);
            Array.Clear(points);
            Array.Clear(rows, 0, 1);
            Array.Fill(colors, Color.Blue);
            Array.Fill(points, new Point(), 1, 1);
            Console.WriteLine(Array.IndexOf(colors, Color.Blue) + Array.LastIndexOf(nodes, null) + Array.IndexOf(rows, numbers));
            Array.Reverse(points);
            Array.Reverse(nodes, 0, 1);
            Array.Sort(numbers);
            Console.WriteLine(Array.BinarySearch(numbers, 2));
            Array.Resize(ref numbers, 5);
            Array.Resize(ref field, 5);
            var copy = (Point[])points.Clone();
            Console.WriteLine(copy.Length);
            """, """
            static int[] field;

            enum Color { Red, Blue }
            struct Point { public int X; }
            class Node { }
            """));

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void The_methods_that_take_a_delegate_arrive_with_delegates()
    {
        var problems = Problems(Program("""
            int[] values = { 1, 2 };
            Console.WriteLine(Array.Exists(values, IsTwo));
            Console.WriteLine(Array.FindIndex(values, IsTwo));
            """, "static bool IsTwo(int value) => value == 2;"));

        Assert.Contains(problems, line => line.EndsWith("the methods of System.Array that take a delegate (Array.Exists, "
            + "Find, FindIndex, FindAll, TrueForAll, ForEach, ConvertAll, Sort with a Comparison) arrive with M3 "
            + "(delegates). For now, write the loop: for (int i = 0; i < array.Length; i++) if (...) { ... }.",
            StringComparison.Ordinal));
        Assert.Equal(2, problems.Count(line => line.Contains("(delegates)", StringComparison.Ordinal)));
    }

    [Fact]
    public void Sort_and_BinarySearch_take_numbers_bools_and_comparable_structs_and_say_so_for_enums_and_other_structs()
    {
        // Point implements IComparable<Point>, which the library's Sort and BinarySearch ask of T (generics over classes)
        var problems = Problems(Program("""
            Color[] colors = { Color.Blue, Color.Red };
            Array.Sort(colors);
            Point[] points = new Point[2];
            Console.WriteLine(Array.BinarySearch(points, new Point()));
            Plain[] plain = new Plain[2];
            Array.Sort(plain);
            """, """
            enum Color { Red, Blue }
            struct Point : IComparable<Point> { public int X; public int CompareTo(Point other) => X - other.X; }
            struct Plain { public int X; }
            """));

        const string Sort = "Array.Sort and Array.BinarySearch of arrays of enums, structs that do not implement " +
            "IComparable<T>, text or objects arrive later. For now, sort an array of numbers (a key for each element) " +
            "or of structs that implement IComparable<T>, or write the loop.";
        Assert.Equal([$"Program.Main() at IL_000a (call): {Sort}", $"Program.Main() at IL_002e (call): {Sort}"],
            problems);
    }

    [Fact]
    public void IndexOf_says_so_for_what_dotnet_compares_with_an_Equals_of_its_own()
    {
        var problems = Problems(Program("""
            Point[] points = new Point[2];
            Console.WriteLine(Array.IndexOf(points, new Point()));
            Same[] same = new Same[1];
            Console.WriteLine(Array.LastIndexOf(same, null));
            object[] things = new object[1];
            Console.WriteLine(Array.IndexOf(things, null));
            Plain[] plain = new Plain[1];
            Console.WriteLine(Array.IndexOf(plain, null));
            """, """
            struct Point { public int X; }
            class Same : IEquatable<Same> { public bool Equals(Same other) => true; }
            class Plain { }
            """));

        const string IndexOf = "Array.IndexOf and Array.LastIndexOf of arrays of structs, text, object or classes with an "
            + "Equals of their own arrive later.";
        Assert.Equal(3, problems.Count(line => line.Contains(IndexOf, StringComparison.Ordinal)));
    }

    [Fact]
    public void A_copy_between_arrays_of_different_element_types_is_not_compiled_yet()
    {
        var problems = Problems(Program("""
            int[] ints = { 1, 2 };
            long[] longs = new long[2];
            Array.Copy(ints, longs, 2);
            Console.WriteLine(longs[1]);
            """));

        Assert.Equal(["Program.Main() at IL_0017 (call): Array.Copy and CopyTo between arrays of different element types "
            + "(int[] into long[]) arrive later. For now, copy the elements one by one: for (int i = 0; i < length; i++) "
            + "destination[i] = source[i];"], problems);
    }

    [Fact]
    public void Resize_takes_a_local_a_parameter_or_a_field_and_says_so_for_an_element()
    {
        var problems = Problems(Program("""
            int[][] rows = { new int[1] };
            Array.Resize(ref rows[0], 3);
            Console.WriteLine(rows[0].Length);
            """));

        Assert.Equal(["Program.Main() at IL_0017 (call): Array.Resize of something other than a local, a parameter or a "
            + "field (an element, a ref parameter) arrives later. For now, pass the variable itself: "
            + "Array.Resize(ref buffer, size)."], problems);
    }

    [Fact]
    public void Without_a_heap_Resize_and_Clone_need_one_and_the_other_methods_are_fine()
    {
        var tiny = new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name);
        var analysis = WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile(Program("""
            int[] values = { 3, 1, 2 };
            Array.Sort(values);
            Array.Reverse(values, 0, 2);
            Array.Fill(values, 4, 1, 1);
            Array.Copy(values, 0, values, 1, 2);
            values[0] = Array.IndexOf(values, 4) + Array.BinarySearch(values, 4);
            Array.Clear(values, 0, 1);
            Array.Resize(ref values, 4);
            int[] copy = (int[])values.Clone();
            Program.copy = copy[0];
            """, "static int copy;")), "Test.dll"), tiny);

        var problems = analysis.Problems.Where(problem => problem.Feature.Contains("Array.", StringComparison.Ordinal)
            || problem.Feature.Contains("array.", StringComparison.Ordinal)).Select(problem => problem.Line).ToList();
        Assert.Equal(
        [
            "Program.Main() at IL_0050 (call): resizing an array with Array.Resize needs a heap, and the target 'tiny' "
                + "(2 KB RAM) has no heap. Make an array as big as it will ever need to be, and keep its length in a "
                + "variable.",
            "Program.Main() at IL_0056 (callvirt): copying an array into a new one with array.Clone() needs a heap, and "
                + "the target 'tiny' (2 KB RAM) has no heap. Make the copy with new and Array.Copy into an array of your "
                + "own.",
        ], problems);
    }

    [Fact]
    public void The_rest_of_System_Array_is_a_library_method_that_is_not_there_yet_saying_to_write_the_loop()
    {
        var problems = Problems(Program("""
            int[] values = { 1, 2 };
            Console.WriteLine(values.GetLength(0));
            Console.WriteLine(Array.LastIndexOf(values, 2, 1, 1) + values.GetUpperBound(0));
            """));

        const string Loop = "is not available yet. For now, write it as a loop over the elements: for (int i = 0; "
            + "i < array.Length; i++) { ... }.";
        Assert.Equal(
        [
            $"Program.Main() at IL_0011 (callvirt): the library method System.Array.GetLength(int) {Loop}",
            $"Program.Main() at IL_0026 (callvirt): the library method System.Array.GetUpperBound(int) {Loop}",
        ], problems);
    }

    [Fact]
    public void A_constant_table_that_IndexOf_and_BinarySearch_only_read_stays_a_table_and_one_that_Fill_writes_does_not()
    {
        var methods = WholeProgram.Analyze(Program("""
            Console.WriteLine(Array.IndexOf(Read, (byte)2) + Array.BinarySearch(Read, (byte)3));
            Array.Fill(Written, (byte)1);
            Array.Copy(Read, Copied, 2);
            Console.WriteLine(Written[0] + Copied[1]);
            """, """
            static readonly byte[] Read = { 1, 2, 3 };
            static readonly byte[] Written = { 1, 2, 3 };
            static readonly byte[] Copied = { 0, 0 };
            """)).Methods;
        var statics = StaticArrays.Find(methods);

        bool IsConstant(string name) => statics.TryGet(methods.SelectMany(method => method.DeclaringType.Fields)
            .First(field => field.Name == name), out var array) && array.IsConstant;
        Assert.True(IsConstant("Read"));
        Assert.False(IsConstant("Written"));
        Assert.False(IsConstant("Copied"));
    }

    [Fact]
    public void Every_runtime_defines_every_failure_the_methods_of_System_Array_call()
    {
        var targets = new TargetLoader(Build.TargetFiles.WithShipped(Path.Combine(_scratch, "targets")));
        var windows = new WindowsX64Runtime().Startup(targets.Load("host")).Files
            .Single(file => file.FileName == WindowsX64Runtime.FailuresFileName).Content;
        var pico = new Rp2040Runtime().Startup(targets.Load("pico")).Files
            .Single(file => file.FileName == Rp2040Runtime.FailuresFileName).Content;
        var weak = typeof(ChipSharp.Runtime.ArmV6M.ArmV6MRuntime).Assembly
            .GetManifestResourceStream(ChipSharp.Runtime.ArmV6M.ArmV6MRuntime.FailuresFileName)!;
        var armv6m = new StreamReader(weak).ReadToEnd();

        Assert.Equal(19, ArrayMethodFunctions.Failures.Count);
        Assert.All(ArrayMethodFunctions.Failures, failure =>
        {
            var symbol = Backend.Llvm.LlvmCodeGenerator.SymbolName(failure);
            var parameters = failure.EndsWith("BelowZero", StringComparison.Ordinal) ? "(int value)" : "(void)";
            Assert.Contains($"__declspec(noreturn) void {symbol}{parameters}", windows);
            Assert.Contains($"__attribute__((noreturn)) void {symbol}{parameters}", pico);
            Assert.Contains($"__attribute__((weak, noreturn)) void {symbol}{parameters}", armv6m);
        });
    }

    private static List<string> Problems(string source) => [.. WholeProgram.Check(source).Problems.Select(p => p.Line)];

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
