using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Lowering;

/// <summary>The TIR that C# compiled by Roslyn (Release) becomes, as the printer writes it.</summary>
public sealed class IlToTirTests
{
    [Fact]
    public void Square_becomes_a_function_per_method_and_an_external_function_per_runtime_function()
    {
        var module = Lowered.Module("""
            using System;

            class Program
            {
                static int Square(int n)
                {
                    return n * n;
                }

                static void Main()
                {
                    Console.WriteLine(Square(42));
                }
            }
            """, assemblyName: "Square");

        Assert.Equal("""
            module Square
            entrypoint Program.Main

            function void Program.Main()
            IL_0000:
              %0 = const i32 42
              %1 = call i32 Program.Square(%0)
              call void ChipSharp.Runtime.WriteLineInt32(%1)
              return

            function i32 Program.Square(i32 %n)
            IL_0000:
              %0 = mul i32 %n, %n
              return %0

            external function void ChipSharp.Runtime.WriteLineInt32(i32 %value)

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module));
    }

    [Fact]
    public void Each_runtime_function_is_declared_once_however_often_it_is_called()
    {
        var module = Lowered.Module("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(1);
                    Console.WriteLine(2u);
                    Console.WriteLine(3);
                    Console.WriteLine();
                    Console.WriteLine(4L);
                    Console.WriteLine(5UL);
                    Console.WriteLine(true);
                }
            }
            """);

        Assert.Equal(
            ["ChipSharp.Runtime.WriteLineInt32", "ChipSharp.Runtime.WriteLineUInt32", "ChipSharp.Runtime.WriteLine",
                "ChipSharp.Runtime.WriteLineInt64", "ChipSharp.Runtime.WriteLineUInt64", "ChipSharp.Runtime.WriteLineBool"],
            module.Functions.Where(f => f.Kind == TirFunctionKind.External).Select(f => f.Name));
    }

