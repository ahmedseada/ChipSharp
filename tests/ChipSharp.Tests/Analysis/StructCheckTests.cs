using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// Structs, enums and references in the whole-program pass: which ones it accepts, the struct constructors and methods
/// it follows, and what it says about the structs it rejects and about objects, which stay out.
/// </summary>
public sealed class StructCheckTests
{
    private const string Decimals = "For now, use bool, the whole-number types (sbyte to ulong, nint, nuint), float, double, "
        + "string (for fixed text), and the program's own structs and enums.";
    private const string Objects = "For now, use structs and static methods.";

    [Fact]
    public void Structs_enums_and_references_of_the_program_compile()
    {
        var analysis = Check("""
            using System;

            enum Color : byte { Red, Green }

            [Flags]
            enum Options { None = 0, Fast = 1, Safe = 2 }

            struct Point
            {
                public int X, Y;
                public Point(int x, int y) => (X, Y) = (x, y);
                public readonly int Sum => X + Y;
                public void Move(int by) => X += by;
            }

            readonly struct Line(Point from, Point to)
            {
                public Point From { get; } = from;
                public Point To { get; } = to;
                public Color Color { get; init; }
            }

            class Program
            {
                static Point origin;
                static Options options = Options.Fast | Options.Safe;

                static void Swap(ref Point a, ref Point b) => (a, b) = (b, a);

                static bool TryGet(int i, out Point point)
                {
                    point = new Point(i, i);
                    return i > 0;
                }

                static int Length(in Line line) => line.To.X - line.From.X;

                static ref Point Origin() => ref origin;

                static void Main()
                {
                    var p = new Point(1, 2);
                    p.Move(3);
                    var q = default(Point);
                    Swap(ref p, ref q);
                    origin.Y = 5;
                    Origin().X++;
                    if (TryGet(4, out var r) && (options & Options.Safe) != 0)
                    {
                        Console.WriteLine(Length(new Line(p, r) { Color = Color.Green }) + q.Sum);
                    }
                }
            }
            """);

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void A_struct_constructor_that_newobj_calls_is_a_method_of_the_program_and_so_is_a_class_constructor()
    {
        var problems = Check("""
            struct Point
            {
                public int X;
                public Point(int x) => X = Twice(x);
                static int Twice(int x) => 2 * x;
            }

            class Box
            {
                public Box(int size) => System.Console.WriteLine(size);
            }

            class Program
            {
                static void Main()
                {
                    System.Console.WriteLine(new Point(3).X);
                    new Box(1);
                }
            }
            """);

        Assert.Equal(["Program.Main", "Point..ctor", "Box..ctor", "Point.Twice"], problems.Methods.Select(m => m.DisplayName));
        Assert.Empty(problems.Problems);
    }

    [Fact]
    public void A_struct_with_a_static_constructor_is_set_up_when_newobj_first_uses_it()
    {
        var program = Analyze("""
            struct Id
            {
                public static int Next;
                public int Value;

                static Id()
                {
                    Next = 100;
                }

                public Id(int offset) => Value = Next++ + offset;
            }

            class Program
            {
                static void Main() => System.Console.WriteLine(new Id(1).Value);
            }
            """);

        Assert.Equal(["Id..cctor"], program.TypeInitializers.Select(method => method.DisplayName));
    }

    [Fact]
    public void A_struct_field_of_an_unsupported_type_is_that_types_problem_where_the_struct_is_used_naming_the_field()
    {
        // Outer has nothing wrong itself: its field Inner has a decimal (a string and a char are fine)
        var problems = Problems("""
            using System;

            struct Person
            {
                public decimal Name;
                public string Title;
                public int Age;

                public int Older(int years) => Age + years;
            }

            struct Inner
            {
                public char Id;
                public decimal Letter;
            }

            struct Outer
            {
                public long Stamp;
                public Inner Inner;
            }

            class Program
            {
                static int Age(Person person) => person.Age;

                static void Main()
                {
                    Person person = default;
                    Console.WriteLine(person.Older(1) + Age(person));
                    Outer outer = default;
                    Console.WriteLine(outer.Stamp);
                }
            }
            """);

        const string Name = $"the type decimal in the field Person.Name arrives later. {Decimals}";
        const string Letter = $"the type decimal in the field Inner.Letter arrives later. {Decimals}";
        Assert.Equal(
        [
            $"Program.Main(), local variable V_0 (Person): {Name}",
            $"Program.Main(), local variable V_1 (Outer): {Letter}",
            $"Program.Main() at IL_0002 (initobj): {Name}",
            $"Program.Main() at IL_001e (initobj): {Letter}",
            $"Program.Main() at IL_0025 (ldfld): {Letter}",
            $"Person.Older(int): {Name}",
            $"Person.Older(int) at IL_0001 (ldfld): {Name}",
            $"Program.Age(Person), parameter 'person' (Person): {Name}",
            $"Program.Age(Person) at IL_0001 (ldfld): {Name}",
        ], problems);
    }

    [Fact]
    public void Generic_library_and_overlapping_structs_fixed_buffers_and_ref_fields_are_features_of_their_own()
    {
        var analysis = Check("""
            using System;
            using System.Runtime.InteropServices;

            struct Box<T>
            {
                public T Value;
            }

            [StructLayout(LayoutKind.Explicit)]
            struct Union
            {
                [FieldOffset(0)] public int Whole;
                [FieldOffset(0)] public byte Low;
            }

            unsafe struct Buffer
            {
                public fixed byte Bytes[4];
                public int Length;
            }

            ref struct Window
            {
                public ref int Start;
            }

            class Program
            {
                static int Value(Box<object> box) => 0;
                static int Low(Union union) => 0;
                static int Length(Buffer buffer) => 0;
                static int Start(Window window) => 0;
                static int Days(DateTime date) => 0;

                static void Main() => Console.WriteLine(Value(default) + Low(default) + Length(default) + Start(default)
                    + Days(default));
            }
            """);

        // each default(...) is a local that initobj clears
        Assert.Equal(
        [
            "generics over classes (a class, an interface, object or an array as a type argument) arrive with M3 (the "
                + "heap): 3 places in Program.Main() (IL_0002 and local V_0) and Program.Value(Box<object>) (parameter "
                + "'box'). For now, use numbers, bools, enums and structs as type arguments: ChipSharp makes the code "
                + "once for each.",
            "structs with an explicit layout (LayoutKind.Explicit) arrive later: 3 places in Program.Main() "
                + "(IL_0010 and local V_1) and Program.Low(Union) (parameter 'union'). "
                + "For now, leave out [StructLayout(LayoutKind.Explicit)] and [FieldOffset].",
            "unsafe code (pointers, &x, sizeof, fixed) arrives later: 3 places in Program.Main() (IL_001f and local V_2) "
                + "and Program.Length(Buffer) (parameter 'buffer'). For now, use ref parameters, ref locals and structs.",
            "ref fields arrive later: 3 places in Program.Main() (IL_002e and local V_3) and Program.Start(Window) "
                + "(parameter 'window'). For now, keep references in parameters and locals.",
            "the type System.DateTime arrives later: 3 places in Program.Main() (IL_003d and local V_4) and "
                + "Program.Days(System.DateTime) (parameter 'date'). "
                + "For now, use bool, the whole-number types (sbyte to ulong, nint, nuint), float, double, string (for "
                + "fixed text), and the program's own structs and enums.",
        ], analysis.Groups.Select(group => group.Line));
    }

    [Fact]
    public void Objects_compile_next_to_structs_but_references_to_them_do_not_yet()
    {
        // a class's field, constructor and methods compile; a reference to a string does not (text itself compiles)
        var problems = Problems("""
            using System;

            class Holder
            {
                public int Count;

                public int Twice() => Count * 2;
            }

            class Program
            {
                static void Clear(ref string text) => text = null;

                static void Main()
                {
                    var holder = new Holder();
                    holder.Count = 3;
                    Console.WriteLine(holder.Twice());
                    string s = "x";
                    Clear(ref s);
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Clear(ref string), parameter 'text' (ref string): references to text (ref string, out string) "
                + "arrive with M3 (the heap). For now, return the text instead.",
            "Program.Clear(ref string) at IL_0002 (stind.ref): references to objects (ref, out and in of a class, object "
                + "or string) arrive with M3 (the heap). For now, return the object, or keep it in a field.",
        ], problems);
    }

    [Fact]
    public void A_class_instance_method_reached_through_call_compiles_like_a_struct_one_and_a_virtual_one_too()
    {
        var analysis = Check("""
            class Counter
            {
                public int Next() => 1;

                public virtual int Last() => 2;
            }

            struct Step
            {
                public int Size;

                public int Next() => Size + 1;
            }

            class Program
            {
                static void Main()
                {
                    System.Console.WriteLine(new Counter().Next());
                    System.Console.WriteLine(new Step().Next());
                    System.Console.WriteLine(new Counter().Last());
                }
            }
            """);

        Assert.True(analysis.IsSupported, analysis.Details);
    }
}
