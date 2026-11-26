using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// Static fields in the whole-program pass: which ones it accepts, which type initializers (.cctor) become methods of
/// the program and in which order they run, and what it says about the fields it rejects.
/// </summary>
public sealed class StaticFieldCheckTests
{
    [Fact]
    public void The_static_fields_of_the_programs_own_types_compile_with_every_supported_type()
    {
        var analysis = Check("""
            using System;

            static class Settings
            {
                public static readonly int Limit;
                public static volatile bool Stop;

                static Settings()
                {
                    Limit = 10;
                }
            }

            class Program
            {
                static bool b = true;
                static sbyte i8 = -1;
                static byte u8 = 1;
                static short i16 = -2;
                static ushort u16 = 2;
                static int i32 = -3;
                static uint u32 = 3;
                static long i64 = -4;
                static ulong u64 = 4;
                static nint n = -5;
                static nuint u = 5;
                static int Count { get; set; }

                static void Main()
                {
                    Count++;
                    Settings.Stop = !b;
                    Console.WriteLine(i8 + u8 + i16 + u16 + i32 + Count + Settings.Limit);
                    Console.WriteLine(u32);
                    Console.WriteLine(i64 + (long)n);
                    Console.WriteLine(u64 + (ulong)u);
                    Console.WriteLine(Settings.Stop);
                }
            }
            """);

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void A_type_initializer_is_a_method_of_the_program_where_its_type_is_first_used()
    {
        // Main uses Program's field first, then calls Helper.Twice, which reads Table's field
        var names = MethodNames("""
            using System;

            static class Table
            {
                public static readonly int Size = Helper.Square(4);
            }

            static class Helper
            {
                public static int Square(int x) => x * x;

                public static int Twice(int x) => 2 * x + Table.Size;
            }

            class Program
            {
                static int start = 3;

                static void Main() => Console.WriteLine(Helper.Twice(start));
            }
            """);

        Assert.Equal(["Program.Main", "Program..cctor", "Helper.Twice", "Table..cctor", "Helper.Square"], names);
    }

    [Fact]
    public void Type_initializers_run_in_the_order_first_used_after_the_ones_they_use()
    {
        // B's initializer reads A's field: A first. C has a static constructor: calling C.Twice uses C.
        var program = Analyze("""
            using System;

            static class A
            {
                public static readonly int Value = Compute();

                static int Compute() => 6 * 7;
            }

            static class B
            {
                public static readonly int Value = A.Value + 1;
            }

            static class C
            {
                static int factor;

                static C()
                {
                    factor = A.Value / 21;
                }

                public static int Twice(int x) => x * factor;
            }

            static class D
            {
                public static readonly int Constant = 5;
            }

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(B.Value);
                    Console.WriteLine(C.Twice(D.Constant));
                }
            }
            """);

        Assert.Equal(["A..cctor", "B..cctor", "D..cctor", "C..cctor"],
            program.TypeInitializers.Select(method => method.DisplayName));
    }

    [Fact]
    public void Initializers_that_use_each_other_run_the_first_one_reached_last()
    {
        var program = Analyze("""
            static class A
            {
                public static int X;

                static A()
                {
                    X = B.Y + 1;
                }
            }

            static class B
            {
                public static int Y;

                static B()
                {
                    Y = A.X + 10;
                }
            }

            class Program
            {
                static void Main() => System.Console.WriteLine(A.X + B.Y);
            }
            """);

        Assert.Equal(["B..cctor", "A..cctor"], program.TypeInitializers.Select(method => method.DisplayName));
    }

    [Fact]
    public void Calling_a_method_uses_a_type_only_when_it_has_a_static_constructor()
    {
        // NoConstructor's initializer would be rejected (a string), but nothing reads its fields, so it never runs
        var program = Analyze("""
            using System;

            static class NoConstructor
            {
                static string name = "never used";

                public static int Twice(int x) => x * 2;
            }

            static class WithConstructor
            {
                static int seen;

                static WithConstructor()
                {
                    seen = 1;
                }

                public static int Twice(int x) => x * 2;
            }

            class Program
            {
                static void Main() => Console.WriteLine(NoConstructor.Twice(1) + WithConstructor.Twice(2));
            }
            """);

        Assert.Equal(["Program.Main", "WithConstructor..cctor", "NoConstructor.Twice", "WithConstructor.Twice"],
            program.Methods.Select(method => method.DisplayName));
        Assert.Equal(["WithConstructor..cctor"], program.TypeInitializers.Select(method => method.DisplayName));
    }

    [Fact]
    public void The_entry_points_type_is_used_when_it_has_a_static_constructor()
    {
        var program = Analyze("""
            class Program
            {
                static int ready;

                static Program()
                {
                    ready = 1;
                }

                static void Main() => System.Console.WriteLine(2);
            }
            """);

        Assert.Equal(["Program.Main", "Program..cctor"], program.Methods.Select(method => method.DisplayName));
        Assert.Equal(["Program..cctor"], program.TypeInitializers.Select(method => method.DisplayName));
    }

    [Fact]
    public void A_type_initializer_is_checked_like_any_method_and_named_as_CSharp_writes_it()
    {
        var analysis = Check("""
            using System;

            static class Settings
            {
                public static readonly long Ticks;

                static Settings()
                {
                    Ticks = checked(Factor * 1000);
                }

                static long Factor => 3;
            }

            class Program
            {
                static int length = "hello".Length;

                static void Main() => Console.WriteLine(length + Settings.Ticks);
            }
            """);

        Assert.Equal(
        [
            "text strings arrive after M0: 1 place in the static field initializers of Program (IL_0000). "
                + "For now, print numbers and bools with Console.WriteLine.",
            "the library method string.get_Length() is not available in M0: 1 place in the static field initializers "
                + "of Program (IL_0005).",
            "checked arithmetic arrives after M0: 1 place in the static constructor of Settings (IL_000b). "
                + "For now, leave out checked: M0 arithmetic wraps around on overflow, like unchecked C#.",
        ], analysis.Groups.Select(group => group.Line));
        Assert.Contains("\n  Program..cctor() at IL_0000 (ldstr): text strings arrive after M0.", analysis.Details);
    }

    [Fact]
    public void Fields_of_unsupported_types_are_problems_of_their_types()
    {
        var problems = Problems("""
            enum Color { Red, Green }

            struct Point
            {
                public int X;
            }

            static class Box<T>
            {
                public static int Count;
            }

            class Program
            {
                static string name;
                static double ratio;
                static Color color;
                static Point origin;
                static int[] table;
                static char letter;

                static void Main()
                {
                    name = name;
                    ratio = ratio;
                    color = color;
                    origin = origin;
                    table = table;
                    letter = letter;
                    Box<int>.Count = 1;
                }
            }
            """);

        const string Strings = "text strings arrive after M0. For now, print numbers and bools with Console.WriteLine.";
        const string Floats = "floating point (float/double) arrives after M0. "
            + "For now, use whole numbers: int, long and the other integer types.";
        const string Enums = "enums arrive after M0. For now, use int constants.";
        const string Structs = "structs arrive after M0. For now, use separate whole-number and bool variables.";
        // an array kept in a static field outlives every method, so it is the "leaves its method" case of stack arrays
        const string Arrays = "an array that leaves the method that creates it (returned, or stored in a field or in "
            + "another array) arrives with the heap in M3. For now, keep each array in the method that creates it; "
            + "the methods it calls can take it as a parameter.";
        const string Characters = "characters (char) arrive after M0. For now, use whole numbers.";
        Assert.Equal(
        [
            $"Program.Main() at IL_0000 (ldsfld): {Strings}",
            $"Program.Main() at IL_0005 (stsfld): {Strings}",
            $"Program.Main() at IL_000a (ldsfld): {Floats}",
            $"Program.Main() at IL_000f (stsfld): {Floats}",
            $"Program.Main() at IL_0014 (ldsfld): {Enums}",
            $"Program.Main() at IL_0019 (stsfld): {Enums}",
            $"Program.Main() at IL_001e (ldsfld): {Structs}",
            $"Program.Main() at IL_0023 (stsfld): {Structs}",
            $"Program.Main() at IL_0028 (ldsfld): {Arrays}",
            $"Program.Main() at IL_002d (stsfld): {Arrays}",
            $"Program.Main() at IL_0032 (ldsfld): {Characters}",
            $"Program.Main() at IL_0037 (stsfld): {Characters}",
            "Program.Main() at IL_003d (stsfld): generic types arrive after M0. "
                + "For now, put the method in a class that is not generic.",
        ], problems);
    }

    [Fact]
    public void Instance_fields_field_addresses_and_library_fields_are_rejected()
    {
        var problems = Problems("""
            using System;

            struct Point
            {
                public int X;
            }

            class Program
            {
                static Point origin;

                static void Main()
                {
                    origin.X = 5;
                    Console.WriteLine(BitConverter.IsLittleEndian);
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main() at IL_0000 (ldsflda): taking addresses (ref, out, struct methods) arrives after M0. "
                + "For now, pass values in and return results.",
            "Program.Main() at IL_0006 (stfld): instance fields (fields without static) arrive after M0. "
                + "For now, make the fields static.",
            "Program.Main() at IL_000b (ldsfld): the library field System.BitConverter.IsLittleEndian is not available "
                + "in M0. For now, use constants or the static fields of your own types.",
        ], problems);
    }
}
