using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Tuples;

/// <summary>
/// Tuples, collection expressions of text and objects, and simple iterators in the whole-program pass: what needs a heap
/// (a tuple's text, a span or an array of strings or objects) and what does not (a tuple's elements, its equality and
/// comparison, as a dictionary key's), and what is still rejected and how.
/// </summary>
public sealed class TupleCheckTests : IDisposable
{
    private const string NoHeapTarget = "and the target 'pico-no-heap' (264 KB RAM) has no heap.";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>A Pico whose target file says it has no heap (but its console).</summary>
    private TargetDescription NoHeap => new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
        ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");

    [Fact]
    public void A_tuple_needs_no_heap_and_its_text_needs_one()
    {
        var source = """
            using System;

            class Program
            {
                static (int Min, int Max) Range(int a, int b) => a < b ? (a, b) : (b, a);

                static void Main()
                {
                    var (min, max) = Range(7, 3);
                    var pair = (min, max);
                    Console.WriteLine(pair == (3, 7));
                    Console.WriteLine(pair.Equals((3, 7)) && pair.CompareTo((3, 8)) < 0);
                    Console.WriteLine(pair.GetHashCode() == (3, 7).GetHashCode());
                    Console.WriteLine(pair switch { (3, _) => min, _ => max });
                }
            }
            """;
        var withText = source.Replace("Console.WriteLine(pair == (3, 7));", "Console.WriteLine(pair);",
            StringComparison.Ordinal);

        Assert.True(Check(source, NoHeap).IsSupported, string.Join("\n", Check(source, NoHeap).Problems.Select(p => p.Line)));
        Assert.Contains(Problems(withText, NoHeap), line => line.StartsWith(
            "ChipSharp.Library.Tuples.ValueTuple<int, int>.ToString() at IL_003a (call): keeping built text in a variable " +
            "needs a heap", StringComparison.Ordinal));
    }

    [Fact]
    public void A_collection_expression_of_text_needs_a_heap_and_says_so_where_its_span_is()
    {
        var problems = Problems("""
            using System;
            using System.Collections.Generic;

            class Program
            {
                static void Main()
                {
                    Span<string> names = new string[] { "a" };
                    Console.WriteLine(names.Length);
                }
            }
            """, NoHeap);

        Assert.Contains($"Program.Main() at IL_000e (call): spans of strings, objects or arrays need a heap, {NoHeapTarget} " +
            "Use spans of numbers, bools, structs or enums.", problems);
    }

    [Fact]
    public void Spans_of_text_and_objects_compile_on_a_target_with_a_heap()
    {
        var analysis = Check("""
            using System;
            using System.Collections.Generic;

            class Lane
            {
            }

            class Program
            {
                static int Count(ReadOnlySpan<string> names) => names.Length;

                static void Main()
                {
                    var names = new[] { "a", "b", "c" };
                    Span<string> span = names;
                    span.Slice(1).Fill("z");
                    span[0] = "y";
                    Span<Lane> lanes = new Lane[2];
                    lanes.Clear();
                    List<string> list = ["p", .. names];
                    Console.WriteLine(Count(span) + list.Count + lanes.Length);
                }
            }
            """, null);

        Assert.True(analysis.IsSupported, string.Join("\n", analysis.Problems.Select(problem => problem.Line)));
    }

    /// <summary>
    /// An iterator with a foreach (or a try, or a using) inside has a fault handler, which C# runs only when an exception
    /// leaves it, so it never runs here: the iterator compiles. One that catches is still rejected, with catching
    /// exceptions.
    /// </summary>
    [Fact]
    public void An_iterator_with_a_foreach_inside_compiles_and_one_that_catches_is_rejected()
    {
        const string Source = """
            using System;
            using System.Collections.Generic;

            class Program
            {
                static IEnumerable<int> Twice(List<int> items)
                {
                    foreach (var item in items)
                    {
                        yield return item * 2;
                    }
                }

                static void Main()
                {
                    foreach (var item in Twice(new List<int> { 1 }))
                    {
                        Console.WriteLine(item);
                    }
                }
            }
            """;
        var catching = Source.Replace("yield return item * 2;",
            "yield return item * 2; try { Console.WriteLine(item); } catch { }", StringComparison.Ordinal);

        Assert.True(Check(Source, null).IsSupported, string.Join("\n", Check(Source, null).Problems.Select(p => p.Line)));
        Assert.Contains(Problems(catching, null), line => line.Contains("catching exceptions (try/catch) arrives later.",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// In a try block (a foreach over a collection, a using) C# makes the handler of a <c>$"..."</c> with newobj and
    /// stores it in its local: printed text all the same, which needs no heap.
    /// </summary>
    [Fact]
    public void Text_printed_in_a_try_block_needs_no_heap()
    {
        var analysis = Check("""
            using System;

            struct Guard : IDisposable
            {
                public int Pin;

                public void Dispose() => Console.WriteLine($"released {Pin}");
            }

            class Program
            {
                static void Main()
                {
                    for (var i = 0; i < 3; i++)
                    {
                        using var guard = new Guard { Pin = i };
                        Console.WriteLine($"pin {guard.Pin} of {3,2}: {i * 10:X2}");
                    }

                    try
                    {
                        Console.WriteLine($"{1} {2}");
                    }
                    finally
                    {
                        Console.WriteLine($"{3} {4}");
                    }
                }
            }
            """, NoHeap);

        Assert.True(analysis.IsSupported, string.Join("\n", analysis.Problems.Select(problem => problem.Line)));
    }

    private static AnalysisResult Check(string source, TargetDescription? target) =>
        WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile(source), "Test.dll"), target);

    private static string[] Problems(string source, TargetDescription? target)
    {
        var analysis = Check(source, target);
        Assert.False(analysis.IsSupported);
        return [.. analysis.Problems.Select(problem => problem.Line)];
    }
}
