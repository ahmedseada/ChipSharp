using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// The TIR of arrays on the stack (see StackArrays): an array is a ChipSharp.Array { ptr data, i32 length } passed by
/// value, its elements a slot of the method that makes it, and every element access checks its index first.
/// </summary>
public sealed class IlToTirArrayTests
{
    private const string Program = """
        using System;

        class Program
        {
            static int First(int[] values) => values[0];

            static void Bump(int[] values, int i) => values[i] += 5;

            static long At(long[] values, long index) => values[index];

            static int Pair()
            {
                int[] pair = { 5, -2 };
                return First(pair);
            }

            static int Table()
            {
                int[] table = { 0, 10, 0, 30, -1 };
                return First(table);
            }

            static bool Flags()
            {
                bool[] flags = new bool[3];
                flags[1] = true;
                return flags[1];
            }

            static int Empty()
            {
                int[] none = [];
                int[] zero = new int[0];
                return none.Length + zero.Length;
            }

            static int Count(int[] values)
            {
                int count = 0;
                for (int i = 0; i < values.Length; i++)
                {
                    count++;
                }

                return count;
            }

            static void Main()
            {
                Console.WriteLine(Pair() + Table() + Count(new int[2]));
                Bump(new int[2], 1);
                Console.WriteLine(At(new long[3], 2));
                Console.WriteLine(Flags());
                Console.WriteLine(Empty());
            }
        }
        """;

    private static readonly Lazy<TirModule> Module = new(() => Lowered.Module(Program));

    [Fact]
    public void An_array_parameter_is_a_ChipSharp_Array_and_an_element_is_checked_then_reached_through_its_data()
    {
        Assert.Equal("""
            function i32 Program.First(ChipSharp.Array %values)
            IL_0000:
              %0 = const i32 0
              call void ChipSharp.Array.CheckIndex(%values, %0)
              %1 = call ptr ChipSharp.Array.Data(%values)
              %2 = elementaddr i32 %1, %0
              %3 = load i32 [%2]
              return %3

            """.ReplaceLineEndings("\n"), Function("Program.First"));
    }

    [Fact]
    public void Length_is_the_arrays_length_as_IL_has_it_a_native_unsigned_int()
    {
        Assert.Contains("""
            IL_000e:
              %8 = load i32 $V_1
              %9 = call i32 ChipSharp.Array.Length(%values)
              %10 = uconvert i32 %9 to nint
              %11 = convert nint %10 to i32
              %12 = lt i32 %8, %11
              branch %12, IL_0006, IL_0014
            """.ReplaceLineEndings("\n"), Function("Program.Count"));
    }

    [Fact]
    public void Newarr_zeroes_a_slot_of_the_elements_and_makes_the_array_value_then_an_initializer_stores_each_element()
    {
        Assert.Equal("""
            function i32 Program.Pair()
              $array_IL_0001: [2 x i32]
            IL_0000:
              %0 = zero [2 x i32]
              store $array_IL_0001, %0
              %1 = addr $array_IL_0001
              %2 = const i32 2
              %3 = call ChipSharp.Array ChipSharp.Array.Make(%1, %2)
              %4 = const i32 0
              call void ChipSharp.Array.CheckIndex(%3, %4)
              %5 = call ptr ChipSharp.Array.Data(%3)
              %6 = elementaddr i32 %5, %4
              %7 = const i32 5
              store i32 [%6], %7
              %8 = const i32 1
              call void ChipSharp.Array.CheckIndex(%3, %8)
              %9 = call ptr ChipSharp.Array.Data(%3)
              %10 = elementaddr i32 %9, %8
              %11 = const i32 -2
              store i32 [%10], %11
              %12 = call i32 Program.First(%3)
              return %12

            """.ReplaceLineEndings("\n"), Function("Program.Pair"));
    }

    [Fact]
    public void An_initializer_from_data_stores_the_elements_that_are_not_zero_into_the_slot()
    {
        Assert.Equal("""
            function i32 Program.Table()
              $array_IL_0001: [5 x i32]
            IL_0000:
              %0 = zero [5 x i32]
              store $array_IL_0001, %0
              %1 = addr $array_IL_0001
              %2 = const i32 5
              %3 = call ChipSharp.Array ChipSharp.Array.Make(%1, %2)
              %4 = addr $array_IL_0001
              %5 = const i32 1
              %6 = elementaddr i32 %4, %5
              %7 = const i32 10
              store i32 [%6], %7
              %8 = const i32 3
              %9 = elementaddr i32 %4, %8
              %10 = const i32 30
              store i32 [%9], %10
              %11 = const i32 4
              %12 = elementaddr i32 %4, %11
              %13 = const i32 -1
              store i32 [%12], %13
              %14 = call i32 Program.First(%3)
              return %14

            """.ReplaceLineEndings("\n"), Function("Program.Table"));
    }

