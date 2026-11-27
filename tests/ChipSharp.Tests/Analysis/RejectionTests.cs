using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// What the pass rejects, and how it says so: one problem per place, in C# words, with a hint; the error names each
/// feature once, the details list every place.
/// </summary>
public sealed class RejectionTests
{
    private const string MultiDimensional =
        "For now, use a one-dimensional array and compute the index: row * width + column.";

    private const string Checked =
        "For now, leave out checked: ChipSharp's arithmetic wraps around on overflow, like unchecked C#.";

    private const string Loop = "For now, write it as a loop over the elements: for (int i = 0; i < array.Length; i++) { ... }.";

    private const string Source = """
        using System;

        class Program
        {
            static void Main()
            {
                int[,] grid = new int[3, 3];
                Console.WriteLine(Math.Abs(checked(grid.Length + 1)));
            }
        }
        """;

    [Fact]
    public void The_error_names_every_feature_once_with_its_places()
    {
        var error = Error(Source);

        Assert.Equal($"""
            Test.dll uses 4 C# features that ChipSharp does not compile yet (4 places):
              multi-dimensional arrays (int[,]) arrive later: 1 place in Program.Main() (IL_0002). {MultiDimensional}
              the library method System.Array.get_Length() is not available yet: 1 place in Program.Main() (IL_0007). {Loop}
              checked arithmetic arrives later: 1 place in Program.Main() (IL_000d). {Checked}
              the library method System.Math.Abs(int) is not available yet: 1 place in Program.Main() (IL_000e).
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
            Test.dll uses 4 C# features that ChipSharp does not compile yet (4 places):
              Program.Main() at IL_0002 (newobj): multi-dimensional arrays (int[,]) arrive later. {MultiDimensional}
              Program.Main() at IL_0007 (callvirt): the library method System.Array.get_Length() is not available yet. {Loop}
              Program.Main() at IL_000d (add.ovf): checked arithmetic arrives later. {Checked}
              Program.Main() at IL_000e (call): the library method System.Math.Abs(int) is not available yet.
            {LibraryCallDetails}
            """.ReplaceLineEndings("\n"), analysis.Details);
        Assert.Equal([2, 7, 13, 14], analysis.Problems.Select(problem => problem.Offset));
        Assert.All(analysis.Problems, problem => Assert.Equal("Program.Main", problem.Method.DisplayName));
    }

