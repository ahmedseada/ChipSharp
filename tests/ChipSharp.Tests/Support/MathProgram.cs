using System.Globalization;
using System.Text;

namespace ChipSharp.Tests.Support;

/// <summary>
/// The differential tests of System.Math and System.MathF: one C# program that prints, line by line, what every member
/// gives for many values, each line its function's name and its result ("D.Sin 0.8414709848078965", "F.Round 2"): the
/// members of doubles (D.), of floats (F.), of whole numbers (I.), double.Sin and generic math (G.), Round to digits by
/// each MidpointRounding (R.). .NET runs it for the expected output; ChipSharp builds it for the PC and the Pico.
/// </summary>
/// <remarks>
/// Every value goes through Math.Max(x, x) first (see <see cref="FloatMathProgram"/>), so that the functions run when
/// the program does, not while LLVM compiles it. MathF's ReciprocalEstimate and ReciprocalSqrtEstimate are not here:
/// .NET on x64 computes them with rcpss and rsqrtss, ChipSharp as 1 / x (see MathAccuracyTests).
/// </remarks>
internal sealed class MathProgram
{
    /// <summary>The functions whose results may differ from .NET's in the last bits, with the ulps they may.</summary>
    public static IReadOnlyDictionary<string, int> Transcendental { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Sin"] = 1, ["Cos"] = 1, ["Tan"] = 1, ["Asin"] = 1, ["Acos"] = 1, ["Atan"] = 1, ["Atan2"] = 1, ["Sinh"] = 1,
        ["Cosh"] = 2, ["Tanh"] = 1, ["Asinh"] = 1, ["Acosh"] = 1, ["Atanh"] = 1, ["Exp"] = 1, ["Log"] = 1, ["Log2"] = 1,
        ["Log10"] = 1, ["LogBase"] = 2, ["Pow"] = 1, ["Cbrt"] = 2, ["SinCos"] = 1,
    };

