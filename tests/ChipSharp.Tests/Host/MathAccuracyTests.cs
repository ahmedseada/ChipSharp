using System.Globalization;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Host;

/// <summary>
/// The functions of System.Math and System.MathF as ChipSharp's runtime computes them (<see cref="MathLibrary"/>), next
/// to .NET's own on this PC, for tens of thousands of values each: the exact ones (Floor, Round, ScaleB,
/// FusedMultiplyAdd, ...) give .NET's bits every time; the transcendental ones (Sin, Exp, Pow, ...) give .NET's result
/// for at least the share each states, and are never further from it than its ulps (units in the last place) allow.
/// Every value is computed twice, with the PC's float hardware and in software as the Pico computes it: the two always
/// give the same bits, NaN too.
/// </summary>
/// <remarks>
/// .NET on Windows calls the C runtime (UCRT) for the transcendental functions, which on a processor with FMA3 (this
/// PC's) takes code paths that use the fused multiply-add; the shares are measured there. A processor without FMA3
/// takes other paths, whose results differ from these in a few cases more.
/// </remarks>
public sealed class MathAccuracyTests(ITestOutputHelper output)
{
    private const int ValuesPerRange = 20_000;

    /// <summary>
    /// A transcendental function: its name in Math or MathF, .NET's, the ranges its values come from, the share of
    /// them that must give .NET's bits (in percent, a little below what was measured) and the most ulps any other may
    /// be from .NET's.
    /// </summary>
    private sealed record Transcendental(string Name, bool Float, Func<double, double, double> Dotnet,
        Func<Random, (double, double)>[] Ranges, double Share, int Ulps);

    private static double U(Random r, double a, double b) => a + (b - a) * r.NextDouble();

    private static double LogU(Random r, double a, double b) => Math.Exp(U(r, Math.Log(a), Math.Log(b)));

    private static double S(Random r, double x) => r.Next(2) == 0 ? x : -x;

    private static double Bits(Random r, bool isFloat)
    {
        while (true)
        {
            var x = isFloat
                ? BitConverter.Int32BitsToSingle(r.Next() ^ (r.Next(2) << 31))
                : BitConverter.Int64BitsToDouble(r.NextInt64() ^ ((long)r.Next(2) << 63));
            if (double.IsFinite(x))
            {
                return x;
            }
        }
    }

    private static Func<Random, (double, double)> One(Func<Random, double> value) => r => (value(r), 0);

