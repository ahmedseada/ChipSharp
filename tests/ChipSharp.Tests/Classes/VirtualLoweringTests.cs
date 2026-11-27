using ChipSharp.Ir;
using ChipSharp.Tests.Lowering;

namespace ChipSharp.Tests.Classes;

/// <summary>
/// What the translation makes of virtual calls, interfaces and type tests (Lowering/MethodLowering.Virtual.cs): virtual
/// method tables whose slots a base class opens and its derived classes fill in, a call through one, a call with a
/// single target as a plain call, a call nothing can answer as the null check alone, interface maps and a call through
/// one, the type tests and casts, and only the overrides that can run.
/// </summary>
public sealed class VirtualLoweringTests
{
    private const string Shapes = """
        abstract class Shape
        {
            public abstract int Area();

            public virtual int Sides() => 0;

            public virtual int Corners() => Sides();
        }

        class Square : Shape
        {
            public override int Area() => 4;

            public override int Sides() => 4;

            public new virtual int Corners() => 40;
        }

        sealed class Cube : Square
        {
            public override int Area() => 24;

            public override int Corners() => 8;
        }

        class Circle : Shape
        {
            public override int Area() => 3;
        }

        class Triangle : Shape
        {
            public override int Area() => 2;
        }

        interface IShape
        {
            int Area();
        }

        class Program
        {
            static int Area(Shape shape) => shape.Area();

            static int Corners(Square square) => square.Corners();

            static int Cube(Cube cube) => cube.Area();

            static int Never(Triangle triangle) => triangle.Area();

            static int Interface(IShape shape) => shape.Area();

            static Square Cast(object thing) => (Square)thing;

            static bool Is(object thing) => thing is Triangle;

            static Shape Up(Square square) => square as Shape;

            static void Main()
            {
                System.Console.WriteLine(Area(new Square()) + Area(new Circle()) + Area(new Cube()) + Corners(new Square())
                    + Cube(new Cube()) + Never(null) + Interface(null) + (Cast(null) == null ? 1 : 0) + (Is(null) ? 1 : 0)
                    + (Up(null) == null ? 1 : 0));
            }
        }
        """;

    private const string Interfaces = """
        interface IShape
        {
            int Area();

            int Corners();
        }

        interface IRound
        {
            int Radius();
        }

        class Square : IShape
        {
            public int Area() => 4;

            int IShape.Corners() => 4;
        }

        class Big : Square, IShape
        {
            public new int Area() => 40;
        }

        class Circle : IShape, IRound
        {
            public int Area() => 3;

            public int Corners() => 0;

            public int Radius() => 1;
        }

        class Program
        {
            static int Area(IShape shape) => shape.Area();

            static int Corners(IShape shape) => shape.Corners();

            static int Radius(IRound round) => round.Radius();

            static bool Round(object thing) => thing is IRound;

            static void Main() => System.Console.WriteLine(Area(new Square()) + Area(new Big()) + Corners(new Circle())
                + Radius(new Circle()) + (Round(new Square()) ? 1 : 0));
        }
        """;

    [Fact]
    public void A_class_has_its_base_s_slots_then_its_own_and_an_override_takes_the_slot_of_what_it_overrides()
    {
        var module = Lowered.Module(Shapes);

        // Shape opens Area (slot 0), Square's new Corners opens slot 1, which Cube's override fills in; Shape's Sides and
        // Corners are called by no one, so they have no slot, and no Shape is ever made, so it has no table
        Assert.Equal(
        [
            "type System.Object = class System.Object",
            "type Shape = class Shape : System.Object",
            "type Square = class Square : Shape virtual [Square.Area, Square.Corners]",
            "type Circle = class Circle : Shape virtual [Circle.Area]",
            "type Cube = class Cube : Square virtual [Cube.Area, Cube.Corners]",
        ], TypeLines(module));
    }

    [Fact]
    public void A_virtual_call_loads_the_method_from_the_object_s_table_and_calls_it_indirectly()
    {
        var module = Lowered.Module(Shapes);

        Assert.Equal("""
            function i32 Program.Area(ref %shape)
            IL_0000:
              nullcheck %shape
              %0 = typeof %shape
              %1 = fieldaddr <type descriptor> %0, virtuals
              %2 = const i32 0
              %3 = elementaddr ptr %1, %2
              %4 = load ptr [%3]
              %5 = call indirect i32 %4(%shape)
              return %5

            function i32 Program.Corners(ref %square)
            IL_0000:
              nullcheck %square
              %0 = typeof %square
              %1 = fieldaddr <type descriptor> %0, virtuals
              %2 = const i32 1
              %3 = elementaddr ptr %1, %2
              %4 = load ptr [%3]
              %5 = call indirect i32 %4(%square)
              return %5

            """.ReplaceLineEndings("\n"), Functions(module, "Program.Area", "Program.Corners"));
    }

