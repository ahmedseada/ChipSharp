using System.Reflection.Emit;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// The TIR that float and double C# becomes: float arithmetic stays in float, conversions are explicit and
/// saturate, the .un comparisons and branches are the unordered ones, and Math becomes runtime calls.
/// </summary>
public sealed class FloatLoweringTests
{
    [Fact]
    public void Float_arithmetic_is_done_in_float()
    {
        Assert.Equal("""
            function f32 C.Logit(f32 %w0, f32 %w1, f32 %f0, f32 %f1)
            IL_0000:
              %0 = mul f32 %w0, %f0
              %1 = mul f32 %w1, %f1
              %2 = add f32 %0, %1
              return %2

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static float Logit(float w0, float w1, float f0, float f1) => w0 * f0 + w1 * f1;
            }
            """, "C.Logit"));
    }

    [Fact]
    public void A_float_with_a_double_is_widened_and_constants_have_their_type()
    {
        const string Source = """
            static class C
            {
                static float Tenth(float x) => x + 0.1f;
                static double Mixed(float f, double d) => f * d + 0.5f;
            }
            """;

        Assert.Equal("""
            function f32 C.Tenth(f32 %x)
            IL_0000:
              %0 = const f32 0.1
              %1 = add f32 %x, %0
              return %1

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.Tenth"));
        Assert.Equal("""
            function f64 C.Mixed(f32 %f, f64 %d)
            IL_0000:
              %0 = convert f32 %f to f64
              %1 = mul f64 %0, %d
              %2 = const f64 0.5
              %3 = add f64 %1, %2
              return %3

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.Mixed"));
    }

    [Fact]
    public void A_float_becomes_an_integer_rounded_toward_zero_and_a_small_one_through_int32()
    {
        Assert.Equal("""
            function i32 C.Conversions(f64 %d, f32 %f)
            IL_0000:
              %0 = convert f64 %d to i32
              %1 = convert f32 %f to i32
              %2 = convert i32 %1 to i8
              %3 = uconvert i8 %2 to i32
              %4 = add i32 %0, %3
              %5 = uconvert f32 %f to i32
              %6 = add i32 %4, %5
              return %6

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static int Conversions(double d, float f) => (int)d + (byte)f + (int)(uint)f;
            }
            """, "C.Conversions"));
    }

    [Fact]
    public void An_unsigned_integer_becomes_a_float_rounded_once_as_RyuJIT_does_it()
    {
        // conv.r.un then conv.r4 is one conversion; with conv.r8 between, the ulong is a double first
        Assert.Equal("""
            function f32 C.FromUnsigned(i64 %u, i32 %i)
            IL_0000:
              %0 = uconvert i64 %u to f32
              %1 = uconvert i32 %i to f64
              %2 = convert f64 %1 to f32
              %3 = add f32 %0, %2
              %4 = uconvert i64 %u to f64
              %5 = convert f64 %4 to f32
              %6 = add f32 %3, %5
              return %6

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static float FromUnsigned(ulong u, uint i) => u + (float)(double)i + (float)(double)u;
            }
            """, "C.FromUnsigned"));
    }

    [Fact]
    public void Negation_flips_the_sign_of_zero_too_and_rem_is_the_float_remainder()
    {
        Assert.Equal("""
            function f64 C.Negate(f64 %x)
            IL_0000:
              %0 = const f64 -0
              %1 = sub f64 %0, %x
              %2 = const f64 2
              %3 = rem f64 %1, %2
              return %3

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static double Negate(double x) => -x % 2.0;
            }
            """, "C.Negate"));
    }

    [Fact]
    public void The_un_branches_on_floats_are_the_unordered_comparisons()
    {
        // if (a >= b) is blt.un over the then part: taken when a < b or either is NaN
        Assert.Equal("""
            function i32 C.Branches(f32 %a, f32 %b)
            IL_0000:
              %0 = ult f32 %a, %b
              branch %0, IL_0006, IL_0004
            IL_0004:
              %1 = const i32 1
              return %1
            IL_0006:
              %2 = ne f32 %a, %b
              branch %2, IL_000c, IL_000a
            IL_000a:
              %3 = const i32 3
              return %3
            IL_000c:
              %4 = const i32 2
              return %4

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static int Branches(float a, float b)
                {
                    if (a >= b)
                    {
                        return 1;
                    }

                    return a != b ? 2 : 3;
                }
            }
            """, "C.Branches"));
    }

    [Fact]
    public void Math_and_MathF_become_runtime_functions()
    {
        var module = Lowered.Module("""
            using System;

            static class C
            {
                static double Root(double x) => Math.Sqrt(Math.Abs(x)) + MathF.Max(1f, (float)x) + Math.Min(x, 2.0);
            }
            """, "C.Root");

        Assert.Equal("""
            function f64 C.Root(f64 %x)
            IL_0000:
              %0 = call f64 ChipSharp.Runtime.AbsFloat64(%x)
              %1 = call f64 ChipSharp.Runtime.SqrtFloat64(%0)
              %2 = const f32 1
              %3 = convert f64 %x to f32
              %4 = call f32 ChipSharp.Runtime.MaxFloat32(%2, %3)
              %5 = convert f32 %4 to f64
              %6 = add f64 %1, %5
              %7 = const f64 2
              %8 = call f64 ChipSharp.Runtime.MinFloat64(%x, %7)
              %9 = add f64 %6, %8
              return %9

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module.Functions[0]));
        Assert.Equal(
            ["ChipSharp.Runtime.AbsFloat64", "ChipSharp.Runtime.SqrtFloat64", "ChipSharp.Runtime.MaxFloat32",
                "ChipSharp.Runtime.MinFloat64"],
            module.Functions.Where(f => f.Kind == TirFunctionKind.External).Select(f => f.Name));
    }

    [Fact]
    public void Float_locals_and_a_double_stored_in_a_float_local_or_returned_as_a_float_are_rounded()
    {
        var emitted = new EmittedAssembly();
        emitted.Method("F", typeof(float), [typeof(double), typeof(float)], il =>
        {
            il.DeclareLocal(typeof(float));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ret);
        });

        var function = Lowered.Lower(Lowered.Program(emitted.Read(), "Program.F")).Functions[0];

        Assert.Equal("""
            function f32 Program.F(f64 %a, f32 %b)
              $V_0: f32
            IL_0000:
              %0 = convert f64 %a to f32
              store $V_0, %0
              %1 = load f32 $V_0
              %2 = add f32 %1, %b
              %3 = convert f32 %2 to f64
              %4 = add f64 %3, %a
              %5 = convert f64 %4 to f32
              return %5

            """.ReplaceLineEndings("\n"), TirPrinter.Print(function));
    }

    [Theory]
    [InlineData("add", "IL_0002: add takes float64 and int32, which IL does not allow together.")]
    [InlineData("and", "IL_0002: and takes an integer, not float64.")]
    [InlineData("stloc", "IL_0001: local V_0 has type int32, but the value on the evaluation stack is float64.")]
    public void A_float_where_IL_wants_an_integer_is_an_error(string what, string message)
    {
        var emitted = new EmittedAssembly("Test");
        emitted.Method("F", typeof(int), [typeof(double)], il =>
        {
            il.DeclareLocal(typeof(int));
            il.Emit(OpCodes.Ldarg_0);
            switch (what)
            {
                case "add":
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Add);
                    break;
                case "and":
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.And);
                    break;
                default:
                    il.Emit(OpCodes.Stloc_0);
                    il.Emit(OpCodes.Ldloc_0);
                    break;
            }

            il.Emit(OpCodes.Ret);
        });

        var error = Assert.Throws<ChipSharpException>(() => Lowered.Lower(Lowered.Program(emitted.Read(), "Program.F")));

        Assert.Equal("Test.dll: Program.F(float64) at " + message, error.Message);
    }

    [Fact]
    public void Ckfinite_is_not_supported_yet()
    {
        var emitted = new EmittedAssembly("Test");
        emitted.Method("F", typeof(double), [typeof(double)], il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ckfinite);
            il.Emit(OpCodes.Ret);
        });

        var error = Assert.Throws<ChipSharpException>(() => Lowered.Lower(Lowered.Program(emitted.Read(), "Program.F")));

        Assert.Equal("Test.dll: Program.F(float64) at IL_0001: ckfinite is not supported yet.",
            error.Message);
    }
}
