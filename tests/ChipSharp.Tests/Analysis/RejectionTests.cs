using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// What the pass rejects, and how it says so: one problem per place, in C# words, with a hint; the error names each
/// feature once, the details list every place.
/// </summary>
public sealed class RejectionTests
{
    private const string Objects = "For now, use structs and static methods.";

    private const string Source = """
        using System;

        class Program
        {
            static void Main()
            {
                string[] words = new string[3];
                words[0] = "done";
                Console.WriteLine(Math.Abs(words[0].Length));
            }
        }
        """;

    [Fact]
    public void The_error_names_every_feature_once_with_its_places()
    {
        var error = Error(Source);

        Assert.Equal($"""
            Test.dll uses 3 C# features that ChipSharp does not compile yet (5 places):
              arrays of strings, objects or arrays arrive with M3 (the heap): 3 places in Program.Main() (IL_0001, IL_000d and IL_000f). For now, use arrays of numbers, bools, structs or enums.
              {Building}: 1 place in Program.Main() (IL_0010). {BuildingHint}
              the library method System.Math.Abs(int) is not available yet: 1 place in Program.Main() (IL_0015).
            {LibraryCalls}
            For every place, run: chipsharp check Test.dll --details
            """.ReplaceLineEndings("\n"), error.Message);
        Assert.Equal(Check(Source).Summary, error.Message);
    }

    [Fact]
    public void The_details_list_every_problem_on_a_line_of_its_own()
    {
        var analysis = Check(Source);

        Assert.Equal($"""
            Test.dll uses 3 C# features that ChipSharp does not compile yet (5 places):
              Program.Main() at IL_0001 (newarr): arrays of strings, objects or arrays arrive with M3 (the heap). For now, use arrays of numbers, bools, structs or enums.
              Program.Main() at IL_000d (stelem.ref): arrays of strings, objects or arrays arrive with M3 (the heap). For now, use arrays of numbers, bools, structs or enums.
              Program.Main() at IL_000f (ldelem.ref): arrays of strings, objects or arrays arrive with M3 (the heap). For now, use arrays of numbers, bools, structs or enums.
              Program.Main() at IL_0010 (callvirt): {Building}. {BuildingHint}
              Program.Main() at IL_0015 (call): the library method System.Math.Abs(int) is not available yet.
            {LibraryCallDetails}
            """.ReplaceLineEndings("\n"), analysis.Details);
        Assert.Equal([1, 13, 15, 16, 21], analysis.Problems.Select(problem => problem.Offset));
        Assert.All(analysis.Problems, problem => Assert.Equal("Program.Main", problem.Method.DisplayName));
    }

    [Fact]
    public void Problems_are_listed_method_by_method_in_the_programs_order()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static int Second() => "two".Length;
                static int First(int x) => checked(x + 1);

