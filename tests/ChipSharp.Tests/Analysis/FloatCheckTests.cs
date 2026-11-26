using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;
using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>What the pass says about floats and doubles: they are supported; ckfinite and the rest of Math are not yet.</summary>
public sealed class FloatCheckTests
{
    [Fact]
    public void Float_and_double_locals_parameters_results_constants_conversions_and_Math_are_supported()
    {
        var analysis = Check("""
            using System;

            class Program
            {
                static float Logit(float weight0, float weight1, float feature0, float feature1)
                {
                    return (weight0 * feature0) + (weight1 * feature1);
                }

                static double Hypotenuse(double a, double b) => Math.Sqrt(a * a + b * b);

                static void Main()
                {
                    float result = Logit(0.5f, -0.25f, 1.0f, 2.0f);
                    Console.WriteLine(result);
                    double sum = 0;
                    for (int i = 1; i <= 10; i++)
                    {
                        sum += 1.0 / i;
                    }

                    Console.WriteLine(sum);
                    Console.WriteLine((int)Hypotenuse(3, 4) + (long)(sum * 1e12) + (byte)result);
                    Console.WriteLine(Math.Max(Math.Abs(-1.5), MathF.Min(2f, 3f)) % 1 < 0.75 && !(sum >= 3));
                    ulong big = ulong.MaxValue;
                    Console.WriteLine((float)big + (double)(uint)big);
                }
            }
            """);

        Assert.True(analysis.IsSupported, analysis.Summary);
        Assert.Equal(["Program.Main", "Program.Logit", "Program.Hypotenuse"],
            analysis.Methods.Select(method => method.DisplayName));
    }

    [Fact]
    public void Float_and_double_map_to_f32_and_f64()
    {
        Assert.True(SupportedSubset.TryMapType(IlType.Float32, out var single));
        Assert.True(SupportedSubset.TryMapType(IlType.Float64, out var @double));
        Assert.Equal(TirType.F32, single);
        Assert.Equal(TirType.F64, @double);
        IReadOnlySet<IlOpCode> opCodes = SupportedSubset.OpCodes;
        Assert.Contains(IlOpCode.Conv_R_Un, opCodes);
        Assert.DoesNotContain(IlOpCode.Ckfinite, opCodes);
    }

    [Fact]
    public void The_Math_methods_beyond_Abs_Min_Max_and_Sqrt_are_library_methods_not_available_yet()
    {
        var problems = Problems("""
            using System;

            class Program
            {
                static void Main()
                {
                    double x = Math.Sqrt(2);
                    Console.WriteLine(Math.Sin(x));
                    Console.WriteLine(Math.Floor(x));
                    Console.WriteLine(double.IsNaN(x));
                    Console.WriteLine(Math.Abs(-3));
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main() at IL_000f (call): the library method System.Math.Sin(double) is not available yet.",
            "Program.Main() at IL_001a (call): the library method System.Math.Floor(double) is not available yet.",
            "Program.Main() at IL_0024 (call): the library method double.IsNaN(double) is not available yet.",
            "Program.Main() at IL_0030 (call): the library method System.Math.Abs(int) is not available yet.",
        ], problems);
    }

    [Fact]
    public void Ckfinite_has_words_of_its_own()
    {
        var ckfinite = Unsupported.ForInstruction(IlOpCode.Ckfinite);

        Assert.Equal("checking that a float is finite (ckfinite) arrives later. For now, compare it: " +
            "x >= -double.MaxValue && x <= double.MaxValue is false for NaN and the infinities.", ckfinite.Message);
    }
}