    [Fact]
    public void A_compound_assignment_goes_through_the_elements_address()
    {
        Assert.Equal("""
            function void Program.Bump(ChipSharp.Array %values, i32 %i)
            IL_0000:
              call void ChipSharp.Array.CheckIndex(%values, %i)
              %0 = call ptr ChipSharp.Array.Data(%values)
              %1 = elementaddr i32 %0, %i
              %2 = load i32 [%1]
              %3 = const i32 5
              %4 = add i32 %2, %3
              store i32 [%1], %4
              return

            """.ReplaceLineEndings("\n"), Function("Program.Bump"));
    }

    [Fact]
    public void A_long_index_is_checked_as_it_is_against_the_length()
    {
        // conv.ovf.i leaves the long as it is: the unsigned check stops every value it would reject
        Assert.Equal("""
            function i64 Program.At(ChipSharp.Array %values, i64 %index)
            IL_0000:
              call void ChipSharp.Array.CheckIndex(%values, %index)
              %0 = call ptr ChipSharp.Array.Data(%values)
              %1 = elementaddr i64 %0, %index
              %2 = load i64 [%1]
              return %2

            """.ReplaceLineEndings("\n"), Function("Program.At"));
    }

    [Fact]
    public void A_bool_element_is_a_byte_holding_0_or_1()
    {
        var function = Function("Program.Flags");

        Assert.Contains("$array_IL_0001: [3 x i8]", function);
        Assert.Contains("""
              %7 = const i8 1
              store i8 [%6], %7
            """.ReplaceLineEndings("\n"), function);
        Assert.Contains("""
              %11 = load i8 [%10]
              %12 = uconvert i8 %11 to i32
              %13 = const i32 0
              %14 = ne i32 %12, %13
              return %14
            """.ReplaceLineEndings("\n"), function);
    }

    [Fact]
    public void An_empty_array_has_no_elements_and_a_null_data()
    {
        var function = Function("Program.Empty");

        Assert.DoesNotContain("$array_", function);
        Assert.StartsWith("""
            function i32 Program.Empty()
              $V_0: ChipSharp.Array
            IL_0000:
              %0 = zero ChipSharp.Array
              %1 = zero ChipSharp.Array
              store $V_0, %1
            """.ReplaceLineEndings("\n"), function);
    }

