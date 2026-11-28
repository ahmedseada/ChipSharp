using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;
using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// Spans in the whole-program pass (see Spans): what ChipSharp compiles (spans of arrays, of stackalloc, of constant
/// data and of variables, their members, MemoryExtensions, BinaryPrimitives and the safe part of MemoryMarshal), with
/// no heap; a span of an array that would outlive the array, which C# allows; and what is not there yet, each said in
/// C# words, also for a chip without a heap.
/// </summary>
public sealed class SpanCheckTests : IDisposable
{
    private const string Reuse = "a span of an array or of stackalloc made in a loop while a span or reference made there "
        + "before may still be in use arrives later. For now, make the array or the stackalloc once, before "
        + "the loop, and reuse it.";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string> ProgramNames => [.. SpanPrograms.All.Select(program => program.Name)];

    [Theory]
    [MemberData(nameof(ProgramNames))]
    public void The_span_programs_compile_in_Release_and_Debug(string name)
    {
        var source = SpanPrograms.All.Single(program => program.Name == name).Source;
        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var analysis = WholeProgramPass.Check(Load(source, optimization));
            Assert.True(analysis.IsSupported, analysis.Details);
        }
    }

    [Fact]
    public void A_span_of_an_array_may_be_passed_down_and_handed_back_by_the_methods_that_get_it()
    {
        // what a callee returns or writes through a reference holds what it was given; the caller keeps to the rule
        var analysis = Check(Program("""
            int[] values = { 1, 2, 3, 4 };
            Span<int> view = Tail(values);
            Pick(out ReadOnlySpan<int> picked, values);
            var holder = new Holder(values.AsSpan(1));
            holder.Keep(view);
            Console.WriteLine(view.Length + picked.Length + holder.Values[0] + Tail(Tail(view)).Length);
            """, """
            static Span<int> Tail(Span<int> values) => values.Slice(1);

            static Span<int> Tail(int[] values) => values.AsSpan(1);

            static void Pick(out ReadOnlySpan<int> picked, int[] values) => picked = values;
            """, """
            ref struct Holder
            {
                public Span<int> Values;

                public Holder(Span<int> values) => Values = values;

                public void Keep(Span<int> values) => Values = values;
            }
            """));

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void A_span_of_an_array_that_leaves_the_method_that_makes_the_array_is_rejected_where_it_leaves()
    {
        // on a target with a heap, each such array is on the heap (see HeapArrays): a chip without one says where
        var source = Program("""
            Span<int> made = default;
            MakeOut(out made);
            MakeThrough(ref made);
            var holder = new Holder();
            holder.Keep();
            Console.WriteLine(Make().Length + made.Length + holder.Values.Length + Element());
            """, """
            static Span<int> Make()
            {
                var made = new int[4];
                return made;
            }

            static void MakeOut(out Span<int> span) => span = new int[2];

            static void Put(ref Span<int> span, Span<int> value) => span = value;

            static void MakeThrough(ref Span<int> span) => Put(ref span, new int[2]);

            static ref int First(Span<int> span) => ref span[0];

            static int Element()
            {
                var values = new int[3];
                return First(values);
            }
            """, """
            ref struct Holder
            {
                public Span<int> Values;

                public void Keep() => Values = new int[3];
            }
            """);
        Assert.True(Check(source).IsSupported, Check(source).Details);

        var problems = WholeProgramPass.Check(Load(source), new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name))
            .Problems.Where(problem => !problem.Feature.StartsWith("Console.", StringComparison.Ordinal))
            .Select(problem => problem.Line);
        const string WithoutHeap = "a span of an array that leaves the method that creates the array (returned, or stored "
            + "where the caller sees it) needs a heap, and the target 'tiny' (2 KB RAM) has no heap. Keep the span in the "
            + "method that creates the array; the methods it calls can take the span as a parameter, and return it.";
        Assert.Equal(string.Join("\n",
            $"Program.MakeOut(ref System.Span<int>) at IL_000c (stobj): {WithoutHeap}",
            $"Program.MakeThrough(ref System.Span<int>) at IL_000c (call): {WithoutHeap}",
            $"Holder.Keep() at IL_000c (stfld): {WithoutHeap}",
            $"Program.Make() at IL_000b (ret): {WithoutHeap}"), string.Join("\n", problems));
    }

    [Fact]
    public void An_array_or_stackalloc_made_again_in_a_loop_while_a_span_of_the_last_one_is_in_use_is_rejected()
    {
        var problems = Problems(Program("""
            Span<int> previous = default;
            for (int i = 0; i < 3; i++)
            {
                var made = new int[2];
                if (i > 0)
                {
                    Use(previous);
                }

                previous = made;
            }

            Span<int> last = stackalloc int[1];
            for (int i = 0; i < 3; i++)
            {
                Span<int> current = stackalloc int[2];
                Use(last);
                last = current;
            }

            // made again in a loop, but nothing from before is in use: fine
            for (int i = 0; i < 3; i++)
            {
                var buffer = new byte[4];
                Span<byte> view = buffer;
                Span<byte> scratch = stackalloc byte[2];
                view.Fill((byte)i);
                scratch[0] = view[1];
                Console.WriteLine(scratch[0]);
            }
            """, "static int Use(ReadOnlySpan<int> span) => span.Length;"));

        // the array made again while a span of the one before is in use is on the heap (see HeapArrays), a stackalloc
        // cannot be
        Assert.Equal($"Program.Main() at IL_0041 (localloc): {Reuse}", string.Join("\n", problems));
    }

    [Fact]
    public void What_is_not_there_yet_says_what_to_write_instead()
    {
        var problems = Problems("""
            using System;
            using System.Runtime.InteropServices;

            struct Point : IEquatable<Point>
            {
                public int X;

                public bool Equals(Point other) => X == other.X;
            }

            class Program
            {
                static unsafe void Main()
                {
                    Span<int> values = stackalloc int[] { 1, 2 };
                    int[] copy = values.ToArray();
                    Span<byte> sized = stackalloc byte[copy.Length];
                    int* pointer = stackalloc int[2];
                    Span<int> listed = [1, 2];
                    Span<Point> points = stackalloc Point[2];
                    Console.WriteLine(sized.Length + listed.Length + points.IndexOf(new Point()) + pointer[0]);
                }
            }
            """);

        Assert.Equal(string.Join("\n",
            "Program.Main(), local variable V_2 (int*): unsafe code (pointers, &x, sizeof, fixed) arrives later. For now, "
                + "use ref parameters, ref locals and structs.",
            "Program.Main(), local variable V_5 (System.Runtime.CompilerServices.InlineArray2<int>): collection "
                + "expressions as spans (Span<int> s = [1, 2, 3]) arrive later. For now, use stackalloc int[] { 1, 2, 3 }, "
                + "or an array: Span<int> s = new int[] { 1, 2, 3 };",
            "Program.Main() at IL_0015 (call): copying a span into a new array (ToArray) arrives later. For "
                + "now, copy it into an array of your own with CopyTo.",
            "Program.Main() at IL_0021 (localloc): stackalloc of a size known only when the program runs arrives later. "
                + "For now, give it a constant size, such as stackalloc byte[16].",
            "Program.Main() at IL_002d (localloc): stackalloc into a pointer (int* p = stackalloc int[4]) arrives later. "
                + "For now, make a span of it: Span<int> buffer = stackalloc int[4];",
            "Program.Main() at IL_0032 (initobj): collection expressions as spans (Span<int> s = [1, 2, 3]) arrive later. "
                + "For now, use stackalloc int[] { 1, 2, 3 }, or an array: Span<int> s = new int[] { 1, 2, 3 };",
            "Program.Main() at IL_003b (call): collection expressions as spans (Span<int> s = [1, 2, 3]) arrive later. "
                + "For now, use stackalloc int[] { 1, 2, 3 }, or an array: Span<int> s = new int[] { 1, 2, 3 };",
            "Program.Main() at IL_0045 (call): collection expressions as spans (Span<int> s = [1, 2, 3]) arrive later. "
                + "For now, use stackalloc int[] { 1, 2, 3 }, or an array: Span<int> s = new int[] { 1, 2, 3 };",
            "Program.Main() at IL_004f (call): collection expressions as spans (Span<int> s = [1, 2, 3]) arrive later. "
                + "For now, use stackalloc int[] { 1, 2, 3 }, or an array: Span<int> s = new int[] { 1, 2, 3 };"),
            string.Join("\n", problems));
    }

    [Fact]
    public void A_span_of_characters_compiles_and_MemoryMarshal_reads_only_plain_values()
    {
        var problems = Problems("""
            using System;
            using System.Runtime.InteropServices;

            struct Person
            {
                public string Name;
                public int Age;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            struct Packed
            {
                public byte Kind;
                public int Value;
            }

            class Program
            {

                static void Main()
                {
                    Span<char> letters = stackalloc char[2];
                    var person = MemoryMarshal.Read<Person>(new byte[16]);
                    var packed = MemoryMarshal.Read<Packed>(new byte[16]);
                    Console.WriteLine(letters.Length + person.Age + packed.Value);
                }
            }
            """);

        Assert.DoesNotContain(problems, problem => problem.Contains("V_0", StringComparison.Ordinal));
        Assert.Contains("Program.Main() at IL_0017 (call): MemoryMarshal reads and writes as bytes only values with no "
            + "references in them, laid out as C# lays them out by default, and Person is not one (the field Person.Name has "
            + "type string). Read and write numbers, bools, enums and structs of them, without [StructLayout(Pack = ...)].",
            problems);
        Assert.Contains("Program.Main() at IL_0029 (call): MemoryMarshal reads and writes as bytes only values with no "
            + "references in them, laid out as C# lays them out by default, and Packed is not one (Packed has a "
            + "[StructLayout] with Pack or Size, which ChipSharp does not lay out yet). Read and write numbers, bools, "
            + "enums and structs of them, without [StructLayout(Pack = ...)].", problems);
    }

    [Fact]
    public void A_static_array_viewed_as_a_ReadOnlySpan_stays_a_constant_table_and_one_viewed_as_a_Span_is_in_RAM()
    {
        var analysis = Check(Program("Console.WriteLine(Sum(Read) + Sum(Written) + Clear(Written));", """
            static readonly int[] Read = { 1, 2, 3 };
            static readonly int[] Written = { 4, 5, 6 };

            static int Sum(ReadOnlySpan<int> values) => values[0] + values[^1];

            static int Clear(Span<int> values)
            {
                values.Clear();
                return values.Length;
            }
            """));

        Assert.True(analysis.IsSupported, analysis.Details);
        Assert.Equal(["Program.Read: True", "Program.Written: False"],
            analysis.ToProgram().StaticArrays.Arrays.Select(array => $"{array.Field.DeclaringType.FullName}." +
                $"{array.Field.Name}: {array.IsConstant}"));
    }

    [Fact]
    public void On_a_chip_without_a_heap_spans_compile_and_what_needs_the_heap_says_so()
    {
        var tiny = new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name);
        var fine = WholeProgramPass.Check(Load(Program("""
            Span<byte> packet = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(packet, result);
            result = System.Runtime.InteropServices.MemoryMarshal.Read<int>(packet.Slice(1)) + Digits[result & 3];
            int[] values = { 1, 2, 3 };
            result += values.AsSpan(1).IndexOf(3);
            """, """
            static volatile int result;

            static ReadOnlySpan<byte> Digits => new byte[] { 0x3F, 0x06, 0x5B, 0x4F };
            """)), tiny);
        Assert.Empty(fine.Problems);

        var escapes = WholeProgramPass.Check(Load(Program("result = Make().Length;", """
            static volatile int result;

            static Span<int> Make()
            {
                var made = new int[4];
                return made;
            }
            """)), tiny);
        Assert.Equal(["Program.Make() at IL_000b (ret): a span of an array that leaves the method that creates the array "
            + "(returned, or stored where the caller sees it) needs a heap, and the target 'tiny' (2 KB RAM) has no heap. "
            + "Keep the span in the method that creates the array; the methods it calls can take the span as a parameter, "
            + "and return it."], escapes.Problems.Select(problem => problem.Line));
    }

    [Fact]
    public void The_members_ChipSharp_compiles_itself_are_listed_with_the_library_calls()
    {
        Assert.Contains("\n  Span<T>: Slice, CopyTo, TryCopyTo, Fill, Clear, its constructors, properties and operators\n",
            WholeProgram.LibraryCalls);
        Assert.Contains("\n  MemoryMarshal: Read, Write, TryRead, TryWrite, AsBytes, Cast, CreateSpan, CreateReadOnlySpan\n",
            WholeProgram.LibraryCalls);
        Assert.Contains("System.MemoryExtensions: IndexOf(System.Span<T>, T), IndexOf(System.ReadOnlySpan<T>, T),",
            LibraryCallDetails);
        Assert.Contains(", AsSpan(T[]), AsSpan(T[], int), AsSpan(T[], int, int), AsSpan(string), AsSpan(string, int), "
            + "AsSpan(string, int, int)\n", LibraryCallDetails);
        Assert.Contains("\n  System.Span<T>: new Span(T[]), new Span(T[], int, int), Length, IsEmpty, Empty, Slice(int), ",
            LibraryCallDetails);
        Assert.Equal(Spans.Members.Count, Spans.Members.Distinct().Count());
    }

    private static IlAssembly Load(string source, OptimizationLevel optimization = OptimizationLevel.Release) =>
        IlAssembly.Load(CSharpCompiler.Compile(source, "Test", OutputKind.ConsoleApplication, optimization), "Test.dll");

    /// <summary>
    /// A class Program with <paramref name="main"/> as its Main and <paramref name="members"/> beside it, and
    /// <paramref name="types"/> after it.
    /// </summary>
    private static string Program(string main, string members = "", string types = "") => $$"""
        using System;

        class Program
        {
            {{members.Replace("\n", "\n    ", StringComparison.Ordinal)}}

            static void Main()
            {
                {{main.Replace("\n", "\n        ", StringComparison.Ordinal)}}
            }
        }

        {{types}}
        """;
}
