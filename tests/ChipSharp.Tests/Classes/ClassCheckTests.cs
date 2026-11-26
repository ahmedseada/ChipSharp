using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Analysis;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Classes;

/// <summary>
/// Classes in the whole-program pass: what it accepts on a target with a heap (and without a target), what a target
/// without a heap says, what is rejected and how it says so, and the rule that no interrupt handler makes an object.
/// </summary>
public sealed class ClassCheckTests : IDisposable
{
    private const string Virtual = "virtual methods (virtual, override, abstract) arrive with M3 (virtual calls). For " +
        "now, leave out virtual, override and abstract, and give a derived class's method a name of its own.";

    private const string InHandler = "an interrupt handler cannot create objects (new ...). The heap may be in the " +
        "middle of an allocation or a collection when the interrupt comes: create what the handler needs before, e.g. " +
        "in a static field.";

    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => ClassPrograms.InReleaseAndDebug();

    [Theory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public void Each_program_passes_the_check_without_a_target_and_for_the_PC_and_the_Pico(string name, bool debug)
    {
        var assembly = ClassPrograms.Compile(name, debug);

        foreach (var target in new TargetDescription?[] { null, Shipped.Load("host"), Shipped.Load("pico") })
        {
            var analysis = WholeProgramPass.Check(assembly, target);
            Assert.True(analysis.IsSupported, $"{name} for {target?.Name ?? "no target"}:\n{analysis.Details}");
        }
    }

    [Fact]
    public void The_program_reaches_constructors_through_newobj_and_methods_through_callvirt()
    {
        var methods = WholeProgram.MethodNames("""
            class Shape
            {
                public int Sides;

                public Shape(int sides) => Sides = sides;

                public int Twice() => Sides * 2;
            }

            sealed class Square : Shape
            {
                public Square() : base(4)
                {
                }

                public int Area(int side) => side * side + Twice();
            }

            class Program
            {
                static void Main()
                {
                    var square = new Square();
                    System.Console.WriteLine(square.Area(3));
                }
            }
            """);

        Assert.Equal(["Program.Main", "Square..ctor", "Square.Area", "Shape..ctor", "Shape.Twice"], methods);
    }

    [Fact]
    public void On_a_chip_without_a_heap_each_place_that_makes_an_object_needs_one_and_the_rest_compiles()
    {
        var withoutHeap = Lowering.GenericPrograms.PicoWithoutHeap(_scratch);
        var analysis = WholeProgramPass.Check(ClassPrograms.Compile("Inheritance", debug: false), withoutHeap);

        const string NeedsHeap = "creating objects (new ...) needs a heap, and the target 'pico-without-heap' (264 KB " +
            "RAM) has no heap. Use structs and static methods.";
        Assert.Equal(
        [
            $"Program.Main() at IL_0006 (newobj): {NeedsHeap}",
            $"Program.Main() at IL_0012 (newobj): {NeedsHeap}",
            $"Program.Main() at IL_001e (newobj): {NeedsHeap}",
        ], analysis.Problems.Select(problem => problem.Line));

        // a class whose objects are never made needs no heap: its static members, and null, compile
        Assert.True(WholeProgramPass.Check(Lowered.Compile("""
            class Registry
            {
                public static int Count = 2;
                public int Value;

                public static int Read(Registry registry) => registry == null ? Count : registry.Value;
            }

            class Program
            {
                static Registry none;

                static void Main() => System.Console.WriteLine(Registry.Read(none));
            }
            """), withoutHeap).IsSupported);
    }

    [Fact]
    public void Virtual_override_and_abstract_methods_arrive_with_virtual_calls()
    {
        var problems = WholeProgram.Problems("""
            abstract class Shape
            {
                public abstract int Area();

                public virtual int Sides() => 0;
            }

            class Square : Shape
            {
                public override int Area() => 4;

                public sealed override int Sides() => 4;
            }

            class Program
            {
                static void Main()
                {
                    Shape shape = new Square();
                    System.Console.WriteLine(shape.Area() + shape.Sides());
                }
            }
            """);

        Assert.Equal(
        [
            $"Program.Main() at IL_0007 (callvirt): {Virtual}",
            $"Program.Main() at IL_000d (callvirt): {Virtual}",
            $"Shape.Area(): {Virtual}",
            $"Shape.Sides(): {Virtual}",
        ], problems);
    }

    [Fact]
    public void Equals_GetHashCode_and_ToString_of_objects_arrive_with_virtual_calls_or_with_built_text()
    {
        var analysis = WholeProgram.Check("""
            class Node
            {
                public int Value;
            }

            class Program
            {
                static void Main()
                {
                    var a = new Node();
                    var b = new Node();
                    System.Console.WriteLine(a.Equals(b));
                    System.Console.WriteLine(a.GetHashCode());
                    System.Console.WriteLine(a.ToString());
                }
            }
            """);

        Assert.Equal(
        [
            "ToString, Equals and GetHashCode of objects arrive with M3 (virtual calls): 2 places in Program.Main() " +
                "(IL_000d and IL_0018). For now, give the class a method of its own for it, such as Describe() or " +
                "SameAs(other), and call that.",
            $"{WholeProgram.Building}: 1 place in Program.Main() (IL_0022). {WholeProgram.BuildingHint}",
        ], analysis.Groups.Select(group => group.Line));
    }

    [Fact]
    public void Records_generic_classes_finalizers_and_classes_derived_from_library_classes_are_rejected_where_used()
    {
        var analysis = WholeProgram.Check("""
            using System;

            record class Point(int X, int Y);

            class Box<T>
            {
                public T Value;
            }

            class Resource
            {
                public int Handle;

                ~Resource() => Handle = 0;
            }

            class Failure : Exception
            {
            }

            class Wrapper : System.Collections.ArrayList
            {
            }

            class Letter
            {
                public char Code;
            }

            class Program
            {
                static void Main()
                {
                    var point = new Point(1, 2);
                    var box = new Box<int>();
                    var resource = new Resource();
                    var failure = new Failure();
                    var wrapper = new Wrapper();
                    var letter = new Letter();
                    Console.WriteLine(point.X + box.Value + resource.Handle + (failure == null ? 0 : 1) + (wrapper == null ? 0 : 1) + (letter == null ? 0 : 1));
                }
            }
            """);

        // the local of Box<int> comes first, then the code
        Assert.Equal(
        [
            "generic classes arrive with M3 (generics over classes)",
            "record classes arrive with M3 (virtual calls)",
            "finalizers (~ClassName()) arrive later",
            "exceptions arrive later",
            "classes derived from the library class System.Collections.ArrayList arrive later",
            "characters (char) in the field Letter.Code arrive with M3 (the heap)",
        ], analysis.Groups.Select(group => group.Problems[0].Unsupported.Heading).Distinct());
    }

    [Fact]
    public void Casts_type_tests_interfaces_of_classes_and_references_to_objects_are_rejected()
    {
        var problems = WholeProgram.Problems("""
            interface IShape
            {
                int Area();
            }

            class Square : IShape
            {
                public int Area() => 4;
            }

            class Program
            {
                static void Main()
                {
                    object thing = new Square();
                    if (thing is Square square)
                    {
                        System.Console.WriteLine(square.Area());
                    }

                    IShape shape = (IShape)thing;
                    System.Console.WriteLine(shape.Area());
                    Replace(ref thing);
                }

                static void Replace(ref object thing) => thing = null;
            }
            """);

        Assert.Equal(
        [
            "Program.Main() at IL_0007 (isinst): type tests and casts on objects (is, as, (T)x) arrive with M3 (the heap). " +
                "For now, use structs and static methods.",
            "Program.Main() at IL_001c (castclass): type tests and casts on objects (is, as, (T)x) arrive with M3 (the " +
                "heap). For now, use structs and static methods.",
            "Program.Main() at IL_0021 (callvirt): interfaces arrive with M3 (the heap). For now, implement the interface " +
                "on a struct and use it through a constraint: static void Run<T>(ref T device) where T : struct, IDevice.",
            "IShape.Area(): interfaces arrive with M3 (the heap). For now, implement the interface on a struct and use it " +
                "through a constraint: static void Run<T>(ref T device) where T : struct, IDevice.",
            "Program.Replace(ref object), parameter 'thing' (ref object): references to objects (ref, out and in of a " +
                "class, object or string) arrive with M3 (the heap). For now, return the object, or keep it in a field.",
            "Program.Replace(ref object) at IL_0002 (stind.ref): references to objects (ref, out and in of a class, object " +
                "or string) arrive with M3 (the heap). For now, return the object, or keep it in a field.",
        ], problems);
    }

    [Fact]
    public void A_library_type_is_not_available_and_a_class_holds_only_what_an_object_may()
    {
        var problems = WholeProgram.Problems("""
            using System;

            class Holder
            {
                public int[] Numbers;
            }

            class Program
            {
                static void Main()
                {
                    var random = new Random(1);
                    var holder = new Holder();
                    Console.WriteLine(random.Next(10) + (holder.Numbers == null ? 0 : 1));
                }
            }
            """);

        // an array in an object's field is an array that leaves the method that made it (arrays on the heap)
        Assert.Equal("Program.Main() at IL_0001 (newobj): the library type System.Random is not available yet.", problems[0]);
        Assert.StartsWith("Program.Main() at IL_0006 (newobj): an array that leaves the method that creates it (returned, " +
            "or stored in a field or in another array) in the field Holder.Numbers arrives with M3 (the heap).", problems[1]);
        Assert.Equal("Program.Main() at IL_000e (callvirt): the library method System.Random.Next(int) is not available yet.",
            problems[2]);
    }

    [Fact]
    public void A_question_mark_dot_that_gives_a_number_makes_a_nullable_which_is_not_compiled_yet()
    {
        // ?. that gives an object, or nothing, compiles (ClassPrograms.Basics); one that gives an int gives an int?
        var problems = WholeProgram.Problems("""
            class Node
            {
                public int Value;
            }

            class Program
            {
                static void Main()
                {
                    Node node = new Node();
                    int? value = node?.Value;
                    System.Console.WriteLine(value.HasValue);
                }
            }
            """);

        Assert.StartsWith("Program.Main(), local variable V_0 (int?): the type int? arrives later.", problems[0]);
    }

    [Fact]
    public void An_interrupt_handler_may_use_objects_but_not_make_one_in_anything_it_calls()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load("""
            using ChipSharp.Hardware;

            class Step
            {
                public int Pin;
                public Step Next;

                public Step(int pin) => Pin = pin;

                public Step Grow() => Next = new Step(Pin + 1);
            }

            class Program
            {
                static volatile Step current = new Step(2);

                static void Main()
                {
                    current.Grow();
                    Timing.EveryMilliseconds(10);
                    while (true)
                    {
                    }
                }

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    Pin.Toggle(current.Pin);
                    if (current.Next == null)
                    {
                        current = new Step(2);
                    }

                    Grow(current);
                }

                static void Grow(Step step) => step.Grow();
            }
            """), PicoBuild.Pico);

        // Main makes objects freely; the handler, and Step.Grow, which a method it calls calls, may not
        Assert.Equal(
        [
            $"Program.Tick() at IL_0020 (newobj): {InHandler}",
            $"Step.Grow() at IL_0009 (newobj): {InHandler}",
        ], analysis.Problems.Select(problem => problem.Line));
    }
}