    [Fact]
    public void A_call_with_one_target_is_a_plain_call_and_one_with_none_is_the_null_check_alone()
    {
        var module = Lowered.Module(Shapes);

        // Cube is sealed; no Triangle and no IShape is ever made, so only null can be there
        Assert.Equal("""
            function i32 Program.Cube(ref %cube)
            IL_0000:
              nullcheck %cube
              %0 = call i32 Cube.Area(%cube)
              return %0

            function i32 Program.Never(ref %triangle)
            IL_0000:
              nullcheck %triangle
              %0 = zero i32
              return %0

            function i32 Program.Interface(ref %shape)
            IL_0000:
              nullcheck %shape
              %0 = zero i32
              return %0

            """.ReplaceLineEndings("\n"), Functions(module, "Program.Cube", "Program.Never", "Program.Interface"));
    }

    [Fact]
    public void Only_the_methods_a_call_can_run_on_an_object_the_program_makes_are_compiled()
    {
        var names = Lowered.Module(Shapes).Functions.Select(function => function.Name).ToList();

        Assert.Contains("Square.Corners", names);
        Assert.Contains("Cube.Corners", names);
        Assert.DoesNotContain("Triangle.Area", names);
        Assert.DoesNotContain("Shape.Sides", names);
        Assert.DoesNotContain("Shape.Corners", names);
        Assert.DoesNotContain("Square.Sides", names);
    }

    [Fact]
    public void A_cast_calls_the_test_a_test_no_object_can_pass_is_null_and_one_the_variable_passes_is_the_object()
    {
        var module = Lowered.Module(Shapes);

        Assert.Equal("""
            function ref Program.Cast(ref %thing)
            IL_0000:
              %0 = typeaddr Square
              %1 = call ref ChipSharp.Object.CastClass(%thing, %0)
              return %1

            function bool Program.Is(ref %thing)
            IL_0000:
              %0 = zero ref
              %1 = zero ref
              %2 = ugt ref %0, %1
              return %2

            function ref Program.Up(ref %square)
            IL_0000:
              return %square

            """.ReplaceLineEndings("\n"), Functions(module, "Program.Cast", "Program.Is", "Program.Up"));
    }

    [Fact]
    public void Each_class_has_a_method_table_for_each_interface_it_implements_that_a_call_or_a_test_needs()
    {
        var module = Lowered.Module(Interfaces);

        // Big implements IShape again with its own Area, and keeps Square's explicit Corners; a call of Radius can run
        // only Circle's, so IRound has no slot, but Circle has a table for it, which the type test looks for
        Assert.Equal(
        [
            "type System.Object = class System.Object",
            "type Square = class Square : System.Object IShape [Square.Area, Square.IShape.Corners]",
            "type Big = class Big : Square IShape [Big.Area, Square.IShape.Corners]",
            "type Circle = class Circle : System.Object IShape [Circle.Area, Circle.Corners] IRound []",
            "type IShape = interface",
            "type IRound = interface",
        ], TypeLines(module));
    }

    [Fact]
    public void A_call_through_an_interface_finds_its_table_in_the_object_s_interface_map()
    {
        var module = Lowered.Module(Interfaces);

        Assert.Equal("""
            function i32 Program.Area(ref %shape)
            IL_0000:
              nullcheck %shape
              %0 = typeof %shape
              %1 = typeaddr IShape
              %2 = call ptr ChipSharp.Object.FindInterface(%0, %1)
              %3 = const i32 0
              %4 = elementaddr ptr %2, %3
              %5 = load ptr [%4]
              %6 = call indirect i32 %5(%shape)
              return %6

            function i32 Program.Radius(ref %round)
            IL_0000:
              nullcheck %round
              %0 = call i32 Circle.Radius(%round)
              return %0

            """.ReplaceLineEndings("\n"), Functions(module, "Program.Area", "Program.Radius"));
    }

    private static string[] TypeLines(TirModule module) =>
        [.. TirPrinter.Print(module).Split('\n').Where(line => line.StartsWith("type ", StringComparison.Ordinal))];

    private static string Functions(TirModule module, params string[] names) =>
        string.Join("\n", names.Select(name => TirPrinter.Print(module.Functions.Single(function => function.Name == name))));
}
