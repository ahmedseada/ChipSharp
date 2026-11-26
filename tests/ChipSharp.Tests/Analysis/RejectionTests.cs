using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>What the pass rejects, and how it says so: one line per problem, in C# words, with a hint.</summary>
public sealed class RejectionTests
{
    private const string Objects = "For now, use static methods with whole-number and bool values.";
    private const string LibraryCalls = "M0 supports these library calls: System.Console.WriteLine(), "
        + "System.Console.WriteLine(int), System.Console.WriteLine(uint), System.Console.WriteLine(long), "
        + "System.Console.WriteLine(ulong), System.Console.WriteLine(bool), " + HardwareCalls + ".";

    private const string HardwareCalls = "ChipSharp.Hardware.Pin.Output(int), ChipSharp.Hardware.Pin.Input(int), "
        + "ChipSharp.Hardware.Pin.Write(int, bool), ChipSharp.Hardware.Pin.Toggle(int), ChipSharp.Hardware.Pin.Read(int), "
        + "ChipSharp.Hardware.Timing.DelayMilliseconds(int), ChipSharp.Hardware.Timing.DelayMicroseconds(int), "
        + "ChipSharp.Hardware.Registers.Read(uint), ChipSharp.Hardware.Registers.Write(uint, uint)";

    [Fact]
    public void Every_problem_is_reported_in_one_error_one_line_each()
    {
        var error = Error("""
            using System;

            class Program
            {
                static void Main()
                {
                    int[] numbers = new int[3];
                    numbers[0] = 42;
                    Console.WriteLine(numbers[0]);
                    Console.WriteLine("done");
                }
            }
            """);

        Assert.Equal($"""
            Test.dll uses C# that M0 does not compile yet (5 problems):
              Program.Main() at IL_0001 (newarr): arrays arrive after M0. For now, use separate variables.
              Program.Main() at IL_000a (stelem.i4): arrays arrive after M0. For now, use separate variables.
              Program.Main() at IL_000c (ldelem.i4): arrays arrive after M0. For now, use separate variables.
              Program.Main() at IL_0012 (ldstr): text strings arrive after M0. For now, print numbers and bools with Console.WriteLine.
              Program.Main() at IL_0017 (call): the library method System.Console.WriteLine(string) is not available in M0. {LibraryCalls}
            """.ReplaceLineEndings("\n"), error.Message);
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
            "Program.First(int) at IL_0002 (add.ovf): checked arithmetic arrives after M0. "
                + "For now, leave out checked: M0 arithmetic wraps around on overflow, like unchecked C#.",
            "Program.Second() at IL_0000 (ldstr): text strings arrive after M0. "
                + "For now, print numbers and bools with Console.WriteLine.",
            $"Program.Second() at IL_0005 (call): the library method string.get_Length() is not available in M0. {LibraryCalls}",
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
            $"Program.Main() at IL_0001 (newobj): creating objects (new ...) arrives after M0. {Objects}",
            "Program.Main() at IL_0008 (callvirt): instance or virtual method calls arrive after M0. "
                + "For now, call static methods of your own program.",
        ], problems);
    }

    [Fact]
    public void Text_strings()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static int Length(string text) => 0;

                static void Main()
                {
                    Console.WriteLine("Hello");
                    Console.WriteLine(Length(null));
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main() at IL_0000 (ldstr): text strings arrive after M0. For now, print numbers and bools with Console.WriteLine.",
            $"Program.Main() at IL_0005 (call): the library method System.Console.WriteLine(string) is not available in M0. {LibraryCalls}",
            $"Program.Main() at IL_000a (ldnull): null arrives after M0. {Objects}",
            "Program.Length(string), parameter 'text' (string): text strings arrive after M0. "
                + "For now, print numbers and bools with Console.WriteLine.",
        ], problems);
    }

    [Fact]
    public void Arrays()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static int Sum(int[] values) => values[0] + values.Length;

                static void Main()
                {
                    Console.WriteLine(Sum(new int[2]));
                }
            }
            """);

        const string Arrays = "arrays arrive after M0. For now, use separate variables.";
        Assert.Equal(
        [
            $"Program.Main() at IL_0001 (newarr): {Arrays}",
            $"Program.Sum(int[]), parameter 'values' (int[]): {Arrays}",
            $"Program.Sum(int[]) at IL_0002 (ldelem.i4): {Arrays}",
            $"Program.Sum(int[]) at IL_0004 (ldlen): {Arrays}",
        ], problems);
    }

    [Fact]
    public void Fields()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static int counter;

                static void Main()
                {
                    counter = counter + 1;
                    Console.WriteLine(counter);
                }
            }
            """);

        const string Fields = "fields arrive after M0. For now, keep values in local variables and pass them as parameters.";
        Assert.Equal(
        [
            $"Program.Main() at IL_0000 (ldsfld): {Fields}",
            $"Program.Main() at IL_0007 (stsfld): {Fields}",
            $"Program.Main() at IL_000c (ldsfld): {Fields}",
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

        const string Checked = "checked arithmetic arrives after M0. "
            + "For now, leave out checked: M0 arithmetic wraps around on overflow, like unchecked C#.";
        Assert.Equal(
        [
            $"Program.Twice(int) at IL_0002 (mul.ovf): {Checked}",
            $"Program.Signed(ulong) at IL_0001 (conv.ovf.i8.un): {Checked}",
        ], problems);
    }

    [Fact]
    public void Floating_point()
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
                    Console.WriteLine((int)Third(9f));
                }
            }
            """);

        const string Floats = "floating point (float/double) arrives after M0. "
            + "For now, use whole numbers: int, long and the other integer types.";
        Assert.Equal(
        [
            $"Program.Main() at IL_000c (ldc.r4): {Floats}",
            $"Program.Half(int), return type (double): {Floats}",
            $"Program.Half(int) at IL_0001 (conv.r8): {Floats}",
            $"Program.Half(int) at IL_0002 (ldc.r8): {Floats}",
            $"Program.Third(float), return type (float): {Floats}",
            $"Program.Third(float), parameter 'x' (float): {Floats}",
            $"Program.Third(float) at IL_0001 (ldc.r4): {Floats}",
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

        const string Exceptions = "exceptions arrive after M0. For now, report failures through return values.";
        Assert.Equal(
        [
            "Program.Main() at IL_0000: try/catch/finally arrive after M0. For now, report failures through return values; "
                + "using, lock and foreach over a collection make a try/finally too.",
            $"Program.Main() at IL_000b (leave.s): {Exceptions}",
            $"Program.Main() at IL_0012 (endfinally): {Exceptions}",
            $"Program.Check(int) at IL_0004 (newobj): creating objects (new ...) arrives after M0. {Objects}",
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
            $"Program.Main() at IL_0006 (call): the library method System.Console.WriteLine(object) is not available in M0. {LibraryCalls}",
            $"Program.Wrap(int), return type (object): classes and objects arrive after M0. {Objects}",
            "Program.Wrap(int) at IL_0001 (box): boxing (using a value as an object) arrives after M0. "
                + "For now, keep values in their own types, such as int and bool.",
        ], problems);
    }

    [Fact]
    public void Taking_addresses_and_ref_parameters()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static void Bump(ref int x) => x++;

                static void Main()
                {
                    int y = 1;
                    Bump(ref y);
                    Console.WriteLine(y);
                }
            }
            """);

        const string References = "references and pointers (ref, out, in, *) arrive after M0. For now, pass values in and return results.";
        Assert.Equal(
        [
            "Program.Main() at IL_0002 (ldloca.s): taking addresses (ref, out, struct methods) arrives after M0. "
                + "For now, pass values in and return results.",
            $"Program.Bump(ref int), parameter 'x' (ref int): {References}",
            $"Program.Bump(ref int) at IL_0002 (ldind.i4): {References}",
            $"Program.Bump(ref int) at IL_0005 (stind.i4): {References}",
        ], problems);
    }

    [Fact]
    public void Struct_locals_and_instance_methods()
    {
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
                }
            }
            """);

        const string Structs = "structs arrive after M0. For now, use separate whole-number and bool variables.";
        const string Addresses = "taking addresses (ref, out, struct methods) arrives after M0. For now, pass values in and return results.";
        Assert.Equal(
        [
            $"Program.Main(), local variable V_0 (Counter): {Structs}",
            $"Program.Main() at IL_0000 (ldloca.s): {Addresses}",
            $"Program.Main() at IL_0002 (initobj): {Structs}",
            $"Program.Main() at IL_0008 (ldloca.s): {Addresses}",
            "Counter.Next(int): instance methods arrive after M0. For now, make the method static.",
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

        const string NewObject = $"creating objects (new ...) arrives after M0. {Objects}";
        Assert.Equal(
        [
            $"Program.Main() at IL_0000 (newobj): {NewObject}",
            "Program.Main() at IL_0005 (callvirt): instance or virtual method calls arrive after M0. "
                + "For now, call static methods of your own program.",
            $"Program.Main() at IL_000f (newobj): {NewObject}",
            "Program.Get(): instance methods arrive after M0. For now, make the method static.",
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

        Assert.Equal(["Program.Id<T>(T): generic methods arrive after M0. For now, write a method for each type you need."], problems);
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

        Assert.Equal(["Box`1.Count(int): generic types arrive after M0. For now, put the method in a class that is not generic."], problems);
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
            [$"Program.Main() at IL_0002 (call): the library method System.Math.Max(int, int) is not available in M0. {LibraryCalls}"],
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
            "Program.<Main>$(string[]) at IL_0000 (ldarg.0): using the command-line arguments (args) arrives after M0. "
                + "For now, leave args unused: Main may take string[] args only if it never uses them.",
            "Program.<Main>$(string[]) at IL_0001 (ldlen): arrays arrive after M0. For now, use separate variables.",
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
            $"Program.Main(string[]) at IL_0000 (ldnull): null arrives after M0. {Objects}",
            "Program.Main(string[]) at IL_0001 (starg.s): using the command-line arguments (args) arrives after M0. "
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
            $"Program.Main() at IL_0000 (ldnull): null arrives after M0. {Objects}",
            "Program.Count(string[]), parameter 'names' (string[]): arrays arrive after M0. For now, use separate variables.",
        ], problems);
    }

    [Fact]
    public void Chars_and_enums()
    {
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
        [
            "Program.Code(char), parameter 'c' (char): characters (char) arrive after M0. For now, use whole numbers.",
            "Program.Hue(Color), parameter 'color' (Color): enums arrive after M0. For now, use int constants.",
        ], problems);
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

        Assert.Equal(["Program.GetTickCount(): methods without a body (extern, DllImport) arrive after M0. For now, write the method in C#."],
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
            "(ldftn): delegates, lambdas and function pointers arrive after M0. For now, call methods directly.",
            StringComparison.Ordinal));
        Assert.DoesNotContain(problems, line => line.StartsWith("Program.<>c", StringComparison.Ordinal));
    }
}
