using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Ir;
using ChipSharp.Tests.Llvm;
using ChipSharp.Tests.Lowering;

namespace ChipSharp.Tests.Classes;

/// <summary>
/// What the translation makes of classes (Lowering/MethodLowering.Classes.cs): newobject and the constructor for new,
/// fieldaddr on the object for its fields, the null check once per block that uses an object (never for this, nor for
/// what was just made), callvirt as the null check and a call, and null meeting an object where ways join.
/// </summary>
public sealed class ClassLoweringTests
{
    private const string Source = """
        struct Place
        {
            public Node Node;
            public int X;
        }

        class Node
        {
            public int Value;
            public Node Next;
            public Place Place;

            public Node(int value) => Value = value;

            public int Twice() => Value * 2;
        }

        static class C
        {
            static Node Make(int value) => new Node(value);

            static int Call(Node node) => node.Twice();

            static int Read(Node node, bool deep) => deep ? node.Next.Value : node.Value;

            static void Store(Node node, Node next)
            {
                node.Next = next;
                node.Place.X = 3;
            }

            static int Direct() => new Node(4).Twice();

            static int Joined(bool c, Node node) => Depth(c ? node : null);

            static int Depth(Node node) => node == null ? 0 : 1 + Depth(node.Next);
        }
        """;

    [Fact]
    public void New_is_newobject_then_the_constructor_whose_this_is_never_null_checked_and_objects_constructor_is_nothing()
    {
        var module = Lowered.Module(Source, "C.Make");

        Assert.Equal("""
            function ref C.Make(i32 %value)
            IL_0000:
              %0 = newobject Node
              call void Node..ctor(%0, %value)
              return %0

            function void Node..ctor(ref %this, i32 %value)
            IL_0000:
              %0 = fieldaddr Node %this, Value
              store i32 [%0], %value
              return

            """.ReplaceLineEndings("\n"), string.Join("\n", module.Functions.Select(TirPrinter.Print)));
        Assert.Equal(["System.Object", "Node"], module.TypeDescriptors.Select(type => type.Name));
    }

    [Fact]
    public void Callvirt_is_the_null_check_of_the_object_then_the_call_and_call_on_a_new_object_checks_nothing()
    {
        Assert.Equal("""
            function i32 C.Call(ref %node)
            IL_0000:
              nullcheck %node
              %0 = call i32 Node.Twice(%node)
              return %0

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.Call"));
        Assert.Equal("""
            function i32 C.Direct()
            IL_0000:
              %0 = newobject Node
              %1 = const i32 4
              call void Node..ctor(%0, %1)
              %2 = call i32 Node.Twice(%0)
              return %2

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.Direct"));
    }

    [Fact]
    public void A_field_is_reached_through_fieldaddr_on_the_object_after_one_null_check_in_each_block_that_uses_it()
    {
        Assert.Equal("""
            function i32 C.Read(ref %node, bool %deep)
            IL_0000:
              branch %deep, IL_000a, IL_0003
            IL_0003:
              nullcheck %node
              %0 = fieldaddr Node %node, Value
              %1 = load i32 [%0]
              return %1
            IL_000a:
              nullcheck %node
              %2 = fieldaddr Node %node, Next
              %3 = load ref [%2]
              nullcheck %3
              %4 = fieldaddr Node %3, Value
              %5 = load i32 [%4]
              return %5

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.Read"));

        // a struct in an object is reached through the field's address, which points into the object
        Assert.Equal("""
            function void C.Store(ref %node, ref %next)
            IL_0000:
              nullcheck %node
              %0 = fieldaddr Node %node, Next
              store ref [%0], %next
              %1 = fieldaddr Node %node, Place
              %2 = const i32 3
              %3 = fieldaddr Place %1, X
              store i32 [%3], %2
              return

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.Store"));
    }

    [Fact]
    public void Null_meeting_an_object_where_ways_join_goes_into_the_ref_stack_slot()
    {
        // the null comes first: it goes into the ptr slot (for text that may still come) and the ref slot, which the
        // join takes once the object comes too
        Assert.Equal("""
            function i32 C.Joined(bool %c, ref %node)
              $stack0_ptr: ptr
              $stack0_ref: ref
            IL_0000:
              branch %c, IL_0006, IL_0003
            IL_0003:
              %0 = zero ptr
              %1 = zero ref
              store $stack0_ptr, %0
              store $stack0_ref, %1
              jump IL_0007
            IL_0006:
              store $stack0_ref, %node
              jump IL_0007
            IL_0007:
              %2 = load ref $stack0_ref
              %3 = call i32 C.Depth(%2)
              return %3

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.Joined"));
    }

    [Fact]
    public void An_object_with_a_struct_holding_an_object_lists_both_references_for_the_collector()
    {
        var module = Lowered.Module(Source, "C.Make");
        var node = module.TypeDescriptors.Single(type => type.Name == "Node").ObjectType!;

        Assert.Equal("struct Node { ptr <type>, i32 Value, ref Next, Place Place }", TirPrinter.Print(module).Split('\n')
            .Single(line => line.StartsWith("struct Node", StringComparison.Ordinal)));
        Assert.Equal([8L, 12L], new TirLayout(4).ReferenceOffsets(node));
        Assert.Equal([16L, 24L], new TirLayout(8).ReferenceOffsets(node));
    }

    [Fact]
    public void Objects_and_structs_holding_objects_in_static_fields_are_the_collectors_static_roots()
    {
        var program = WholeProgramPass.Analyze(Lowered.Compile("""
            struct Place
            {
                public int X;
                public Node Node;
            }

            class Node
            {
                public int Value;
            }

            class Program
            {
                static int count;
                static Node head;
                static Place place;

                static void Main()
                {
                    head = new Node();
                    place.Node = head;
                    count = place.Node.Value;
                }
            }
            """));

        var ll = Assert.Single(new LlvmCodeGenerator().Generate(Lowered.Lower(program), LlvmTargets.Pico).Files).Content;

        Assert.Contains("@chipsharp.roots = private constant [2 x { ptr, i32 }] [{ ptr, i32 } { ptr @Program_head, i32 1 }, " +
            "{ ptr, i32 } { ptr getelementptr (i8, ptr @Program_place, i32 4), i32 1 }], align 4\n", ll);
        Assert.Contains("call void @chipsharp_heap_initialize(ptr @chipsharp.roots, i32 2)", ll);
    }
}