                static void Main()
                {
                    Console.WriteLine(First(1) + Second());
                }
            }
            """);

        Assert.Equal(
        [
            "Program.First(int) at IL_0002 (add.ovf): checked arithmetic arrives later. "
                + "For now, leave out checked: ChipSharp's arithmetic wraps around on overflow, like unchecked C#.",
            $"Program.Second() at IL_0005 (call): {Building}. {BuildingHint}",
        ], problems);
    }

    [Fact]
    public void Creating_objects_and_calling_their_methods()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(new Random(1).Next(10));
                }
            }
            """);

        Assert.Equal(
        [
            $"Program.Main() at IL_0001 (newobj): creating objects (new ...) arrives with M3 (the heap). {Objects}",
            "Program.Main() at IL_0008 (callvirt): instance or virtual method calls arrive with M3 (the heap). "
                + "For now, call static methods, or the methods of your own structs.",
        ], problems);
    }

    [Fact]
    public void Arrays()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static int[] Make(int size) => new int[size];

                static void Main()
                {
                    Console.WriteLine(Make(2).Length);
                }
            }
            """);

        // arrays live on the stack of the method that makes them: of a fixed size, and never returned
        Assert.Equal(
        [
            "Program.Make(int), return type (int[]): an array that leaves the method that creates it (returned, or "
                + "stored in a field or in another array) arrives with M3 (the heap). For now, keep each array in the "
                + "method that creates it; the methods it calls can take it as a parameter.",
            "Program.Make(int) at IL_0001 (newarr): an array whose size is known only when the program runs arrives "
                + "with M3 (the heap). For now, give the array a fixed size: a constant, such as new int[16].",
        ], problems);
    }

    [Fact]
    public void Fields()
    {
        // counter, its address and a float field compile; a field of the library does not
        var problems = Problems("""
            using System;
            using System.Threading;

            class Program
            {
                static int counter;
                static float scale;

                static void Main()
                {
                    Interlocked.Increment(ref counter);
                    scale = 2;
                    Console.WriteLine(counter + (int)nint.Zero);
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main() at IL_0005 (call): the library method System.Threading.Interlocked.Increment(ref int) "
                + "is not available yet.",
            "Program.Main() at IL_001a (ldsfld): the library field nint.Zero is not available yet. "
                + "For now, use constants or the static fields of your own types.",
        ], problems);
    }

    [Fact]
    public void Checked_arithmetic()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static int Twice(int x) => checked(x * 2);
                static long Signed(ulong x) => checked((long)x);

                static void Main()
                {
                    Console.WriteLine(Twice(3));
                    Console.WriteLine(Signed(4));
                }
            }
            """);

        const string Checked = "checked arithmetic arrives later. "
            + "For now, leave out checked: ChipSharp's arithmetic wraps around on overflow, like unchecked C#.";
        Assert.Equal(
        [
            $"Program.Twice(int) at IL_0002 (mul.ovf): {Checked}",
            $"Program.Signed(ulong) at IL_0001 (conv.ovf.i8.un): {Checked}",
        ], problems);
    }

    [Fact]
    public void Floating_point_beyond_the_supported_Math_methods_and_unchecked_conversions()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static double Half(int x) => x / 2.0;
                static float Third(float x) => x / 3f;

                static void Main()
                {
                    Console.WriteLine((int)Half(5));
                    Console.WriteLine(Math.Sin(Third(9f)));
                    Console.WriteLine(checked((int)Half(7)));
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main() at IL_0017 (call): the library method System.Math.Sin(double) is not available yet.",
            "Program.Main() at IL_0027 (conv.ovf.i4): checked arithmetic arrives later. For now, leave out " +
                "checked: ChipSharp's arithmetic wraps around on overflow, like unchecked C#.",
        ], problems);
    }

    [Fact]
    public void Exceptions_and_try_catch_finally()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static int Check(int x)
                {
                    if (x < 0)
                    {
                        throw new InvalidOperationException();
                    }

                    return x;
                }

                static void Main()
                {
                    try
                    {
                        Console.WriteLine(Check(1));
                    }
                    finally
                    {
                        Console.WriteLine();
                    }
                }
            }
            """);

        const string Exceptions = "exceptions arrive later. For now, report failures through return values.";
        Assert.Equal(
        [
            "Program.Main() at IL_0000: try/catch/finally arrive later. For now, report failures through return values; "
                + "using, lock and foreach over a collection make a try/finally too.",
            $"Program.Main() at IL_000b (leave.s): {Exceptions}",
            $"Program.Main() at IL_0012 (endfinally): {Exceptions}",
            $"Program.Check(int) at IL_0004 (newobj): creating objects (new ...) arrives with M3 (the heap). {Objects}",
            $"Program.Check(int) at IL_0009 (throw): {Exceptions}",
        ], problems);
    }

    [Fact]
    public void Boxing_and_objects()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static object Wrap(int x) => x;

                static void Main()
                {
                    Console.WriteLine(Wrap(1));
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main() at IL_0006 (call): the library method System.Console.WriteLine(object) is not available yet.",
            $"Program.Wrap(int), return type (object): classes and objects arrive with M3 (the heap). {Objects}",
            "Program.Wrap(int) at IL_0001 (box): boxing (using a value as an object) arrives with M3 (the heap). "
                + "For now, keep values in their own types, such as int and bool.",
        ], problems);
    }

    [Fact]
    public void Unsafe_code()
    {
        // the address of y as a reference compiles; as a pointer (a number), like sizeof, it does not
        var problems = Problems("""
            using System;

            struct Point
            {
                public int X;
            }

            class Program
            {
                static unsafe int Read(int* p) => *p;

                static unsafe void Main()
                {
                    int y = 1;
                    int* p = &y;
                    *p = 2;
                    Console.WriteLine(Read(&y) + sizeof(Point));
                }
            }
            """);

        const string Unsafe = "unsafe code (pointers, &x, sizeof, fixed) arrives later. "
            + "For now, use ref parameters, ref locals and structs.";
        Assert.Equal(
        [
            $"Program.Main() at IL_0004 (conv.u): {Unsafe}",
            $"Program.Main() at IL_0009 (conv.u): {Unsafe}",
            $"Program.Main() at IL_000f (sizeof): {Unsafe}",
            $"Program.Read(int*), parameter 'p' (int*): {Unsafe}",
        ], problems);
    }

    [Fact]
    public void Structs_used_as_objects()
    {
        // a struct's own methods compile; the ones it has as an object (GetHashCode), and boxing it, do not
        var problems = Problems("""
            using System;

            struct Counter
            {
                public int Next(int x) => x + 1;
            }

            class Program
            {
                static void Main()
                {
                    var counter = new Counter();
                    Console.WriteLine(counter.Next(1));
                    Console.WriteLine(counter.GetHashCode());
                    Console.WriteLine(counter);
                }
            }
            """);

        const string InstanceCalls = "instance or virtual method calls arrive with M3 (the heap). "
            + "For now, call static methods, or the methods of your own structs.";
        Assert.Equal(
        [
            $"Program.Main() at IL_0017 (constrained.): {InstanceCalls}",
            $"Program.Main() at IL_001d (callvirt): {InstanceCalls}",
            "Program.Main() at IL_0028 (box): boxing (using a value as an object) arrives with M3 (the heap). "
                + "For now, keep values in their own types, such as int and bool.",
            "Program.Main() at IL_002d (call): the library method System.Console.WriteLine(object) is not available yet.",
        ], problems);
    }

    [Fact]
    public void An_instance_method_is_followed_through_call_but_not_through_callvirt()
    {
        // Roslyn calls Get with call when the object is known not to be null (new Program()), else with callvirt
        var problems = Problems("""
            using System;

            class Program
            {
                int Get() => 1;

                static void Main()
                {
                    var program = new Program();
                    Console.WriteLine(program.Get());
                    Console.WriteLine(new Program().Get());
                }
            }
            """);

        const string NewObject = $"creating objects (new ...) arrives with M3 (the heap). {Objects}";
        Assert.Equal(
        [
            $"Program.Main() at IL_0000 (newobj): {NewObject}",
            "Program.Main() at IL_0005 (callvirt): instance or virtual method calls arrive with M3 (the heap). "
                + "For now, call static methods, or the methods of your own structs.",
            $"Program.Main() at IL_000f (newobj): {NewObject}",
            "Program.Get(): instance methods of classes arrive with M3 (the heap). For now, make the method static, or make the class a struct.",
        ], problems);
    }

    [Fact]
    public void A_generic_method()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static T Id<T>(T value) => value;

                static void Main()
                {
                    Console.WriteLine(Id(5));
                }
            }
            """);

        Assert.Equal(["Program.Id<T>(T): generic methods arrive with M3 (the heap). For now, write a method for each type you need."], problems);
    }

    [Fact]
    public void A_method_of_a_generic_type()
    {
        var problems = Problems("""
            using System;

            static class Box<T>
            {
                public static int Count(int x) => x;
            }

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(Box<long>.Count(3));
                }
            }
            """);

        Assert.Equal(["Box`1.Count(int): generic types arrive with M3 (the heap). For now, write a type for each type argument you need."], problems);
    }

    [Fact]
    public void A_library_call_that_is_not_known()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(Math.Max(1, 2));
                }
            }
            """);

        Assert.Equal(
            ["Program.Main() at IL_0002 (call): the library method System.Math.Max(int, int) is not available yet."],
            problems);
    }

    [Fact]
    public void Top_level_statements_that_use_args()
    {
        var problems = Problems("""
            using System;

            Console.WriteLine(args.Length);
            """);

        Assert.Equal(
        [
            "Program.<Main>$(string[]) at IL_0000 (ldarg.0): using the command-line arguments (args) arrives later. "
                + "For now, leave args unused: Main may take string[] args only if it never uses them.",
        ], problems);
    }

    [Fact]
    public void Main_that_stores_into_args()
    {
        var problems = Problems("""
            class Program
            {
                static void Main(string[] args)
                {
                    args = null;
                    System.Console.WriteLine(1);
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main(string[]) at IL_0001 (starg.s): using the command-line arguments (args) arrives later. "
                + "For now, leave args unused: Main may take string[] args only if it never uses them.",
        ], problems);
    }

    [Fact]
    public void A_string_array_parameter_is_only_allowed_on_the_entry_point()
    {
        var problems = Problems("""
            class Program
            {
                static int Count(string[] names) => 0;

                static void Main()
                {
                    System.Console.WriteLine(Count(null));
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Count(string[]), parameter 'names' (string[]): arrays of strings, objects or arrays arrive with M3 (the heap). "
                + "For now, use arrays of numbers, bools, structs or enums.",
        ], problems);
    }

    [Fact]
    public void Chars_but_not_enums()
    {
        // an enum is its whole number, so Hue compiles
        var problems = Problems("""
            using System;

            enum Color { Red, Green }

            class Program
            {
                static int Code(char c) => c;
                static int Hue(Color color) => (int)color;

                static void Main()
                {
                    Console.WriteLine(Code('A'));
                    Console.WriteLine(Hue(Color.Green));
                }
            }
            """);

        Assert.Equal(
            ["Program.Code(char), parameter 'c' (char): characters (char) arrive with M3 (the heap). For now, use whole numbers."],
            problems);
    }

    [Fact]
    public void A_method_without_a_body()
    {
        var problems = Problems("""
            using System;
            using System.Runtime.InteropServices;

            class Program
            {
                [DllImport("kernel32")]
                static extern uint GetTickCount();

                static void Main()
                {
                    Console.WriteLine(GetTickCount() > 0);
                }
            }
            """);

        Assert.Equal(["Program.GetTickCount(): methods without a body (extern, DllImport) arrive later. For now, write the method in C#."],
            problems);
    }

    [Fact]
    public void Lambdas()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static void Main()
                {
                    Func<int> three = () => 3;
                    Console.WriteLine(three());
                }
            }
            """);

        Assert.Contains(problems, line => line.EndsWith(
            "(ldftn): delegates, lambdas and function pointers arrive later. For now, call methods directly.",
            StringComparison.Ordinal));
        Assert.DoesNotContain(problems, line => line.StartsWith("Program.<>c", StringComparison.Ordinal));
    }
}