    [Fact]
    public void The_array_functions_are_added_once_to_the_module_with_the_runtimes_IndexOutOfRange()
    {
        var module = Module.Value;
        var names = module.Functions.Select(f => $"{f.Kind} {f.Name}({string.Join(", ", f.Parameters.Select(p => p.Type))})")
            .Where(name => name.Contains("ChipSharp.Array.", StringComparison.Ordinal)
                || name.Contains("IndexOutOfRange", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        Assert.Equal(
        [
            "Defined ChipSharp.Array.CheckIndex(ChipSharp.Array, i32)",
            "Defined ChipSharp.Array.CheckIndex(ChipSharp.Array, i64)",
            "Defined ChipSharp.Array.Data(ChipSharp.Array)",
            "Defined ChipSharp.Array.Length(ChipSharp.Array)",
            "Defined ChipSharp.Array.Make(ptr, i32)",
            "External ChipSharp.Runtime.IndexOutOfRange()",
        ], names);
        Assert.Equal([StackArrays.ArrayType], module.FindStructTypes());
        Assert.Equal("""
            function void ChipSharp.Array.CheckIndex(ChipSharp.Array %array, i64 %index)
            entry:
              %0 = call i32 ChipSharp.Array.Length(%array)
              %1 = uconvert i32 %0 to i64
              %2 = ult i64 %index, %1
              branch %2, in_range, out_of_range
            in_range:
              return
            out_of_range:
              call void ChipSharp.Runtime.IndexOutOfRange()
              return

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module.Functions.Single(f =>
                f.Name == "ChipSharp.Array.CheckIndex" && f.Parameters[1].Type == TirType.I64)));
        Assert.Equal("""
            function ChipSharp.Array ChipSharp.Array.Make(ptr %data, i32 %length)
              $array: ChipSharp.Array
            entry:
              %0 = addr $array
              %1 = fieldaddr ChipSharp.Array %0, data
              store ptr [%1], %data
              %2 = fieldaddr ChipSharp.Array %0, length
              store i32 [%2], %length
              %3 = load ChipSharp.Array $array
              return %3

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module.Functions.Single(f => f.Name == "ChipSharp.Array.Make")));
        Assert.Equal("""
            function i32 ChipSharp.Array.Length(ChipSharp.Array %array)
              $array: ChipSharp.Array
            entry:
              store $array, %array
              %0 = addr $array
              %1 = fieldaddr ChipSharp.Array %0, length
              %2 = load i32 [%1]
              return %2

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module.Functions.Single(f => f.Name == "ChipSharp.Array.Length")));
    }

    [Fact]
    public void A_program_without_arrays_has_none_of_the_array_functions()
    {
        var module = Lowered.Module("""
            class Program
            {
                static void Main() => System.Console.WriteLine(42);
            }
            """);

        Assert.DoesNotContain(module.Functions, f => f.Name.StartsWith("ChipSharp.Array.", StringComparison.Ordinal));
        Assert.Empty(module.FindStructTypes());
    }

    [Fact]
    public void An_array_made_in_a_loop_is_zeroed_each_time_round()
    {
        var function = Lowered.Function("""
            class Program
            {
                static int Run()
                {
                    int sum = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        int[] counts = new int[2];
                        counts[i % 2]++;
                        sum += counts[0];
                    }

                    return sum;
                }
            }
            """, "Program.Run");

        // one slot for the newarr, zeroed where it runs: in the loop's body
        Assert.Single(function.Split('\n'), line => line.StartsWith("  $array_", StringComparison.Ordinal));
        Assert.Contains("""
            IL_0006:
              %2 = zero [2 x i32]
              store $array_IL_0007, %2
            """.ReplaceLineEndings("\n"), function);
    }

    [Fact]
    public void An_array_of_structs_holds_them_in_its_slot_and_an_elements_field_is_reached_through_its_address()
    {
        var function = Lowered.Function("""
            struct Point
            {
                public int X;
                public short Y;
            }

            class Program
            {
                static int Run()
                {
                    Point[] points = new Point[2];
                    points[1].Y = 5;
                    ref Point first = ref points[0];
                    first.X = points[1].Y;
                    return first.X;
                }
            }
            """, "Program.Run");

        Assert.Contains("""
              $array_IL_0001: [2 x Point]
            IL_0000:
              %0 = zero [2 x Point]
              store $array_IL_0001, %0
            """.ReplaceLineEndings("\n"), function);
        Assert.Contains("""
              %6 = call ptr ChipSharp.Array.Data(%4)
              %7 = elementaddr Point %6, %5
              %8 = const i16 5
              %9 = fieldaddr Point %7, Y
              store i16 [%9], %8
            """.ReplaceLineEndings("\n"), function);

        // ref Point first = ref points[0] is the element's address, and its field is reached through it
        Assert.Contains("""
              %13 = elementaddr Point %12, %11
            """.ReplaceLineEndings("\n"), function);
        Assert.EndsWith("""
              %21 = fieldaddr Point %13, X
              store i32 [%21], %20
              %22 = fieldaddr Point %13, X
              %23 = load i32 [%22]
              return %23

            """.ReplaceLineEndings("\n"), function);
    }

    [Fact]
    public void Breaking_the_rule_is_an_error_of_the_translation_too()
    {
        const string Source = """
            class C
            {
                static int[] Make() => new int[2];

                static int Sized(int n)
                {
                    int[] values = new int[n];
                    return values.Length;
                }

                static ref int First()
                {
                    int[] values = new int[2];
                    return ref values[0];
                }
            }
            """;

        Assert.Equal("Test.dll: C.Make(): it returns int32[], and an array that leaves the method that creates it " +
            "(returned, or stored in a field or in another array) arrives with the heap in M3.",
            Error(Source, "C.Make").Message);
        Assert.Equal("Test.dll: C.Sized(int32) at IL_0001: newarr: an array whose size is known only when the program " +
            "runs arrives with the heap in M3.", Error(Source, "C.Sized").Message);
        Assert.Equal("Test.dll: C.First() at IL_000c: ret: an array that leaves the method that creates it (returned, " +
            "or stored in a field or in another array) arrives with the heap in M3.", Error(Source, "C.First").Message);
    }

    private static string Function(string name) =>
        TirPrinter.Print(Assert.Single(Module.Value.Functions, f => f.Name == name));

    private static ChipSharpException Error(string source, string method) =>
        Assert.Throws<ChipSharpException>(() => IlToTir.Lower(Lowered.Program(Lowered.Compile(source), method)));
}