    private static Transcendental[] Functions(bool f)
    {
        var big = f ? 1e38 : 1e300;
        var tiny = f ? 1e-38 : 1e-300;
        var near1 = f ? 1e-8 : 1e-17;
        Func<Random, (double, double)>[] trig =
            [One(r => U(r, -7, 7)), One(r => S(r, LogU(r, 1e-10, 1e6))), One(r => S(r, LogU(r, 1e6, big))),
                One(r => Bits(r, f))];
        Func<Random, (double, double)>[] inverse =
            [One(r => U(r, -1, 1)), One(r => S(r, 1 - LogU(r, near1, 1))), One(r => S(r, LogU(r, tiny, 1)))];
        Func<Random, (double, double)>[] logs =
            [One(r => U(r, 0.5, 2)), One(r => LogU(r, f ? 1e-38 : 1e-308, f ? 3e38 : 1e308)), One(r => Math.Abs(Bits(r, f)))];
        Func<Random, (double, double)>[] hyperbolic =
            [One(r => U(r, -3, 3)), One(r => U(r, f ? -90 : -711, f ? 90 : 711)), One(r => S(r, LogU(r, tiny, 1)))];
        string Name(string name) => f ? name + "f" : name;
        return
        [
            new("Sin", f, (x, _) => f ? MathF.Sin((float)x) : Math.Sin(x), trig, f ? 99.8 : 98.5, 1),
            new("Cos", f, (x, _) => f ? MathF.Cos((float)x) : Math.Cos(x), trig, f ? 99.8 : 98.5, 1),
            new("Tan", f, (x, _) => f ? MathF.Tan((float)x) : Math.Tan(x), trig, f ? 99.9 : 96.8, 1),
            new("Asin", f, (x, _) => f ? MathF.Asin((float)x) : Math.Asin(x), inverse, 99.3, 1),
            new("Acos", f, (x, _) => f ? MathF.Acos((float)x) : Math.Acos(x), inverse, 99.5, 1),
            new("Atan", f, (x, _) => f ? MathF.Atan((float)x) : Math.Atan(x),
                [One(r => U(r, -10, 10)), One(r => S(r, LogU(r, tiny, big))), One(r => Bits(r, f))], 99.9, 1),
            new("Sinh", f, (x, _) => f ? MathF.Sinh((float)x) : Math.Sinh(x), hyperbolic, f ? 99.9 : 99.0, 1),
            new("Cosh", f, (x, _) => f ? MathF.Cosh((float)x) : Math.Cosh(x), hyperbolic, f ? 99.9 : 97.5, f ? 1 : 2),
            new("Tanh", f, (x, _) => f ? MathF.Tanh((float)x) : Math.Tanh(x),
                [One(r => U(r, -3, 3)), One(r => U(r, f ? -10 : -25, f ? 10 : 25)), One(r => S(r, LogU(r, tiny, 1)))],
                99.5, 1),
            new("Asinh", f, (x, _) => f ? MathF.Asinh((float)x) : Math.Asinh(x),
                [One(r => U(r, -3, 3)), One(r => S(r, LogU(r, tiny, big))), One(r => Bits(r, f))], f ? 92.5 : 93.8, 1),
            new("Acosh", f, (x, _) => f ? MathF.Acosh((float)x) : Math.Acosh(x),
                [One(r => 1 + LogU(r, f ? 1e-7 : 1e-17, 3)), One(r => LogU(r, 1, big))], f ? 87 : 88, 1),
            new("Atanh", f, (x, _) => f ? MathF.Atanh((float)x) : Math.Atanh(x), inverse, f ? 91.5 : 93.3, 1),
            new("Exp", f, (x, _) => f ? MathF.Exp((float)x) : Math.Exp(x),
                [One(r => U(r, -1, 1)), One(r => f ? U(r, -104, 89) : U(r, -746, 710)), One(r => S(r, LogU(r, tiny, 1)))],
                f ? 99.9 : 99.5, 1),
            new("Log", f, (x, _) => f ? MathF.Log((float)x) : Math.Log(x), logs, f ? 99.3 : 99.8, 1),
            new("Log2", f, (x, _) => f ? MathF.Log2((float)x) : Math.Log2(x), logs, f ? 99.9 : 99.5, 1),
            new("Log10", f, (x, _) => f ? MathF.Log10((float)x) : Math.Log10(x), logs, f ? 96.5 : 99.1, 1),
            new("Cbrt", f, (x, _) => f ? MathF.Cbrt((float)x) : Math.Cbrt(x), [One(r => U(r, -10, 10)), One(r => Bits(r, f))],
                69, 2),
            new("Atan2", f, (y, x) => f ? MathF.Atan2((float)y, (float)x) : Math.Atan2(y, x),
                [r => (U(r, -10, 10), U(r, -10, 10)), r => (Bits(r, f), Bits(r, f))], 99.9, 1),
            new("Pow", f, (x, y) => f ? MathF.Pow((float)x, (float)y) : Math.Pow(x, y),
            [
                r => (LogU(r, 1e-3, 1e3), U(r, f ? -12 : -50, f ? 12 : 50)),
                r => (U(r, 0.9, 1.1), U(r, f ? -500 : -1000, f ? 500 : 1000)),
                r => (S(r, LogU(r, 1e-3, 1e3)), r.Next(f ? -12 : -60, f ? 12 : 60)),
                r => (Math.Abs(Bits(r, f)), U(r, -2, 2)),
            ], f ? 99.6 : 99.8, 1),
        ];
    }

    public static TheoryData<bool> Widths => new(false, true);