    private const string Helpers = """
        using System;
        using System.Numerics;

        class Program
        {
            static double D(double x) => Math.Max(x, x);
            static float F(float x) => MathF.Max(x, x);

            static T Root<T>(T x) where T : IRootFunctions<T> => T.Sqrt(x) + T.Cbrt(x);
            static T Wave<T>(T x) where T : ITrigonometricFunctions<T>, IExponentialFunctions<T> => T.Sin(x) * T.Exp(x);
            static T Nearest<T>(T x) where T : IFloatingPoint<T> => T.Round(x, 2, MidpointRounding.AwayFromZero);

            static void Double(double a, double b)
            {
                a = D(a);
                b = D(b);
                Console.WriteLine("D.Sin " + Math.Sin(a));
                Console.WriteLine("D.Cos " + Math.Cos(a));
                Console.WriteLine("D.Tan " + Math.Tan(a));
                Console.WriteLine("D.Asin " + Math.Asin(a));
                Console.WriteLine("D.Acos " + Math.Acos(a));
                Console.WriteLine("D.Atan " + Math.Atan(a));
                Console.WriteLine("D.Atan2 " + Math.Atan2(a, b));
                Console.WriteLine("D.Sinh " + Math.Sinh(a));
                Console.WriteLine("D.Cosh " + Math.Cosh(a));
                Console.WriteLine("D.Tanh " + Math.Tanh(a));
                Console.WriteLine("D.Asinh " + Math.Asinh(a));
                Console.WriteLine("D.Acosh " + Math.Acosh(a));
                Console.WriteLine("D.Atanh " + Math.Atanh(a));
                Console.WriteLine("D.Exp " + Math.Exp(a));
                Console.WriteLine("D.Log " + Math.Log(a));
                Console.WriteLine("D.Log2 " + Math.Log2(a));
                Console.WriteLine("D.Log10 " + Math.Log10(a));
                Console.WriteLine("D.LogBase " + Math.Log(a, b));
                Console.WriteLine("D.Pow " + Math.Pow(a, b));
                Console.WriteLine("D.Cbrt " + Math.Cbrt(a));
                var (sin, cos) = Math.SinCos(a);
                Console.WriteLine("D.SinCos " + sin + " " + cos);
                Console.WriteLine("D.Sqrt " + Math.Sqrt(a));
                Console.WriteLine("D.Floor " + Math.Floor(a));
                Console.WriteLine("D.Ceiling " + Math.Ceiling(a));
                Console.WriteLine("D.Truncate " + Math.Truncate(a));
                Console.WriteLine("D.Round " + Math.Round(a));
                Console.WriteLine("D.Round " + Math.Round(a, MidpointRounding.AwayFromZero));
                Console.WriteLine("D.Round " + Math.Round(a, MidpointRounding.ToZero));
                Console.WriteLine("D.Round " + Math.Round(a, MidpointRounding.ToNegativeInfinity));
                Console.WriteLine("D.Round " + Math.Round(a, MidpointRounding.ToPositiveInfinity));
                Console.WriteLine("D.Round " + Math.Round(a, 2));
                Console.WriteLine("D.Round " + Math.Round(a, 3, MidpointRounding.AwayFromZero));
                Console.WriteLine("D.CopySign " + Math.CopySign(a, b));
                Console.WriteLine("D.BitIncrement " + Math.BitIncrement(a));
                Console.WriteLine("D.BitDecrement " + Math.BitDecrement(a));
                Console.WriteLine("D.MinMagnitude " + Math.MinMagnitude(a, b));
                Console.WriteLine("D.MaxMagnitude " + Math.MaxMagnitude(a, b));
                Console.WriteLine("D.IEEERemainder " + Math.IEEERemainder(a, b));
                Console.WriteLine("D.ScaleB " + Math.ScaleB(a, 7) + " " + Math.ScaleB(a, -1080) + " " + Math.ScaleB(a, 2000));
                Console.WriteLine("D.ILogB " + Math.ILogB(a));
                Console.WriteLine("D.FusedMultiplyAdd " + Math.FusedMultiplyAdd(a, b, a));
                Console.WriteLine("D.ReciprocalEstimate " + Math.ReciprocalEstimate(a));
                Console.WriteLine("D.ReciprocalSqrtEstimate " + Math.ReciprocalSqrtEstimate(a));
                Console.WriteLine("D.Clamp " + Math.Clamp(a, -1.5, 2.5));
                Console.WriteLine("D.Abs " + Math.Abs(a) + " " + Math.Min(a, b) + " " + Math.Max(a, b));
                if (a == a)
                {
                    Console.WriteLine("D.Sign " + Math.Sign(a));
                }

                Console.WriteLine("G.Sin " + double.Sin(a));
                Console.WriteLine("G.Wave " + (Wave(a) == Math.Sin(a) * Math.Exp(a)));
                Console.WriteLine("G.Root " + (Root(a) == Math.Sqrt(a) + Math.Cbrt(a)));
                Console.WriteLine("G.Nearest " + Nearest(a));
            }

            static void Float(float a, float b)
            {
                a = F(a);
                b = F(b);
                Console.WriteLine("F.Sin " + MathF.Sin(a));
                Console.WriteLine("F.Cos " + MathF.Cos(a));
                Console.WriteLine("F.Tan " + MathF.Tan(a));
                Console.WriteLine("F.Asin " + MathF.Asin(a));
                Console.WriteLine("F.Acos " + MathF.Acos(a));
                Console.WriteLine("F.Atan " + MathF.Atan(a));
                Console.WriteLine("F.Atan2 " + MathF.Atan2(a, b));
                Console.WriteLine("F.Sinh " + MathF.Sinh(a));
                Console.WriteLine("F.Cosh " + MathF.Cosh(a));
                Console.WriteLine("F.Tanh " + MathF.Tanh(a));
                Console.WriteLine("F.Asinh " + MathF.Asinh(a));
                Console.WriteLine("F.Acosh " + MathF.Acosh(a));
                Console.WriteLine("F.Atanh " + MathF.Atanh(a));
                Console.WriteLine("F.Exp " + MathF.Exp(a));
                Console.WriteLine("F.Log " + MathF.Log(a));
                Console.WriteLine("F.Log2 " + MathF.Log2(a));
                Console.WriteLine("F.Log10 " + MathF.Log10(a));
                Console.WriteLine("F.LogBase " + MathF.Log(a, b));
                Console.WriteLine("F.Pow " + MathF.Pow(a, b));
                Console.WriteLine("F.Cbrt " + MathF.Cbrt(a));
                var (sin, cos) = MathF.SinCos(a);
                Console.WriteLine("F.SinCos " + sin + " " + cos);
                Console.WriteLine("F.Sqrt " + MathF.Sqrt(a));
                Console.WriteLine("F.Floor " + MathF.Floor(a));
                Console.WriteLine("F.Ceiling " + MathF.Ceiling(a));
                Console.WriteLine("F.Truncate " + MathF.Truncate(a));
                Console.WriteLine("F.Round " + MathF.Round(a));
                Console.WriteLine("F.Round " + MathF.Round(a, MidpointRounding.AwayFromZero));
                Console.WriteLine("F.Round " + MathF.Round(a, MidpointRounding.ToZero));
                Console.WriteLine("F.Round " + MathF.Round(a, MidpointRounding.ToNegativeInfinity));
                Console.WriteLine("F.Round " + MathF.Round(a, MidpointRounding.ToPositiveInfinity));
                Console.WriteLine("F.Round " + MathF.Round(a, 2));
                Console.WriteLine("F.Round " + MathF.Round(a, 3, MidpointRounding.AwayFromZero));
                Console.WriteLine("F.CopySign " + MathF.CopySign(a, b));
                Console.WriteLine("F.BitIncrement " + MathF.BitIncrement(a));
                Console.WriteLine("F.BitDecrement " + MathF.BitDecrement(a));
                Console.WriteLine("F.MinMagnitude " + MathF.MinMagnitude(a, b));
                Console.WriteLine("F.MaxMagnitude " + MathF.MaxMagnitude(a, b));
                Console.WriteLine("F.IEEERemainder " + MathF.IEEERemainder(a, b));
                Console.WriteLine("F.ScaleB " + MathF.ScaleB(a, 7) + " " + MathF.ScaleB(a, -160) + " " + MathF.ScaleB(a, 300));
                Console.WriteLine("F.ILogB " + MathF.ILogB(a));
                Console.WriteLine("F.FusedMultiplyAdd " + MathF.FusedMultiplyAdd(a, b, a));
                Console.WriteLine("F.Clamp " + Math.Clamp(a, -1.5f, 2.5f));
                Console.WriteLine("F.Abs " + MathF.Abs(a) + " " + MathF.Min(a, b) + " " + MathF.Max(a, b));
                if (a == a)
                {
                    Console.WriteLine("F.Sign " + MathF.Sign(a));
                }

                Console.WriteLine("GF.Sin " + float.Sin(a));
                Console.WriteLine("G.Root " + (Root(a) == MathF.Sqrt(a) + MathF.Cbrt(a)));
                Console.WriteLine("G.Nearest " + Nearest(a));
            }

            static void Rounding(double value)
            {
                double d = D(value);
                float f = F((float)value);
                for (int digits = 0; digits <= 15; digits++)
                {
                    Console.WriteLine("R.Double " + digits + " " + Math.Round(d, digits) + " " +
                        Math.Round(d, digits, MidpointRounding.AwayFromZero) + " " + Math.Round(d, digits, MidpointRounding.ToZero) +
                        " " + Math.Round(d, digits, MidpointRounding.ToNegativeInfinity) + " " +
                        Math.Round(d, digits, MidpointRounding.ToPositiveInfinity));
                }

                for (int digits = 0; digits <= 6; digits++)
                {
                    Console.WriteLine("R.Float " + digits + " " + MathF.Round(f, digits) + " " +
                        MathF.Round(f, digits, MidpointRounding.AwayFromZero) + " " + MathF.Round(f, digits, MidpointRounding.ToZero) +
                        " " + MathF.Round(f, digits, MidpointRounding.ToNegativeInfinity) + " " +
                        MathF.Round(f, digits, MidpointRounding.ToPositiveInfinity));
                }
            }

            static void Integers(long value, long other)
            {
                long l = value ^ (long)Math.Max(0.0, 0.0);
                long o = other ^ (long)Math.Max(0.0, 0.0);
                int i = (int)l;
                int j = (int)o == 0 ? 7 : (int)o;
                Console.WriteLine("I.Abs " + Math.Abs(i == int.MinValue ? 0 : i) + " " + Math.Abs(l == long.MinValue ? 0 : l) + " " +
                    Math.Abs((short)((short)i == short.MinValue ? 0 : (short)i)) + " " + Math.Abs((sbyte)((sbyte)i == sbyte.MinValue ? 0 : (sbyte)i)) +
                    " " + (long)Math.Abs((nint)(i == int.MinValue ? 0 : i)));
                Console.WriteLine("I.Min " + Math.Min(i, j) + " " + Math.Min(l, o) + " " + Math.Min((uint)i, (uint)j) + " " +
                    Math.Min((ulong)l, (ulong)o) + " " + Math.Min((short)i, (short)j) + " " + Math.Min((ushort)i, (ushort)j) +
                    " " + Math.Min((byte)i, (byte)j) + " " + Math.Min((sbyte)i, (sbyte)j) + " " + (long)Math.Min((nint)i, (nint)j) +
                    " " + (ulong)Math.Min((nuint)(uint)i, (nuint)(uint)j));
                Console.WriteLine("I.Max " + Math.Max(i, j) + " " + Math.Max(l, o) + " " + Math.Max((uint)i, (uint)j) + " " +
                    Math.Max((ulong)l, (ulong)o) + " " + Math.Max((short)i, (short)j) + " " + Math.Max((ushort)i, (ushort)j) +
                    " " + Math.Max((byte)i, (byte)j) + " " + Math.Max((sbyte)i, (sbyte)j) + " " + (long)Math.Max((nint)i, (nint)j) +
                    " " + (ulong)Math.Max((nuint)(uint)i, (nuint)(uint)j));
                Console.WriteLine("I.Clamp " + Math.Clamp(i, -1000, 1000) + " " + Math.Clamp(l, -5L, 50000000000L) + " " +
                    Math.Clamp((uint)i, 3u, 4000000000u) + " " + Math.Clamp((ulong)l, 9ul, 1ul << 60) + " " +
                    Math.Clamp((short)i, (short)-300, (short)300) + " " + Math.Clamp((ushort)i, (ushort)10, (ushort)60000) + " " +
                    Math.Clamp((byte)i, (byte)20, (byte)200) + " " + Math.Clamp((sbyte)i, (sbyte)-100, (sbyte)100) + " " +
                    (long)Math.Clamp((nint)i, -77, 77) + " " + (ulong)Math.Clamp((nuint)(uint)i, 5u, 500u));
                Console.WriteLine("I.Sign " + Math.Sign(i) + " " + Math.Sign(l) + " " + Math.Sign((short)i) + " " +
                    Math.Sign((sbyte)i) + " " + Math.Sign((nint)i));
                var (q1, r1) = Math.DivRem(i == int.MinValue && j == -1 ? 0 : i, j);
                var (q2, r2) = Math.DivRem(l == long.MinValue && o == -1 ? 0 : l, o == 0 ? 3 : o);
                var (q3, r3) = Math.DivRem((uint)i, (uint)j);
                var (q4, r4) = Math.DivRem((ulong)l, (ulong)(o == 0 ? 3 : o));
                var (q5, r5) = Math.DivRem((short)i, (short)((short)j == 0 ? 5 : (short)j));
                var (q6, r6) = Math.DivRem((ushort)i, (ushort)((ushort)j == 0 ? 5 : (ushort)j));
                var (q7, r7) = Math.DivRem((byte)i, (byte)((byte)j == 0 ? 5 : (byte)j));
                var (q8, r8) = Math.DivRem((sbyte)i, (sbyte)((sbyte)j == 0 ? 5 : (sbyte)j));
                var (q9, r9) = Math.DivRem((nint)(i == int.MinValue && j == -1 ? 0 : i), (nint)j);
                var (q10, r10) = Math.DivRem((nuint)(uint)i, (nuint)(uint)j);
                Console.WriteLine("I.DivRem " + q1 + " " + r1 + " " + q2 + " " + r2 + " " + q3 + " " + r3 + " " + q4 + " " + r4 +
                    " " + q5 + " " + r5 + " " + q6 + " " + r6 + " " + q7 + " " + r7 + " " + q8 + " " + r8 + " " + (long)q9 + " " +
                    (long)r9 + " " + (ulong)q10 + " " + (ulong)r10);
                int quotient = Math.DivRem(i == int.MinValue && j == -1 ? 0 : i, j, out int remainder);
                long longQuotient = Math.DivRem(l == long.MinValue && o == -1 ? 0 : l, o == 0 ? 3 : o, out long longRemainder);
                Console.WriteLine("I.DivRemOut " + quotient + " " + remainder + " " + longQuotient + " " + longRemainder);
                ulong high = Math.BigMul((ulong)l, (ulong)o, out ulong low);
                long signedHigh = Math.BigMul(l, o, out long signedLow);
                Console.WriteLine("I.BigMul " + Math.BigMul(i, j) + " " + Math.BigMul((uint)i, (uint)j) + " " + high + " " + low +
                    " " + signedHigh + " " + signedLow);
            }
        """;

