using ChipSharp.Frontend.Il;
using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>Which methods make up the program, and in what order: the entry point first, then in the order they are first called.</summary>
public sealed class ReachabilityTests
{
    [Fact]
    public void The_entry_point_comes_first_then_methods_in_the_order_they_are_first_called()
    {
        // Main is scanned first and calls B, then A; B is scanned next and calls C; A calls nothing new
        var names = MethodNames("""
            using System;

            class Program
            {
                static int A(int x) => x - 1;
                static int C(int x) => x + 1;
                static int B(int x) => C(x) * 2;

                static void Main()
                {
                    Console.WriteLine(B(1));
                    Console.WriteLine(A(2));
                    Console.WriteLine(B(3));
                }
            }
            """);

        Assert.Equal(["Program.Main", "Program.B", "Program.A", "Program.C"], names);
    }

    [Fact]
    public void Methods_nothing_calls_are_left_out_and_not_checked()
    {
        // Unused would be rejected (a string, a double, an instance method), but nothing calls it
        var program = Analyze("""
            using System;

            class Program
            {
                static string Unused(double d) => d.ToString();
                int Instance() => 1;
                static int Used() => 2;

                static void Main()
                {
                    Console.WriteLine(Used());
                }
            }
            """);

        Assert.Equal(["Program.Main", "Program.Used"], program.Methods.Select(m => m.DisplayName));
        Assert.Equal(5, program.Assembly.Methods.Count);
    }

    [Fact]
    public void A_recursive_method_appears_once()
    {
        var names = MethodNames("""
            using System;

            class Program
            {
                static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

                static void Main()
                {
                    Console.WriteLine(Fib(10));
                    Console.WriteLine(Fib(20));
                }
            }
            """);

        Assert.Equal(["Program.Main", "Program.Fib"], names);
    }

    [Fact]
    public void Mutually_recursive_methods_appear_once_each()
    {
        var names = MethodNames("""
            using System;

            class Program
            {
                static bool IsEven(int n) => n == 0 || IsOdd(n - 1);
                static bool IsOdd(int n) => n != 0 && IsEven(n - 1);

                static void Main()
                {
                    Console.WriteLine(IsOdd(7));
                }
            }
            """);

        Assert.Equal(["Program.Main", "Program.IsOdd", "Program.IsEven"], names);
    }

    [Fact]
    public void A_method_called_from_several_places_appears_once_where_it_is_first_called()
    {
        var names = MethodNames("""
            using System;

            static class Helpers
            {
                public static int Twice(int x) => x * 2;
            }

            class Program
            {
                static int A(int x) => Helpers.Twice(x) + Last(x);
                static int B(int x) => Helpers.Twice(A(x));
                static int Last(int x) => x;

                static void Main()
                {
                    Console.WriteLine(B(1));
                    Console.WriteLine(Helpers.Twice(2));
                    Console.WriteLine(A(3));
                }
            }
            """);

        Assert.Equal(["Program.Main", "Program.B", "Helpers.Twice", "Program.A", "Program.Last"], names);
    }

    [Fact]
    public void Library_calls_are_not_methods_of_the_program()
    {
        var program = Analyze("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine();
                    Console.WriteLine(1);
                    Console.WriteLine(2u);
                    Console.WriteLine(3L);
                    Console.WriteLine(4UL);
                    Console.WriteLine(true);
                }
            }
            """);

        var main = Assert.Single(program.Methods);
        Assert.Same(program.Assembly.EntryPoint, main);
        Assert.Same(main, program.EntryPoint);
    }

    [Fact]
    public void Methods_of_other_and_nested_types_are_part_of_the_program()
    {
        var names = MethodNames("""
            namespace Demo
            {
                static class Outer
                {
                    public static class Inner
                    {
                        public static int Run(int x) => x + 1;
                    }
                }

                class Program
                {
                    static void Main()
                    {
                        System.Console.WriteLine(Outer.Inner.Run(41));
                    }
                }
            }
            """);

        Assert.Equal(["Demo.Program.Main", "Demo.Outer+Inner.Run"], names);
    }

    [Fact]
    public void Top_level_statements_that_do_not_use_args_pass_with_their_local_functions()
    {
        var program = Analyze("""
            using System;

            Console.WriteLine(Square(12));

            static int Square(int n) => n * n;
            """);

        Assert.Equal(["Program.<Main>$", "Program.<<Main>$>g__Square|0_0"], program.Methods.Select(m => m.DisplayName));
        Assert.Equal([new IlParameter(0, "args", new IlArrayType(IlType.String))], program.EntryPoint.Parameters);
    }

    [Fact]
    public void Main_may_declare_args_and_return_an_int_when_it_never_reads_args()
    {
        var program = Analyze("""
            class Program
            {
                static int Main(string[] args)
                {
                    System.Console.WriteLine(7);
                    return 0;
                }
            }
            """);

        Assert.Equal("Program.Main", Assert.Single(program.Methods).DisplayName);
    }

    [Fact]
    public void A_library_has_no_entry_point()
    {
        var error = Error("""
            public static class Library
            {
                public static int Square(int n) => n * n;
            }
            """);

        Assert.Equal(
            "Test.dll has no entry point: a program needs a static void Main() (or top-level statements) to start from.",
            error.Message);
    }
}
