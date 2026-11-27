using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Analysis;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Memory;

/// <summary>
/// The rule that lets the garbage collector run with interrupts on: an interrupt handler, and everything it runs
/// (methods it calls, virtual calls, delegates it invokes, library substitutes), stores no reference other than null in a
/// static field, a field of an object, an element of an array or through a ref. Reading objects, storing numbers in them,
/// and keeping references in its own locals and structs are fine.
/// </summary>
public sealed class InterruptStoreRuleTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>The rule as <c>chipsharp check</c> says it, after the place.</summary>
    public const string Rule = "an interrupt handler cannot store an object (a reference other than null) in a static " +
        "field, an object or an array. The garbage collector may be in the middle of a collection when the interrupt " +
        "comes, and runs on after it: an object moved from where it has not looked yet to where it has looked already " +
        "would be freed while still in use. Read objects in the handler, store numbers and bools (in volatile static " +
        "fields, or in the objects' fields), set references to null; let Main store the objects.";

    [Fact]
    public void A_handler_that_stores_references_breaks_the_rule_where_it_stores_them_and_in_what_it_runs()
    {
        var problems = Problems("""
            using System;
            using ChipSharp.Hardware;

            class Node
            {
                public int Value;
                public Node Next;
                public string Name;

                public void Link(Node other) => Next = other;
            }

            struct Pair
            {
                public Node First;
                public int Count;
            }

            static class Program
            {
                static volatile Node head;
                static volatile Node spare;
                static Node[] slots = new Node[4];
                static Pair pair;
                static volatile Action<Node> onNode;

                static void Main()
                {
                    head = new Node();
                    spare = new Node { Name = "spare" };
                    onNode = node => slots[1] = node;
                    Timing.EveryMilliseconds(1);
                }

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    head = spare;
                    head.Next = spare;
                    slots[0] = spare;
                    pair.First = spare;
                    spare.Name = head.Name;
                    head.Link(spare);
                    onNode(spare);
                }
            }
            """);
        output.WriteLine(string.Join("\n", problems.Order(StringComparer.Ordinal)));

        Assert.Equal(
        [
            $"Node.Link(Node) at IL_0002 (stfld): {Rule}",                       // a method the handler calls
            $"Program+<>c.<Main>b__5_0(Node) at IL_0007 (stelem.ref): {Rule}",  // the delegate it invokes
            $"Program.Tick() at IL_0009 (stsfld): {Rule}",                       // head = spare
            $"Program.Tick() at IL_001c (stfld): {Rule}",                        // head.Next = spare
            $"Program.Tick() at IL_002e (stelem.ref): {Rule}",                   // slots[0] = spare
            $"Program.Tick() at IL_003b (stfld): {Rule}",                        // pair.First = spare, a static struct
            $"Program.Tick() at IL_0053 (stfld): {Rule}",                        // spare.Name = head.Name, a string
        ], problems.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_handler_may_read_objects_store_numbers_in_them_set_references_to_null_and_keep_them_in_locals()
    {
        var problems = Problems("""
            using System;
            using ChipSharp.Hardware;

            class Node
            {
                public int Value;
                public Node Next;
            }

            struct Cursor
            {
                public Node At;
                public int Steps;
            }

            static class Program
            {
                static volatile Node head;
                static volatile Node pending;
                static int[] counts = new int[4];
                static Node[] slots = new Node[4];
                static volatile int total;

                static void Main()
                {
                    head = new Node { Next = new Node() };
                    pending = head;
                    Timing.EveryMilliseconds(1);
                }

                static int Walk(Node from)
                {
                    var cursor = new Cursor { At = from };
                    while (cursor.At != null)
                    {
                        cursor.At.Value++;
                        cursor.At = cursor.At.Next;
                        cursor.Steps++;
                    }

                    return cursor.Steps;
                }

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    total += Walk(head);
                    counts[total % 4]++;
                    slots[total % 4] = null;
                    pending = null;
                    Array.Copy(counts, 0, counts, 1, 3);
                    Array.Fill(counts, 7);
                }
            }
            """);

        Assert.Empty(problems);
    }

    [Fact]
    public void A_handler_that_boxes_a_value_makes_an_object()
    {
        var problems = Problems("""
            using System;
            using ChipSharp.Hardware;

            static class Program
            {
                static volatile int ticks;

                static void Main() => Timing.EveryMilliseconds(1);

                static bool Same(object a, object b) => a.Equals(b);

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    if (Same(ticks, 3))
                    {
                        ticks = 0;
                    }

                    ticks++;
                }
            }
            """);

        Assert.Equal(2, problems.Count);
        Assert.All(problems, problem => Assert.Matches(@"^Program\.Tick\(\) at IL_00[0-9a-f]{2} \(box\): an interrupt handler " +
            @"cannot create objects \(new \.\.\.\)\.", problem));
    }

    private static List<string> Problems(string source) =>
        [.. WholeProgramPass.Check(InterruptCheckTests.Load(source), PicoBuild.Pico).Problems.Select(problem => problem.Line)];
}
