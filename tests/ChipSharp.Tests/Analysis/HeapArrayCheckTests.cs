using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// Arrays on the heap in the analysis (see HeapArrays): which newarr makes its array on the stack and which on the heap,
/// which parameters are kept and which may be null, through every call; what a target with a heap accepts, what one
/// without a heap says, and what no target compiles yet.
/// </summary>
public sealed class HeapArrayCheckTests : IDisposable
{
    private const string Decisions = """
        using System;

        class Program
        {
            static int[] field;

            static int[] Returned() => new int[2];

            static int Local()
            {
                int[] values = new int[3];
                return values.Length;
            }

            static int Sized(int n)
            {
                int[] values = new int[n];
                return values.Length;
            }

            static void Stored() => field = new int[1];

            static void Keep(int[] values) => field = values;

            static void Through(int[] values) => Keep(values);

            static int Reads(int[] values) => values[0];

            static int Passes()
            {
                Through(new int[4]);
                return Reads(new int[5]) + Reads(null);
            }

            static int Again()
            {
                int[] previous = new int[1];
                for (int i = 0; i < 3; i++)
                {
                    int[] current = new int[1];
                    current[0] = previous[0] + 1;
                    previous = current;
                }

                return previous[0];
            }

            static int[][] Jagged()
            {
                int[][] rows = new int[2][];
                rows[0] = new int[3];
                return rows;
            }

            static int JaggedHere()
            {
                int[][] rows = new int[2][];
                rows[0] = new int[3];
                return rows[0].Length;
            }

            static ref int Element()
            {
                int[] values = new int[2];
                return ref values[1];
            }

            static int Empty()
            {
                int[] none = new int[0];
                return none.Length;
            }

            static void Main()
            {
                Console.WriteLine(Returned().Length + Local() + Sized(2) + Passes() + Again() + Jagged().Length
                    + JaggedHere() + Element() + Empty());
                Stored();
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

    [Fact]
    public void An_array_is_on_the_heap_where_it_is_kept_sized_when_the_program_runs_or_made_again_while_in_use()
    {
        var program = Lowered.Program(Lowered.Compile(Decisions));

        Assert.Equal(
        [
            "Program.Returned: heap",
            "Program.Local: stack",
            "Program.Sized: heap",
            "Program.Passes: heap, stack",
            "Program.Again: stack, heap",
            "Program.Jagged: heap, heap",
            "Program.JaggedHere: stack, heap",
            "Program.Element: heap",
            "Program.Empty: stack",
            "Program.Stored: heap",
        ], Newarrs(program));
    }

    [Fact]
    public void A_parameter_is_kept_where_its_array_is_or_where_it_goes_to_one_that_is_and_may_be_null_when_a_caller_says_so()
    {
        var program = Lowered.Program(Lowered.Compile(Decisions));
        var heap = program.HeapArrays;
        IlMethod Method(string name) => program.Methods.Single(method => method.DisplayName == name);

        Assert.True(heap.Keeps(Method("Program.Keep"), 0));
        Assert.True(heap.Keeps(Method("Program.Through"), 0));
        Assert.False(heap.Keeps(Method("Program.Reads"), 0));
        Assert.True(heap.MayBeNull(Method("Program.Reads"), 0));
        Assert.False(heap.MayBeNull(Method("Program.Through"), 0));
        Assert.False(heap.MayBeNull(Method("Program.Keep"), 0));
    }

    [Fact]
    public void Arrays_on_the_heap_compile_for_any_target_with_a_heap_and_need_one_on_a_chip_without()
    {
        Assert.True(Check(Decisions).IsSupported, Check(Decisions).Details);
        var pico = Targets.Load("pico");
        Assert.Empty(WholeProgramPass.Check(Load(Decisions), pico).Problems);

        var tiny = WholeProgramPass.Check(Load(Decisions), Targets.Load(TinyChip.Name)).Problems
            .Where(problem => !problem.Feature.StartsWith("Console.", StringComparison.Ordinal))
            .Select(problem => problem.Line).ToList();

        const string NoHeap = "needs a heap, and the target 'tiny' (2 KB RAM) has no heap.";
        const string Escapes = "an array that leaves the method that creates it (returned, or stored in a field or in "
            + $"another array) {NoHeap} Keep each array in the method that creates it; the methods it calls can take it "
            + "as a parameter.";
        Assert.Contains($"Program.Returned(), return type (int[]): {Escapes}", tiny);
        Assert.Contains("Program.Sized(int) at IL_0001 (newarr): an array whose size is known only when the program runs "
            + $"{NoHeap} Give the array a fixed size: a constant, such as new int[16].", tiny);
        Assert.Contains("Program.Again() at IL_000c (newarr): an array made in a loop while the one made there before is "
            + $"still in use {NoHeap} Make the array once, before the loop, and reuse it.", tiny);
        Assert.Contains("Program.Passes() at IL_0017 (call): null in place of an array " + NoHeap
            + " Use an empty array (new int[0]): an array on the stack is never null.", tiny);
        Assert.Contains($"Program.Element() at IL_000c (ret): {Escapes}", tiny);
        Assert.DoesNotContain(tiny, line => line.StartsWith("Program.Local()", StringComparison.Ordinal)
            || line.StartsWith("Program.Empty()", StringComparison.Ordinal));
    }

    [Fact]
    public void What_no_target_compiles_yet_a_static_array_kept_covariance_and_references_to_arrays()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static readonly byte[] Table = { 1, 2, 3 };
                static int[] kept;

                static byte[] Get() => Table;

                static object[] Covariant() => new int[1][];

                static void Replace(ref int[] values) => values = new int[1];

                static void Main()
                {
                    kept = new int[1];
                    Replace(ref kept);
                    string[] names = new string[2];
                    Console.WriteLine(Get().Length + Covariant().Length + names.Length + kept.Length);
                }
            }
            """);

        // an array of strings is an array of objects, which compiles
        const string Covariance = "array covariance (an array of one type of object used as an array of another, "
            + "object[] items = new int[2][]) arrives later. For now, make the array of the type it is used as: new object[2].";
        Assert.Equal(
        [
            "Program.Main() at IL_000b (ldsflda): references to a static field that holds an array (ref, out) arrive with "
                + "M3 (the heap). For now, use the field itself: it always holds the same array.",
            "Program.Replace(ref int[]), parameter 'values' (ref int[]): references to arrays (ref int[], out int[]) "
                + "arrive later. For now, return the array, or keep it in a field.",
            "Program.Get() at IL_0005 (ret): an array in a static field that needs no heap (a constant table, or a fixed "
                + "array) kept outside its field: returned, stored in another field or array, or passed to a method that "
                + "keeps it arrives later. For now, read it through its field where it is needed, or copy its elements "
                + "into an array made with new and keep that.",
            $"Program.Covariant() at IL_0006 (stloc.0): {Covariance}",
            $"Program.Covariant() at IL_0008 (ret): {Covariance}",
        ], problems);
    }

    [Fact]
    public void Arrays_of_objects_and_of_arrays_compile_with_a_heap_and_need_one_without()
    {
        const string Source = """
            using System;

            class Program
            {
                static object[] Things(int n)
                {
                    object[] things = new object[n];
                    things[0] = new object();
                    return things;
                }

                static void Main()
                {
                    int[][] rows = { new[] { 1 }, new int[2] };
                    Console.WriteLine(Things(2)[0] != null);
                    Console.WriteLine(rows[1].Length);
                }
            }
            """;

        Assert.True(Check(Source).IsSupported, Check(Source).Details);
        var tiny = WholeProgramPass.Check(Load(Source), Targets.Load(TinyChip.Name)).Problems
            .Select(problem => problem.Line).ToList();
        Assert.Contains("Program.Things(int), return type (object[]): arrays of strings, objects or arrays need a heap, "
            + "and the target 'tiny' (2 KB RAM) has no heap. Use arrays of numbers, bools, structs or enums.", tiny);
        Assert.Contains("Program.Things(int) at IL_0008 (newobj): creating objects (new ...) needs a heap, and the target "
            + "'tiny' (2 KB RAM) has no heap. Use structs and static methods.", tiny);
    }

    [Fact]
    public void An_interrupt_handler_makes_arrays_on_the_stack_but_none_on_the_heap()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load("""
            using ChipSharp.Hardware;

            class Program
            {
                static volatile int[] samples = new int[4];
                static volatile int count;

                static void Main()
                {
                    Timing.EveryMilliseconds(10);
                    while (true)
                    {
                    }
                }

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    int[] pins = { 2, 3 };
                    Pin.Toggle(pins[count % pins.Length]);
                    if (count++ % 100 == 0)
                    {
                        samples = new int[count % 8 + 1];
                    }
                }
            }
            """), PicoBuild.Pico);

        // the pins are on the stack; the samples, of a size known only when the program runs, would be on the heap
        Assert.Equal(["Program.Tick() at IL_0042 (newarr): an interrupt handler cannot create objects (new ...). The heap "
            + "may be in the middle of an allocation or a collection when the interrupt comes: create what the handler "
            + "needs before, e.g. in a static field."], analysis.Problems.Select(problem => problem.Line));
    }

    /// <summary>"Program.Passes: heap, stack": where each newarr of each method makes its array, in their order.</summary>
    private static string[] Newarrs(IlProgram program) =>
    [
        .. program.Methods.Where(method => method.Body!.Instructions.Any(i => i.OpCode == IlOpCode.Newarr))
            .Where(method => method.DisplayName != "Program.Main")
            .Select(method =>
            {
                var heap = ArrayEscapeAnalysis.Analyze(method, program.StaticArrays, program.HeapArrays).HeapArrays;
                var where = method.Body!.Instructions.Where(i => i.OpCode == IlOpCode.Newarr)
                    .Select(newarr => heap.Contains(newarr.Offset) ? "heap" : "stack");
                return $"{method.DisplayName}: {string.Join(", ", where)}";
            }),
    ];

    private TargetLoader Targets => new(TinyChip.Targets(_scratch));

    private static IlAssembly Load(string source) => IlAssembly.Load(CSharpCompiler.Compile(source), "Test.dll");
}