    /// <summary>
    /// Each transcendental function gives .NET's bits for at least its share of the values, and is otherwise at most
    /// its ulps from it (1 but for Cbrt and Math.Cosh: 2); in hardware and in software alike.
    /// </summary>
    [HostTheory]
    [MemberData(nameof(Widths))]
    public void The_transcendental_functions_give_dotnets_result_but_for_a_few_values_and_then_differ_by_one_ulp(bool f)
    {
        List<string> failures = [];
        foreach (var function in Functions(f))
        {
            var op = MathLibrary.Op(function.Name, f);
            var random = new Random(function.Name.GetHashCode(StringComparison.Ordinal) & 0xffff);
            long total = 0, same = 0, worstUlps = 0;
            var worst = "";
            foreach (var range in function.Ranges)
            {
                for (var i = 0; i < ValuesPerRange; i++)
                {
                    var (x, y) = range(random);
                    if (f)
                    {
                        (x, y) = ((float)x, (float)y);
                    }

                    var dotnet = function.Dotnet(x, y);
                    var (hardware, software) = (Call(op, f, x, y), Call(op, f, x, y, software: true));
                    Assert.True(Bits(hardware, f) == Bits(software, f),
                        $"{function.Name}({x:R}, {y:R}): {hardware:R} on the PC, {software:R} in software");
                    total++;
                    if (Bits(hardware, f) == Bits(dotnet, f) || (double.IsNaN(hardware) && double.IsNaN(dotnet)))
                    {
                        same++;
                        continue;
                    }

                    var ulps = Ulps(hardware, dotnet, f);
                    if (ulps > worstUlps)
                    {
                        (worstUlps, worst) = (ulps, $"{x:R}, {y:R}: .NET {dotnet:R}, ChipSharp {hardware:R}");
                    }
                }
            }

            var share = 100.0 * same / total;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(f ? "MathF" : "Math")}.{function.Name,-6} {share,8:F3} % of {total} values as .NET, the others {worstUlps} ulp away"));
            if (share < function.Share || worstUlps > function.Ulps)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{function.Name}: {share:F3} % (expected {function.Share} %), {worstUlps} ulp (at most {function.Ulps}) at {worst}"));
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>The exact functions give .NET's bits for every special value, every pair of them, and random values.</summary>
    [HostTheory]
    [MemberData(nameof(Widths))]
    public void The_exact_functions_give_dotnets_bits_for_every_value(bool f)
    {
        var random = new Random(f ? 31 : 32);
        double[] values =
        [
            .. f ? FloatMathProgram.SpecialFloats.Select(v => (double)v) : FloatMathProgram.SpecialDoubles,
            .. Enumerable.Range(0, 20_000).Select(i => (i % 4) switch
            {
                0 => Bits(random, f),
                1 => S(random, U(random, 0, 8)) + random.Next(-3, 3) * 0.5,
                2 => S(random, LogU(random, f ? 1e-38 : 1e-308, f ? 1e38 : 1e308)),
                _ => random.Next(-1000, 1000) / 4.0,
            }).Select(v => f ? (float)v : v),
        ];
        (string Name, Func<double, double, double, double> Dotnet, int Arity)[] functions = f
            ?
            [
                ("Floor", (x, _, _) => MathF.Floor((float)x), 1), ("Ceiling", (x, _, _) => MathF.Ceiling((float)x), 1),
                ("Truncate", (x, _, _) => MathF.Truncate((float)x), 1), ("Round", (x, _, _) => MathF.Round((float)x), 1),
                ("BitIncrement", (x, _, _) => MathF.BitIncrement((float)x), 1),
                ("BitDecrement", (x, _, _) => MathF.BitDecrement((float)x), 1),
                // .NET on x64 estimates these with rcpss and rsqrtss, whose results differ between Intel's processors
                // and AMD's; ChipSharp computes what .NET computes without them
                ("ReciprocalEstimate", (x, _, _) => 1f / (float)x, 1),
                ("ReciprocalSqrtEstimate", (x, _, _) => 1f / MathF.Sqrt((float)x), 1),
                ("Sqrt", (x, _, _) => MathF.Sqrt((float)x), 1),
                ("CopySign", (x, y, _) => MathF.CopySign((float)x, (float)y), 2),
                ("IEEERemainder", (x, y, _) => MathF.IEEERemainder((float)x, (float)y), 2),
                ("MinMagnitude", (x, y, _) => MathF.MinMagnitude((float)x, (float)y), 2),
                ("MaxMagnitude", (x, y, _) => MathF.MaxMagnitude((float)x, (float)y), 2),
                ("ScaleB", (x, y, _) => MathF.ScaleB((float)x, (int)y), 2),
                ("FusedMultiplyAdd", (x, y, z) => MathF.FusedMultiplyAdd((float)x, (float)y, (float)z), 3),
                ("ILogB", (x, _, _) => MathF.ILogB((float)x), 1),
            ]
            :
            [
                ("Floor", (x, _, _) => Math.Floor(x), 1), ("Ceiling", (x, _, _) => Math.Ceiling(x), 1),
                ("Truncate", (x, _, _) => Math.Truncate(x), 1), ("Round", (x, _, _) => Math.Round(x), 1),
                ("BitIncrement", (x, _, _) => Math.BitIncrement(x), 1),
                ("BitDecrement", (x, _, _) => Math.BitDecrement(x), 1),
                ("ReciprocalEstimate", (x, _, _) => Math.ReciprocalEstimate(x), 1),
                ("ReciprocalSqrtEstimate", (x, _, _) => Math.ReciprocalSqrtEstimate(x), 1),
                ("Sqrt", (x, _, _) => Math.Sqrt(x), 1),
                ("CopySign", (x, y, _) => Math.CopySign(x, y), 2),
                ("IEEERemainder", (x, y, _) => Math.IEEERemainder(x, y), 2),
                ("MinMagnitude", (x, y, _) => Math.MinMagnitude(x, y), 2),
                ("MaxMagnitude", (x, y, _) => Math.MaxMagnitude(x, y), 2),
                ("ScaleB", (x, y, _) => Math.ScaleB(x, (int)y), 2),
                ("FusedMultiplyAdd", Math.FusedMultiplyAdd, 3),
                ("ILogB", (x, _, _) => Math.ILogB(x), 1),
            ];

        List<string> failures = [];
        foreach (var (name, dotnet, arity) in functions)
        {
            var op = MathLibrary.Op(name, f);
            for (var i = 0; i < values.Length; i++)
            {
                var x = values[i];
                var y = name == "ScaleB" ? random.Next(-2200, 2200) : values[random.Next(values.Length)];
                var z = values[random.Next(values.Length)];
                var expected = dotnet(x, y, z);
                ulong a = Bits(x, f), b = name == "ScaleB" ? (ulong)(uint)(int)y : Bits(y, f), c = Bits(z, f);
                var hardware = MathLibrary.Hardware(op, a, b, c);
                var software = MathLibrary.Software(op, a, b, c);
                var bits = name == "ILogB" ? (ulong)(uint)(int)expected : Bits(expected, f);
                if (hardware != bits || software != bits)
                {
                    failures.Add($"{name}({x:R}, {y:R}, {z:R}): .NET {bits:X}, ChipSharp {hardware:X}, in software {software:X}");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} differ:\n{string.Join('\n', failures.Take(20))}");
    }

    private static double Call(int op, bool f, double x, double y, bool software = false)
    {
        var (a, b) = (Bits(x, f), Bits(y, f));
        var result = software ? MathLibrary.Software(op, a, b) : MathLibrary.Hardware(op, a, b);
        return f ? BitConverter.UInt32BitsToSingle((uint)result) : BitConverter.UInt64BitsToDouble(result);
    }

    private static ulong Bits(double value, bool f) =>
        f ? BitConverter.SingleToUInt32Bits((float)value) : BitConverter.DoubleToUInt64Bits(value);

    /// <summary>How many representable values apart two results are (a NaN and a number: as far as can be).</summary>
    private static long Ulps(double a, double b, bool f)
    {
        if (double.IsNaN(a) || double.IsNaN(b))
        {
            return long.MaxValue;
        }

        // the bits in the order of the values: negative ones below zero, -0 next to +0
        static long Order(long bits) => bits < 0 ? long.MinValue - bits : bits;
        static long OrderF(int bits) => bits < 0 ? (long)int.MinValue - bits : bits;
        return f
            ? Math.Abs(OrderF(BitConverter.SingleToInt32Bits((float)a)) - OrderF(BitConverter.SingleToInt32Bits((float)b)))
            : Math.Abs(Order(BitConverter.DoubleToInt64Bits(a)) - Order(BitConverter.DoubleToInt64Bits(b)));
    }
}