    private readonly StringBuilder _main = new();

    /// <summary>The C# program.</summary>
    public string Source() => $"{Helpers}\n    static void Main()\n    {{\n{_main}    }}\n}}\n";

    /// <summary>Every member of Math on doubles for each pair of values.</summary>
    public MathProgram Doubles(IEnumerable<(double A, double B)> pairs)
    {
        foreach (var (a, b) in pairs)
        {
            _main.Append($"        Double({FloatMathProgram.Literal(a)}, {FloatMathProgram.Literal(b)});\n");
        }

        return this;
    }

    /// <summary>Every member of MathF (and of Math on floats) for each pair of values.</summary>
    public MathProgram Floats(IEnumerable<(float A, float B)> pairs)
    {
        foreach (var (a, b) in pairs)
        {
            _main.Append($"        Float({FloatMathProgram.Literal(a)}, {FloatMathProgram.Literal(b)});\n");
        }

        return this;
    }

    /// <summary>Round of each value to every number of digits, by every MidpointRounding, in doubles and in floats.</summary>
    public MathProgram Rounding(IEnumerable<double> values)
    {
        foreach (var value in values)
        {
            _main.Append($"        Rounding({FloatMathProgram.Literal(value)});\n");
        }

        return this;
    }

    /// <summary>Abs, Min, Max, Clamp, Sign, DivRem and BigMul of every whole-number type for each pair.</summary>
    public MathProgram Integers(IEnumerable<(long A, long B)> pairs)
    {
        foreach (var (a, b) in pairs)
        {
            _main.Append(CultureInfo.InvariantCulture, $"        Integers({a}L, {b}L);\n");
        }

        return this;
    }

