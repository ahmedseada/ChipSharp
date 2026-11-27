using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Targets;
using ChipSharp.Tests.Analysis;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Classes;

/// <summary>
/// Virtual calls, interfaces of classes and type tests in the whole-program pass: what the program reaches (only the
/// overrides of classes it makes, through slots it calls), what it accepts for the PC and the Pico and without a heap,
/// and what it still rejects and how it says so.
/// </summary>
public sealed class VirtualCheckTests : IDisposable
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => VirtualPrograms.InReleaseAndDebug();

    [Theory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public void Each_program_passes_the_check_without_a_target_and_for_the_PC_and_the_Pico(string name, bool debug)
    {
        var assembly = VirtualPrograms.Compile(name, debug);

        foreach (var target in new TargetDescription?[] { null, Shipped.Load("host"), Shipped.Load("pico") })
        {
            var analysis = WholeProgramPass.Check(assembly, target);
            Assert.True(analysis.IsSupported, $"{name} for {target?.Name ?? "no target"}:\n{analysis.Details}");
        }
    }

    [Fact]
    public void Only_the_overrides_of_classes_the_program_makes_whose_slots_it_calls_are_reached()
    {
        var methods = WholeProgram.MethodNames("""
            class Shape
            {
                public virtual int Area() => 0;

                public virtual int Sides() => 0;
            }

            class Square : Shape
            {
                public override int Area() => 4;

                public override int Sides() => 4;
            }

            class Circle : Shape
            {
                public override int Area() => 3;
            }

            sealed class Big : Square
            {
                public override int Area() => 100;
            }

            class Program
            {
                static void Main()
                {
                    Shape shape = new Shape();
                    System.Console.WriteLine(shape.Area());
                    shape = new Square();
                    System.Console.WriteLine(shape.Area());
                }
            }
            """);

        // no Circle and no Big is made, and nothing calls Sides
        Assert.Equal(["Program.Main", "Shape..ctor", "Shape.Area", "Square..ctor", "Square.Area"], methods);
    }

    [Fact]
    public void A_class_made_after_the_call_brings_its_override_in_too()
    {
        var methods = WholeProgram.MethodNames("""
            class Shape
            {
                public virtual int Area() => 0;
            }

            class Square : Shape
            {
                public override int Area() => 4;
            }

            class Program
            {
                static void Main()
                {
                    System.Console.WriteLine(Measure(Make(true)));
                    System.Console.WriteLine(Measure(Make(false)));
                }

                static int Measure(Shape shape) => shape.Area();

                static Shape Make(bool square) => square ? Square() : new Shape();

                static Shape Square() => new Square();
            }
            """);

        Assert.Equal(["Program.Main", "Program.Make", "Program.Measure", "Program.Square", "Shape..ctor", "Shape.Area",
            "Square..ctor", "Square.Area"], methods.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_call_through_an_interface_reaches_what_each_class_made_runs_for_it_a_default_method_too()
    {
        var methods = WholeProgram.MethodNames("""
            interface IShape
            {
                int Area();

                int Twice() => Area() * 2;
            }

            class Square : IShape
            {
                int IShape.Area() => 4;
            }

            class Circle : IShape
            {
                public int Area() => 3;

                public int Twice() => 7;
            }

            class Triangle : IShape
            {
                public int Area() => 2;
            }

            class Program
            {
                static void Main()
                {
                    Show(new Square());
                    Show(new Circle());
                }

                static void Show(IShape shape) => System.Console.WriteLine(shape.Twice());
            }
            """);

        // Twice: the default method on a Square, Circle's own; Area, which the default method calls, of both classes
        // made (no Triangle is made)
        Assert.Equal(["Program.Main", "Square..ctor", "Program.Show", "Circle..ctor", "IShape.Twice", "Circle.Twice",
            "Square.IShape.Area", "Circle.Area"], methods);
    }

    [Fact]
    public void Equals_and_GetHashCode_overrides_are_reached_only_when_object_s_are_called()
    {
        const string Point = """
            class Point
            {
                public int X;

                public override bool Equals(object other) => other is Point point && point.X == X;

                public override int GetHashCode() => X;
            }

            class Program
            {
                static void Main()
                {
                    var a = new Point();
                    System.Console.WriteLine(CALL);
                }
            }
            """;

        Assert.Equal(["Program.Main", "Point..ctor"], WholeProgram.MethodNames(Point.Replace("CALL", "a.X")));
        Assert.Equal(["Program.Main", "Point..ctor", "Point.Equals"], WholeProgram.MethodNames(Point.Replace("CALL",
            "a.Equals(a)")));
        Assert.Equal(["Program.Main", "Point..ctor", "Point.GetHashCode"], WholeProgram.MethodNames(Point.Replace("CALL",
            "((object)a).GetHashCode()")));
    }

    [Fact]
    public void On_a_chip_without_a_heap_virtual_calls_interfaces_and_casts_compile_and_only_new_needs_one()
    {
        var analysis = WholeProgramPass.Check(Lowering.Lowered.Compile("""
            interface IShape
            {
                int Area();
            }

            abstract class Shape : IShape
            {
                public abstract int Area();
            }

            class Square : Shape
            {
                public override int Area() => 4;
            }

            class Program
            {
                static IShape none;
                static int area;

                static void Main()
                {
                    Shape shape = none as Shape;
                    area = shape == null ? 0 : shape.Area() + none.Area() + ((Square)none).Area();
                    area += new Square().Area();
                }
            }
            """), new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name));

        Assert.Equal(
        [
            "Program.Main() at IL_003c (newobj): creating objects (new ...) needs a heap, and the target 'tiny' (2 KB " +
                "RAM) has no heap. Use structs and static methods.",
        ], analysis.Problems.Select(problem => problem.Line));
    }

    [Fact]
    public void What_still_needs_generics_over_classes_or_the_library_is_rejected()
    {
        var problems = WholeProgram.Problems("""
            using System;

            class Node
            {
                public virtual T Pick<T>(T a, T b) => a;
            }

            class Program
            {
                static void Main()
                {
                    object thing = new Node();
                    Console.WriteLine(((Node)thing).Pick(1, 2));
                    Console.WriteLine(thing.ToString() == null);
                    Console.WriteLine(thing.GetType() == null);
                    Console.WriteLine(thing is string);
                    Console.WriteLine(thing is Random);
                    Console.WriteLine((int)thing);
                    Console.WriteLine(thing is int[,]);
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main() at IL_000d (callvirt): generic virtual methods (virtual void Run<T>(), or a generic method of " +
                "an interface) arrive with M3 (generics over classes). For now, make the method generic or virtual, not both.",
            "Program.Main() at IL_0026 (callvirt): typeof and type handles arrive later. For now, leave them out.",
            "Program.Main() at IL_002c (call): the library method System.Type.op_Equality(System.Type, System.Type) is " +
                "not available yet.",
            "Program.Main() at IL_0045 (isinst): type tests and casts to System.Random (is, as, (T)x) arrive with M3 (the " +
                "heap). For now, test objects against the program's own classes and interfaces.",
            "Program.Main() at IL_0053 (unbox.any): boxing (using a value as an object) arrives with M3 (the heap). For " +
                "now, keep values in their own types, such as int and bool.",
            "Program.Main() at IL_005d (isinst): type tests and casts to int[,] (is, as, (T)x) arrive with M3 (the heap). " +
                "For now, test objects against the program's own classes and interfaces.",
        ], problems);
    }

    [Fact]
    public void Printing_an_object_is_its_ToString_and_one_in_an_interpolated_hole_waits()
    {
        var problems = WholeProgram.Problems("""
            using System;

            interface IShape
            {
            }

            class Square : IShape
            {
            }

            class Program
            {
                static void Main()
                {
                    var square = new Square();
                    IShape shape = square;
                    Console.WriteLine(square);
                    Console.Write(shape);
                    Console.WriteLine($"{square}");
                }
            }
            """);

        const string ToText = "ToString of objects (and printing an object) arrives with M3 (stored strings). For now, " +
            "give the class a method that prints what it holds, such as Print(), and call that.";
        Assert.Equal(
        [
            $"Program.Main() at IL_001e (call): {ToText}",
        ], problems);
    }

    [Fact]
    public void An_interrupt_handler_may_not_make_an_object_in_an_override_its_virtual_call_runs()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load("""
            using ChipSharp.Hardware;

            class Step
            {
                public Step Next;

                public virtual void Grow()
                {
                }
            }

            class Growing : Step
            {
                public override void Grow() => Next = new Step();
            }

            class Program
            {
                static volatile Step current = new Growing();

                static void Main()
                {
                    current = new Step();
                    Timing.EveryMilliseconds(10);
                    while (true)
                    {
                    }
                }

                [Interrupt(Event.Timer)]
                static void Tick() => current.Grow();
            }
            """), PicoBuild.Pico);

        Assert.Equal(
        [
            "Growing.Grow() at IL_0001 (newobj): an interrupt handler cannot create objects (new ...). The heap may be in " +
                "the middle of an allocation or a collection when the interrupt comes: create what the handler needs " +
                "before, e.g. in a static field.",
        ], analysis.Problems.Select(problem => problem.Line));
    }
}
