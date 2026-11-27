using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Analysis;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Collections;

/// <summary>
/// What the whole-program pass says of try/finally, throw, arrays as interfaces and the collections: try/finally is plain
/// branches by the time anything reads a method's IL, a throw and the collections need a heap (on a target without one,
/// each place says so), and an interrupt handler that would make a collection's objects is rejected where it does.
/// </summary>
public sealed class CollectionCheckTests : IDisposable
{
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
    public void A_try_finally_is_plain_branches_with_a_continuation_only_where_a_finally_goes_on_two_ways()
    {
        var assembly = IlAssembly.Load(CSharpCompiler.Compile("""
            using System;

            static class Guarded
            {
                static void Once()
                {
                    try
                    {
                        Console.WriteLine(1);
                    }
                    finally
                    {
                        Console.WriteLine(2);
                    }
                }

                static int Twice(int x)
                {
                    try
                    {
                        if (x > 0)
                        {
                            return 1;
                        }

                        Console.WriteLine(x);
                    }
                    finally
                    {
                        Console.WriteLine(3);
                    }

                    return 0;
                }
            }
            """), "Test.dll");

        var once = Assert.Single(assembly.FindMethods("Guarded.Once"));
        var twice = Assert.Single(assembly.FindMethods("Guarded.Twice"));

        // what the IL has stays readable as it is; what ChipSharp compiles has no region, leave or endfinally left
        Assert.Single(once.BodyAsRead!.ExceptionRegions);
        foreach (var method in new[] { once, twice })
        {
            Assert.Empty(method.Body!.ExceptionRegions);
            Assert.DoesNotContain(method.Body.Instructions,
                instruction => instruction.OpCode is IlOpCode.Leave or IlOpCode.Leave_S or IlOpCode.Endfinally);
        }

        // a finally left one way only needs no continuation; one left two ways (return 1, and the end) has one, an int
        Assert.Equal(once.BodyAsRead.Locals.Count, once.Body!.Locals.Count);
        Assert.Equal([.. twice.BodyAsRead!.Locals, new IlLocal(twice.BodyAsRead.Locals.Count, IlType.Int32)], twice.Body!.Locals);
        Assert.Contains(twice.Body.Instructions, instruction => instruction.OpCode == IlOpCode.Switch);

        // every instruction of the IL keeps its offset; the stores and the switch come after it
        Assert.All(once.BodyAsRead.Instructions, read => Assert.Contains(once.Body.Instructions, folded => folded.Offset == read.Offset));
        Assert.True(twice.Body.CodeSize > twice.BodyAsRead.CodeSize);
    }

    [Fact]
    public void Throw_try_finally_using_foreach_and_the_collections_compile()
    {
        var analysis = WholeProgram.Check("""
            using System;
            using System.Collections.Generic;

            class Program
            {
                static void Main()
                {
                    var counts = new Dictionary<string, int>();
                    foreach (var word in new List<string> { "a", "b", "a" })
                    {
                        counts[word] = counts.GetValueOrDefault(word) + 1;
                    }

                    IEnumerable<int> numbers = new[] { 1, 2 };
                    foreach (var number in numbers)
                    {
                        Console.WriteLine(number);
                    }

                    if (counts.Count > 5)
                    {
                        throw new InvalidOperationException("too many words");
                    }
                }
            }
            """);

        Assert.True(analysis.IsSupported, analysis.Summary);
    }

    [Fact]
    public void On_a_target_without_a_heap_a_collection_a_throw_and_an_array_as_an_interface_need_one()
    {
        var analysis = WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile("""
            using System;
            using System.Collections.Generic;

            class Program
            {
                static int First(IReadOnlyList<int> values) => values[0];

                static int result;

                static void Main()
                {
                    var list = new List<int>();
                    list.Add(1);
                    result = list.Count + First(new[] { 4, 5 });
                    if (result > 10)
                    {
                        throw new InvalidOperationException("too big");
                    }
                }
            }
            """), "Test.dll"), Tiny);

        // each place of the program's that needs the heap says so (the library's code, which it reaches, aside)
        var lines = analysis.Groups.Select(group => group.Line).ToList();
        Assert.Contains(lines, line => line.StartsWith("creating objects (new ...) needs a heap, and the target 'tiny' (2 KB RAM) " +
            "has no heap", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("throw (an exception is an object) needs a heap, and the target 'tiny'",
            StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("arrays used as interfaces (an array as an IEnumerable<T>, " +
            "ICollection<T>, IList<T> or IReadOnlyList<T>) need a heap, and the target 'tiny'", StringComparison.Ordinal));
        Assert.All(analysis.Problems.Where(problem => problem.Unsupported.IsHeapUse || problem.Unsupported.NeedsHeap),
            problem => Assert.StartsWith("Program.", problem.Line, StringComparison.Ordinal));
    }

    [Fact]
    public void An_interrupt_handler_that_adds_to_a_list_would_make_its_array_and_is_rejected()
    {
        var analysis = WholeProgramPass.Check(Interrupts("""
            using System.Collections.Generic;
            using ChipSharp.Hardware;

            class Program
            {
                static readonly List<int> ticks = new List<int>();

                static void Main()
                {
                    Timing.EveryMilliseconds(10);
                    while (true)
                    {
                    }
                }

                [Interrupt(Event.Timer)]
                static void Tick() => ticks.Add(1);
            }
            """), PicoBuild.Pico);

        // List.Add grows its array, which the heap makes: the handler's call says so
        var problem = Assert.Single(analysis.Problems, problem => problem.Unsupported == Unsupported.AllocationInHandler);
        Assert.StartsWith("Program.Tick() at IL_0006 (callvirt): an interrupt handler cannot create objects", problem.Line,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Catching_an_exception_is_still_rejected_where_the_try_starts()
    {
        var analysis = WholeProgram.Check("""
            using System;

            class Program
            {
                static void Main()
                {
                    try
                    {
                        Console.WriteLine(int.Parse("12"));
                    }
                    catch (FormatException)
                    {
                        Console.WriteLine("not a number");
                    }
                }
            }
            """);

        var problem = Assert.Single(analysis.Problems, problem => problem.Unsupported == Unsupported.TryCatch);
        Assert.Equal("Program.Main() at IL_0000: catching exceptions (try/catch) arrives later. For now, report failures " +
            "through return values: a throw stops the program, as an exception nothing catches does.", problem.Line);
    }

    private static IlAssembly Interrupts(string source) => InterruptCheckTests.Load(source);
}