    /// <summary>Values that round halfway, near halfway, at the limits of Round and between.</summary>
    public static double[] RoundingValues { get; } =
    [
        0, -0.0, 0.5, -0.5, 1.5, 2.5, -2.5, 0.125, 0.375, 2.345, 2.355, -2.345, 1.005, 1.015, 0.285, 1234.5678, -98765.4321,
        123456789.123456789, 1e15 + 0.5, 9999999999999998, 1e16, 3.14159265358979, 0.1, 1.0 / 3, 2.675, 4.35, 1e-10, 5e-324,
        double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 7.5e-8, 0.49999999999999994,
    ];

    /// <summary>
    /// Values for the functions: <paramref name="count"/> from each of the ranges a function of Math takes (around zero,
    /// [-1, 1], positive, large, any bits), as doubles.
    /// </summary>
    public static IEnumerable<double> RandomDoubles(int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            yield return (i % 5) switch
            {
                0 => random.NextDouble() * 20 - 10,
                1 => random.NextDouble() * 2 - 1,
                2 => Math.Exp(random.NextDouble() * 40 - 20),
                3 => Math.Exp(random.NextDouble() * 1400 - 700) * (random.Next(2) == 0 ? 1 : -1),
                _ => BitConverter.Int64BitsToDouble(random.NextInt64() ^ ((long)random.Next(2) << 63)),
            };
        }
    }

    /// <summary>The same as floats (in range of a float: the large ones up to 1e38).</summary>
    public static IEnumerable<float> RandomFloats(int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            yield return (i % 5) switch
            {
                0 => (float)(random.NextDouble() * 20 - 10),
                1 => (float)(random.NextDouble() * 2 - 1),
                2 => (float)Math.Exp(random.NextDouble() * 40 - 20),
                3 => (float)(Math.Exp(random.NextDouble() * 170 - 85) * (random.Next(2) == 0 ? 1 : -1)),
                _ => BitConverter.Int32BitsToSingle(random.Next() ^ (random.Next(2) << 31)),
            };
        }
    }

    /// <summary>
    /// Compares ChipSharp's output with .NET's line by line: equal, but for the transcendental functions, whose numbers
    /// may each be up to their ulps away; fails with the lines that are neither. Returns, by function, how many of its
    /// lines were not equal.
    /// </summary>
    public static Dictionary<string, int> AssertCloseToDotnet(string dotnet, string chipSharp)
    {
        string[] expected = dotnet.Split("\r\n");
        string[] actual = chipSharp.Split("\r\n");
        Assert.Equal(expected.Length, actual.Length);
        var differing = new Dictionary<string, int>(StringComparer.Ordinal);
        var failures = new List<string>();
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] == actual[i])
            {
                continue;
            }

            var tag = expected[i].Split(' ')[0];
            var name = tag[(tag.IndexOf('.', StringComparison.Ordinal) + 1)..];
            var isFloat = tag.StartsWith("F.", StringComparison.Ordinal) || tag.StartsWith("GF.", StringComparison.Ordinal);
            differing[tag] = differing.GetValueOrDefault(tag) + 1;
            var (a, b) = (expected[i].Split(' '), actual[i].Split(' '));
            if (Transcendental.TryGetValue(name, out var ulps) && a.Length == b.Length && a[0] == b[0]
                && a.Skip(1).Zip(b.Skip(1)).All(pair => Ulps(pair.First, pair.Second, isFloat) <= ulps))
            {
                continue;
            }

            failures.Add($"line {i + 1}: .NET {expected[i]}, ChipSharp {actual[i]}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} lines differ:\n{string.Join('\n', failures.Take(25))}");
        return differing;
    }

    /// <summary>How many representable values apart two printed numbers are.</summary>
    private static long Ulps(string a, string b, bool isFloat)
    {
        if (isFloat)
        {
            var (x, y) = (float.Parse(a, CultureInfo.InvariantCulture), float.Parse(b, CultureInfo.InvariantCulture));
            static long Order(int bits) => bits < 0 ? (long)int.MinValue - bits : bits;
            return float.IsNaN(x) || float.IsNaN(y) ? (float.IsNaN(x) && float.IsNaN(y) ? 0 : long.MaxValue)
                : Math.Abs(Order(BitConverter.SingleToInt32Bits(x)) - Order(BitConverter.SingleToInt32Bits(y)));
        }

        var (p, q) = (double.Parse(a, CultureInfo.InvariantCulture), double.Parse(b, CultureInfo.InvariantCulture));
        static long OrderD(long bits) => bits < 0 ? long.MinValue - bits : bits;
        return double.IsNaN(p) || double.IsNaN(q) ? (double.IsNaN(p) && double.IsNaN(q) ? 0 : long.MaxValue)
            : Math.Abs(OrderD(BitConverter.DoubleToInt64Bits(p)) - OrderD(BitConverter.DoubleToInt64Bits(q)));
    }
}