    [Fact]
    public void Problems_are_listed_method_by_method_in_the_programs_order()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static int Second() => new Random().Next();
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
            "Program.Second() at IL_0000 (newobj): the library type System.Random is not available yet.",
            "Program.Second() at IL_0005 (callvirt): the library method System.Random.Next() is not available yet.",
        ], problems);
    }

    [Fact]
    public void Creating_objects_of_the_library_and_calling_their_methods()
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
            "Program.Main() at IL_0001 (newobj): the library type System.Random is not available yet.",
            "Program.Main() at IL_0008 (callvirt): the library method System.Random.Next(int) is not available yet.",
        ], problems);
    }

    [Fact]
    public void Arrays()
    {
        var analysis = Check("""
            using System;

            class Program
            {
                static int[] Make(int size) => new int[size];

                static void Main()
                {
                    Console.WriteLine(Make(2).Length + new string[1].Length);
                }
            }
            """);

        // an array returned, or of a size known only when the program runs, is on the heap (see HeapArrays): fine for a
        // target with one, and on a target without one it needs a heap (see HeapArrayCheckTests); an array of strings is
        // an array of objects
        Assert.True(analysis.IsSupported, analysis.Details);
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
    public void Catching_exceptions_is_rejected_while_throw_and_try_finally_compile()
    {
        // throw and try/finally compile (TryFinally, Exceptions); a method that catches keeps its regions, and each of
        // its leaves and its rethrow is part of exceptions, which arrive later
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

                    try
                    {
                        return x;
                    }
                    finally
                    {
                        Console.WriteLine();
                    }
                }

                static void Main()
                {
                    try
                    {
                        Console.WriteLine(Check(1));
                    }
                    catch (InvalidOperationException)
                    {
                        throw;
                    }
                }
            }
            """);

        const string Exceptions = "exceptions arrive later. For now, report failures through return values.";
        Assert.Equal(
        [
            "Program.Main() at IL_0000: catching exceptions (try/catch) arrives later. For now, report failures through "
                + "return values: a throw stops the program, as an exception nothing catches does.",
            $"Program.Main() at IL_000b (leave.s): {Exceptions}",
            $"Program.Main() at IL_000e (rethrow): {Exceptions}",
        ], problems);
    }

    [Fact]
    public void Boxing_and_objects()
    {
        // an object compiles, and so do object's Equals and printing one (its ToString), and a value turned into one
        // (boxing, which needs a heap: see Boxing.BoxingCheckTests); a library struct boxed does not
        var problems = Problems("""
            using System;

            class Program
            {
                static object Wrap(int x) => x;

                static void Main()
                {
                    Console.WriteLine(Wrap(1));
                    Console.WriteLine(new object().Equals(null));
                    Console.WriteLine((object)TimeSpan.Zero);
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main() at IL_0020 (box): boxing a System.TimeSpan (using it as an object) arrives with M3 (the heap). "
                + "For now, box the numbers, enums and structs of the program, or keep the value in its own type.",
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
        // a struct's own methods compile, and so do the ones it has as an object (GetHashCode: ValueType's) and boxing it
        // (a heap use); its GetType does not
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
                    Console.WriteLine(counter.GetType() == null);
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main() at IL_0038 (call): the library method object.GetType() is not available yet.",
            "Program.Main() at IL_003e (call): the library method System.Type.op_Equality(System.Type, System.Type) is "
                + "not available yet.",
        ], problems);
    }

    [Fact]
    public void An_instance_method_is_followed_through_call_and_through_callvirt()
    {
        // Roslyn calls Get with call when the object is known not to be null (new Program()), else with callvirt; Set
        // only through callvirt
        var methods = MethodNames("""
            using System;

            class Program
            {
                int value;

                int Get() => value;

                void Set(int to) => value = to;

                static void Main()
                {
                    var program = new Program();
                    program.Set(2);
                    Console.WriteLine(program.Get());
                    Console.WriteLine(new Program().Get());
                }
            }
            """);

        Assert.Equal(["Program.Main", "Program..ctor", "Program.Set", "Program.Get"], methods);
    }

    [Fact]
    public void A_generic_method_over_a_class_compiles_once_for_each_type()
    {
        // over a number and over a class alike (Generics.Instantiations); over a class it needs the heap (TargetRuleTests)
        var methods = MethodNames("""
            using System;

            class Program
            {
                static T Id<T>(T value) => value;

                static void Main()
                {
                    Console.WriteLine(Id(5));
                    Console.WriteLine(Id<object>(null) == null);
                }
            }
            """);

        Assert.Equal(["Program.Main", "Program.Id<int>", "Program.Id<object>"], methods);
    }

    [Fact]
    public void A_method_of_a_generic_type_over_a_multi_dimensional_array()
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
                    Console.WriteLine(Box<int[,]>.Count(3));
                }
            }
            """);

        // an array as a type argument is what its arrays need: a multi-dimensional one arrives later
        Assert.Equal([$"Program.Main() at IL_000c (call): multi-dimensional arrays (int[,]) arrive later. {MultiDimensional}"],
            problems);
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
    public void A_string_array_parameter_compiles_anywhere_but_args_used_by_the_entry_point()
    {
        // a string[] is an array of objects; the entry point's args is left out, so it must stay unused (see
        // Top_level_statements_that_use_args)
        var analysis = Check("""
            class Program
            {
                static int Count(string[] names) => 0;

                static void Main()
                {
                    System.Console.WriteLine(Count(null));
                }
            }
            """);

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void Chars_and_enums()
    {
        // a char is its UTF-16 code unit and an enum its whole number, so both compile
        var analysis = Check("""
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

        Assert.True(analysis.IsSupported, analysis.Details);
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
    public void Function_pointers()
    {
        // delegates and lambdas compile (see the tests in Delegates); a function pointer does not
        var problems = Problems("""
            using System;

            unsafe class Program
            {
                static int Three() => 3;

                static void Main()
                {
                    delegate*<int> three = &Three;
                    Console.WriteLine(three());
                }
            }
            """);

        Assert.Contains(problems, line => line.EndsWith(
            "(ldftn): function pointers (delegate*, &Method) arrive later. For now, use a delegate (Func<int, int>, " +
            "Action), or call the method directly.", StringComparison.Ordinal));
    }
}