    [Fact]
    public void Arithmetic_maps_one_to_one_and_a_division_by_a_variable_checks_the_divisor_first()
    {
        Assert.Equal("""
            function i32 C.Arithmetic(i32 %a, i32 %b)
            IL_0000:
              %0 = add i32 %a, %b
              %1 = mul i32 %a, %b
              %2 = sub i32 %0, %1
              %3 = call i32 ChipSharp.Division.Div(%2, %b)
              %4 = call i32 ChipSharp.Division.Rem(%3, %a)
              return %4

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static int Arithmetic(int a, int b) => (a + b - a * b) / b % a;
            }
            """, "C.Arithmetic"));
    }

    [Fact]
    public void Unsigned_division_remainder_and_shift_use_the_unsigned_operations_and_checks()
    {
        Assert.Equal("""
            function i32 C.Unsigned(i32 %a, i32 %b)
            IL_0000:
              %0 = call i32 ChipSharp.Division.DivUnsigned(%a, %b)
              %1 = call i32 ChipSharp.Division.RemUnsigned(%a, %b)
              %2 = add i32 %0, %1
              %3 = const i32 3
              %4 = ushr i32 %a, %3
              %5 = add i32 %2, %4
              return %5

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static uint Unsigned(uint a, uint b) => a / b + a % b + (a >> 3);
            }
            """, "C.Unsigned"));
    }

    [Fact]
    public void Not_is_xor_with_minus_one_and_neg_is_zero_minus()
    {
        Assert.Equal("""
            function i32 C.Bits(i32 %a, i32 %b)
            IL_0000:
              %0 = and i32 %a, %b
              %1 = const i32 -1
              %2 = xor i32 %b, %1
              %3 = xor i32 %a, %2
              %4 = or i32 %0, %3
              %5 = const i32 0
              %6 = sub i32 %5, %a
              %7 = or i32 %4, %6
              return %7

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static int Bits(int a, int b) => (a & b) | (a ^ ~b) | -a;
            }
            """, "C.Bits"));
    }

    [Fact]
    public void A_shift_amount_takes_the_type_of_the_value_shifted()
    {
        // C# masks a long's shift amount with 63; the int32 amount becomes an i64 (a constant one directly)
        Assert.Equal("""
            function i64 C.Shifts(i64 %a, i32 %n)
            IL_0000:
              %0 = const i32 63
              %1 = and i32 %n, %0
              %2 = uconvert i32 %1 to i64
              %3 = shl i64 %a, %2
              %4 = const i64 3
              %5 = shr i64 %3, %4
              return %5

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static long Shifts(long a, int n) => (a << n) >> 3;
            }
            """, "C.Shifts"));
    }

    [Fact]
    public void Comparisons_give_a_bool_that_is_widened_only_where_an_int32_is_needed()
    {
        // ceq compares the two 0-or-1 int32s; the bool it gives is returned as it is
        Assert.Equal("""
            function bool C.Compare(i32 %a, i32 %b, i32 %c, i32 %d)
            IL_0000:
              %0 = lt i32 %a, %b
              %1 = ugt i32 %c, %d
              %2 = convert bool %0 to i32
              %3 = convert bool %1 to i32
              %4 = eq i32 %2, %3
              return %4

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static bool Compare(int a, int b, uint c, uint d) => a < b == c > d;
            }
            """, "C.Compare"));
    }

    [Fact]
    public void Conditional_branches_compare_signed_or_unsigned_as_IL_says()
    {
        // blt.s for the ints, bge.un.s for the uints
        Assert.Equal("""
            function i32 C.Pick(i32 %a, i32 %b, i32 %c, i32 %d)
            IL_0000:
              %0 = lt i32 %a, %b
              branch %0, IL_0006, IL_0004
            IL_0004:
              %1 = const i32 1
              return %1
            IL_0006:
              %2 = uge i32 %c, %d
              branch %2, IL_000c, IL_000a
            IL_000a:
              %3 = const i32 2
              return %3
            IL_000c:
              %4 = const i32 3
              return %4

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static int Pick(int a, int b, uint c, uint d)
                {
                    if (a >= b)
                    {
                        return 1;
                    }

                    if (c < d)
                    {
                        return 2;
                    }

                    return 3;
                }
            }
            """, "C.Pick"));
    }

    [Fact]
    public void Bool_parameters_and_results_stay_bool_and_branches_test_them_directly()
    {
        var module = Lowered.Module("""
            using System;

            class Program
            {
                static bool Check(int id, bool answer)
                {
                    Console.WriteLine(id);
                    return answer;
                }

                static void Main()
                {
                    if (Check(1, false))
                    {
                        Console.WriteLine(true);
                    }
                }
            }
            """);

        Assert.Equal("""
            function void Program.Main()
            IL_0000:
              %0 = const i32 1
              %1 = const bool false
              %2 = call bool Program.Check(%0, %1)
              branch %2, IL_0009, IL_000f
            IL_0009:
              %3 = const i32 1
              call void ChipSharp.Runtime.WriteLineBool(%3)
              jump IL_000f
            IL_000f:
              return

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module.Functions[0]));
        Assert.Equal("""
            function bool Program.Check(i32 %id, bool %answer)
            IL_0000:
              call void ChipSharp.Runtime.WriteLineInt32(%id)
              return %answer

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module.Functions[1]));
    }

    [Fact]
    public void Small_integers_widen_by_their_own_sign_when_loaded_and_keep_their_low_bits_when_stored()
    {
        Assert.Equal("""
            function i8 C.Small(i8 %a, i8 %b, i16 %c, i16 %d)
            IL_0000:
              %0 = convert i8 %a to i32
              %1 = uconvert i8 %b to i32
              %2 = add i32 %0, %1
              %3 = convert i32 %2 to i8
              %4 = uconvert i8 %3 to i32
              %5 = convert i16 %c to i32
              %6 = add i32 %4, %5
              %7 = uconvert i16 %d to i32
              %8 = add i32 %6, %7
              %9 = convert i32 %8 to i8
              %10 = convert i8 %9 to i32
              %11 = convert i32 %10 to i8
              return %11

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static sbyte Small(sbyte a, byte b, short c, ushort d)
                {
                    byte e = (byte)(a + b);
                    return (sbyte)(e + c + d);
                }
            }
            """, "C.Small"));
    }

    [Fact]
    public void Conversions_truncate_and_extend_as_ECMA_335_says()
    {
        // conv.i1 = keep 8 bits, sign-extend back; conv.u1 the same with zero extension; conv.u4 then conv.u8 and
        // conv.u8 of a uint zero-extend; conv.i8 of an int sign-extends
        Assert.Equal("""
            function i64 C.Conv(i32 %a, i64 %b, i32 %c)
            IL_0000:
              %0 = convert i32 %a to i8
              %1 = convert i8 %0 to i32
              %2 = convert i32 %a to i8
              %3 = uconvert i8 %2 to i32
              %4 = add i32 %1, %3
              %5 = convert i32 %a to i16
              %6 = convert i16 %5 to i32
              %7 = add i32 %4, %6
              %8 = convert i32 %a to i16
              %9 = uconvert i16 %8 to i32
              %10 = add i32 %7, %9
              %11 = convert i64 %b to i32
              %12 = add i32 %10, %11
              %13 = convert i32 %12 to i64
              %14 = convert i64 %b to i32
              %15 = uconvert i32 %14 to i64
              %16 = add i64 %13, %15
              %17 = uconvert i32 %c to i64
              %18 = add i64 %16, %17
              %19 = convert i32 %a to i64
              %20 = add i64 %18, %19
              return %20

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static long Conv(int a, long b, uint c) =>
                    (sbyte)a + (byte)a + (short)a + (ushort)a + (int)b + (uint)b + (long)c + a;
            }
            """, "C.Conv"));
    }

    [Fact]
    public void Native_ints_take_int32_operands_sign_extended_and_conv_u_zero_extends()
    {
        Assert.Equal("""
            function nint C.Native(nint %a, i32 %b, nint %c)
            IL_0000:
              %0 = convert i32 %b to nint
              %1 = add nint %a, %0
              %2 = const nint 1
              %3 = ushr nint %c, %2
              %4 = add nint %1, %3
              %5 = uconvert i32 %b to nint
              %6 = add nint %4, %5
              return %6

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static nint Native(nint a, int b, nuint c) => a + b + (nint)(c >> 1) + (nint)(uint)b;
            }
            """, "C.Native"));
    }

    [Fact]
    public void Constants_are_written_in_the_type_they_end_up_as()
    {
        // ldc.i4.5 conv.i8 is one i64 constant, ldc.i4.m1 conv.i8 is ulong.MaxValue
        Assert.Equal("""
            function i64 C.Constants(bool %x)
              $V_0: i64
              $V_1: i64
            IL_0000:
              %0 = const i64 5
              store $V_0, %0
              %1 = const i64 -1
              store $V_1, %1
              %2 = const i32 1
              %3 = convert bool %x to i32
              %4 = and i32 %2, %3
              %5 = const i32 0
              %6 = ne i32 %4, %5
              branch %6, IL_000d, IL_000b
            IL_000b:
              %7 = load i64 $V_1
              return %7
            IL_000d:
              %8 = load i64 $V_0
              return %8

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static ulong Constants(bool x)
                {
                    long a = 5;
                    ulong b = ulong.MaxValue;
                    bool c = true;
                    return c && x ? (ulong)a : b;
                }
            }
            """, "C.Constants"));
    }

    [Fact]
    public void The_conditional_operator_passes_values_on_the_stack_through_stack_slots()
    {
        // a is on the stack when the ?: branches; each way stores its value, the join loads both
        Assert.Equal("""
            function i32 C.Ternary(i32 %a, bool %c)
              $stack0_i32: i32
              $stack1_i32: i32
            IL_0000:
              store $stack0_i32, %a
              branch %c, IL_0008, IL_0004
            IL_0004:
              %0 = const i32 100
              store $stack1_i32, %0
              jump IL_0009
            IL_0008:
              %1 = const i32 1
              store $stack1_i32, %1
              jump IL_0009
            IL_0009:
              %2 = load i32 $stack0_i32
              %3 = load i32 $stack1_i32
              %4 = add i32 %2, %3
              return %4

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static int Ternary(int a, bool c) => a + (c ? 1 : 100);
            }
            """, "C.Ternary"));
    }

    [Fact]
    public void And_and_or_pass_their_result_through_a_stack_slot()
    {
        var module = Lowered.Module("""
            using System;

            class Program
            {
                static bool Check(bool answer) => answer;

                static void Main()
                {
                    Console.WriteLine(Check(false) && Check(true));
                    Console.WriteLine(Check(true) || Check(false));
                }
            }
            """);

        Assert.Equal("""
            function void Program.Main()
              $stack0_i32: i32
            IL_0000:
              %0 = const bool false
              %1 = call bool Program.Check(%0)
              branch %1, IL_0008, IL_0010
            IL_0008:
              %2 = const bool true
              %3 = call bool Program.Check(%2)
              %4 = convert bool %3 to i32
              store $stack0_i32, %4
              jump IL_0011
            IL_0010:
              %5 = const i32 0
              store $stack0_i32, %5
              jump IL_0011
            IL_0011:
              %6 = load i32 $stack0_i32
              call void ChipSharp.Runtime.WriteLineBool(%6)
              %7 = const bool true
              %8 = call bool Program.Check(%7)
              branch %8, IL_0026, IL_001e
            IL_001e:
              %9 = const bool false
              %10 = call bool Program.Check(%9)
              %11 = convert bool %10 to i32
              store $stack0_i32, %11
              jump IL_0027
            IL_0026:
              %12 = const i32 1
              store $stack0_i32, %12
              jump IL_0027
            IL_0027:
              %13 = load i32 $stack0_i32
              call void ChipSharp.Runtime.WriteLineBool(%13)
              return

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module.Functions[0]));
    }

    [Fact]
    public void A_for_loop_jumps_to_its_test_and_the_test_back_to_the_body()
    {
        Assert.Equal("""
            function i32 C.Sum(i32 %n)
              $V_0: i32
              $V_1: i32
            IL_0000:
              %0 = const i32 0
              store $V_0, %0
              %1 = const i32 1
              store $V_1, %1
              jump IL_000e
            IL_0006:
              %2 = load i32 $V_0
              %3 = load i32 $V_1
              %4 = add i32 %2, %3
              store $V_0, %4
              %5 = load i32 $V_1
              %6 = const i32 1
              %7 = add i32 %5, %6
              store $V_1, %7
              jump IL_000e
            IL_000e:
              %8 = load i32 $V_1
              %9 = le i32 %8, %n
              branch %9, IL_0006, IL_0012
            IL_0012:
              %10 = load i32 $V_0
              return %10

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static int Sum(int n)
                {
                    int s = 0;
                    for (int i = 1; i <= n; i++)
                    {
                        s += i;
                    }

                    return s;
                }
            }
            """, "C.Sum"));
    }

    [Fact]
    public void A_switch_becomes_a_chain_of_tests_that_falls_through_to_the_default()
    {
        Assert.Equal("""
            function i32 C.Switch(i32 %a)
              $switch_i32: i32
            IL_0000:
              %0 = const i32 1
              %1 = sub i32 %a, %0
              store $switch_i32, %1
              %2 = const i32 0
              %3 = eq i32 %1, %2
              branch %3, IL_001a, IL_0003_case1
            IL_0003_case1:
              %4 = load i32 $switch_i32
              %5 = const i32 1
              %6 = eq i32 %4, %5
              branch %6, IL_001d, IL_0003_case2
            IL_0003_case2:
              %7 = load i32 $switch_i32
              %8 = const i32 2
              %9 = eq i32 %7, %8
              branch %9, IL_0020, IL_0003_case3
            IL_0003_case3:
              %10 = load i32 $switch_i32
              %11 = const i32 3
              %12 = eq i32 %10, %11
              branch %12, IL_0023, IL_0018
            IL_0018:
              jump IL_0026
            IL_001a:
              %13 = const i32 10
              return %13
            IL_001d:
              %14 = const i32 20
              return %14
            IL_0020:
              %15 = const i32 30
              return %15
            IL_0023:
              %16 = const i32 40
              return %16
            IL_0026:
              %17 = const i32 -1
              return %17

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static int Switch(int a)
                {
                    switch (a)
                    {
                        case 1: return 10;
                        case 2: return 20;
                        case 3: return 30;
                        case 4: return 40;
                        default: return -1;
                    }
                }
            }
            """, "C.Switch"));
    }

    [Fact]
    public void A_recursive_method_calls_its_own_function()
    {
        Assert.Equal("""
            function i32 C.Factorial(i32 %n)
            IL_0000:
              %0 = const i32 1
              %1 = le i32 %n, %0
              branch %1, IL_000f, IL_0004
            IL_0004:
              %2 = const i32 1
              %3 = sub i32 %n, %2
              %4 = call i32 C.Factorial(%3)
              %5 = mul i32 %n, %4
              return %5
            IL_000f:
              %6 = const i32 1
              return %6

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);
            }
            """, "C.Factorial"));
    }

    [Fact]
    public void A_parameter_the_method_assigns_to_is_copied_into_a_slot_first()
    {
        // the copies go at the start of IL_0000, which nothing jumps back to; Roslyn keeps rest on the stack
        // (ldarg.0 ldarg.1 rem ldarg.1 starg.s a starg.s b), so b = rest stores the rem computed first
        Assert.Equal("""
            function i32 C.Gcd(i32 %a, i32 %b)
              $a: i32
              $b: i32
            IL_0000:
              store $a, %a
              store $b, %b
              jump IL_000a
            IL_0002:
              %0 = load i32 $a
              %1 = load i32 $b
              %2 = call i32 ChipSharp.Division.Rem(%0, %1)
              %3 = load i32 $b
              store $a, %3
              store $b, %2
              jump IL_000a
            IL_000a:
              %4 = load i32 $b
              %5 = const i32 0
              %6 = ne i32 %4, %5
              branch %6, IL_0002, IL_000d
            IL_000d:
              %7 = load i32 $a
              return %7

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static int Gcd(int a, int b)
                {
                    while (b != 0)
                    {
                        int rest = a % b;
                        a = b;
                        b = rest;
                    }

                    return a;
                }
            }
            """, "C.Gcd"));
    }

    [Fact]
    public void A_loop_at_the_very_start_gets_an_entry_block_for_the_parameter_copies()
    {
        Assert.Equal("""
            function i32 C.CountDown(i32 %n)
              $n: i32
            entry:
              store $n, %n
              jump IL_0000
            IL_0000:
              %0 = load i32 $n
              %1 = const i32 1
              %2 = sub i32 %0, %1
              store $n, %2
              %3 = load i32 $n
              %4 = const i32 3
              %5 = gt i32 %3, %4
              branch %5, IL_0000, IL_0009
            IL_0009:
              %6 = load i32 $n
              return %6

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static int CountDown(int n)
                {
                    do
                    {
                        n--;
                    }
                    while (n > 3);

                    return n;
                }
            }
            """, "C.CountDown"));
    }

    [Fact]
    public void Top_level_statements_lose_their_unused_args_and_local_functions_are_static_methods()
    {
        var module = Lowered.Module("""
            int Square(int n) => n * n;
            System.Console.WriteLine(Square(7));
            """);

        Assert.Equal("""
            module Test
            entrypoint Program.<Main>$

            function void Program.<Main>$()
            IL_0000:
              %0 = const i32 7
              %1 = call i32 Program.<<Main>$>g__Square|0_0(%0)
              call void ChipSharp.Runtime.WriteLineInt32(%1)
              return

            function i32 Program.<<Main>$>g__Square|0_0(i32 %n)
            IL_0000:
              %0 = mul i32 %n, %n
              return %0

            external function void ChipSharp.Runtime.WriteLineInt32(i32 %value)

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module));
    }

    [Fact]
    public void Overloads_share_their_display_name_and_each_call_finds_its_own()
    {
        var module = Lowered.Module("""
            static class C
            {
                static int Twice(int a) => a * 2;
                static long Twice(long a) => a * 2;
                static long Run() => Twice(3) + Twice(4L);
            }
            """, "C.Run");

        Assert.Equal("""
            module Test
            entrypoint C.Run

            function i64 C.Run()
            IL_0000:
              %0 = const i32 3
              %1 = call i32 C.Twice(%0)
              %2 = convert i32 %1 to i64
              %3 = const i64 4
              %4 = call i64 C.Twice(%3)
              %5 = add i64 %2, %4
              return %5

            function i32 C.Twice(i32 %a)
            IL_0000:
              %0 = const i32 2
              %1 = mul i32 %a, %0
              return %1

            function i64 C.Twice(i64 %a)
            IL_0000:
              %0 = const i64 2
              %1 = mul i64 %a, %0
              return %1

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module));
    }

    [Fact]
    public void An_int_result_of_Main_is_kept()
    {
        var module = Lowered.Module("""
            class Program
            {
                static int Main() => 3;
            }
            """);

        Assert.Equal(TirType.I32, module.EntryPoint!.ReturnType);
        Assert.Empty(module.EntryPoint.Parameters);
    }

    [Fact]
    public void Lowering_does_not_change_the_program_it_reads()
    {
        var program = Lowered.Program(Lowered.Compile("""
            class Program
            {
                static int Twice(int a) => a * 2;
                static void Main() => System.Console.WriteLine(Twice(21));
            }
            """));

        var first = TirPrinter.Print(IlToTir.Lower(program));
        var second = TirPrinter.Print(IlToTir.Lower(program));

        Assert.Equal(first, second);
    }
}
