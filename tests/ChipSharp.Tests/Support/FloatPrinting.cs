using System.Globalization;
using ChipSharp.Frontend;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Support;

/// <summary>
/// Doubles and floats to print through the runtimes' WriteLineFloat64 and WriteLineFloat32 and compare with what
/// .NET writes, and the TIR program that prints them (C# cannot: float instructions arrive later).
/// </summary>
internal static class FloatPrinting
{
    /// <summary>
    /// The cases every test prints: zeros, NaNs, infinities, subnormals, the largest and smallest values, everyday
    /// values, and the powers of ten (and the values next to them) on both sides of each switch to exponent notation:
    /// below 0.0001, and from 10^17 up.
    /// </summary>
    public static IEnumerable<double> DoubleCases()
    {
        double[] cases =
        [
            0.0, -0.0, double.NaN, -double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            Bits(0x7FF0000000000001), Bits(0xFFFFFFFFFFFFFFFF), // a signalling NaN, a negative NaN with every bit set
            double.Epsilon, -double.Epsilon, 2 * double.Epsilon, 3 * double.Epsilon, Bits(0x000FFFFFFFFFFFFF),
            Bits(0x0010000000000000), Bits(0x0010000000000001), double.MaxValue, double.MinValue,
            Bits(0x7FEFFFFFFFFFFFFE), 1.0, -1.0, 0.1, 0.2, 0.3, 0.1 + 0.2, 1.0 / 3, 2.0 / 3, -1.0 / 3, Math.PI, Math.E,
            123456789012345680, 12345678901234567, 9007199254740992, 9007199254740993, 9007199254740994, 1e23,
            4.35, 2.5, 0.5, 100, 1234.5678, -98.76, 1e21, 1.7976931348623157e308, 2.2250738585072014E-308,
            4.9406564584124654E-324, 5e-324, 1e-323, 1e-322, 1e-300, 123.456, 0.000123456, 1.5e-5,
        ];
        foreach (var value in cases)
        {
            yield return value;
        }

        foreach (var power in (int[])[-7, -6, -5, -4, -3, -2, 14, 15, 16, 17, 18, 19])
        {
            var ten = double.Parse($"1e{power}", CultureInfo.InvariantCulture);
            yield return ten;
            yield return -ten;
            yield return Math.BitDecrement(ten);
            yield return Math.BitIncrement(ten);
            foreach (var digits in (double[])[1.5, 9.5, 9.99, 1.2345678901234567])
            {
                yield return digits * ten;
            }
        }
    }

    /// <summary>The same cases for float; it switches to exponent notation from 10^9 up.</summary>
    public static IEnumerable<float> FloatCases()
    {
        float[] cases =
        [
            0f, -0f, float.NaN, -float.NaN, float.PositiveInfinity, float.NegativeInfinity, Bits(0x7F800001u),
            Bits(0xFFFFFFFFu), float.Epsilon, -float.Epsilon, 2 * float.Epsilon, Bits(0x007FFFFFu), Bits(0x00800000u),
            Bits(0x00800001u), float.MaxValue, float.MinValue, Bits(0x7F7FFFFEu), 1f, -1f, 0.1f, 0.2f, 0.3f,
            0.1f + 0.2f, 1f / 3, 2f / 3, MathF.PI, MathF.E, 16777216f, 16777217f, 123456789f, 1e10f, 4.35f, 2.5f,
            1234.5678f, -98.76f, 1e-45f, 1.17549435E-38f, 3.4028235E+38f, 0.000123456f, 1.5e-5f,
        ];
        foreach (var value in cases)
        {
            yield return value;
        }

        foreach (var power in (int[])[-7, -6, -5, -4, -3, -2, 6, 7, 8, 9, 10, 11])
        {
            var ten = float.Parse($"1e{power}", CultureInfo.InvariantCulture);
            yield return ten;
            yield return -ten;
            yield return MathF.BitDecrement(ten);
            yield return MathF.BitIncrement(ten);
            foreach (var digits in (float[])[1.5f, 9.5f, 9.99f, 1.2345678f])
            {
                yield return digits * ten;
            }
        }
    }

    /// <summary>Every power of 2 a double has, and every power of ten with the values next to it.</summary>
    public static IEnumerable<double> DoublePowers()
    {
        for (var bits = 1UL; bits < 0x7FF0000000000000; bits = bits < 0x0010000000000000 ? bits << 1 : bits + 0x0010000000000000)
        {
            yield return Bits(bits);
        }

        for (var power = -324; power <= 308; power++)
        {
            var ten = double.Parse($"1e{power}", CultureInfo.InvariantCulture);
            yield return ten;
            yield return Math.BitDecrement(ten);
            yield return Math.BitIncrement(ten);
        }
    }

    /// <summary>
    /// <paramref name="count"/> random doubles from a fixed seed: half any bit pattern, half everyday numbers (up to
    /// nine digits, scaled by a power of ten).
    /// </summary>
    public static IEnumerable<double> RandomDoubles(int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count / 2; i++)
        {
            yield return Bits((ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63));
            yield return random.Next(-1_000_000, 1_000_000) * Math.Pow(10, random.Next(-12, 22)) / 1000;
        }
    }

    /// <summary>Every power of 2 a float has, and every power of ten with the values next to it.</summary>
    public static IEnumerable<float> FloatPowers()
    {
        for (var bits = 1u; bits < 0x7F800000u; bits = bits < 0x00800000u ? bits << 1 : bits + 0x00800000u)
        {
            yield return Bits(bits);
        }

        for (var power = -45; power <= 38; power++)
        {
            var ten = float.Parse($"1e{power}", CultureInfo.InvariantCulture);
            yield return ten;
            yield return MathF.BitDecrement(ten);
            yield return MathF.BitIncrement(ten);
        }
    }

    /// <summary><paramref name="count"/> random floats from a fixed seed, like <see cref="RandomDoubles"/>.</summary>
    public static IEnumerable<float> RandomFloats(int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count / 2; i++)
        {
            yield return Bits((uint)random.Next() ^ ((uint)random.Next(2) << 31));
            yield return (float)(random.Next(-1_000_000, 1_000_000) * Math.Pow(10, random.Next(-12, 14)) / 1000);
        }
    }

    /// <summary>
    /// A program whose Main prints every double, then every float, with the runtime functions Console.WriteLine(double)
    /// and Console.WriteLine(float) become (each value a constant).
    /// </summary>
    public static TirModule Program(string name, IEnumerable<double> doubles, IEnumerable<float> floats)
    {
        var module = new TirModule(name);
        var main = module.AddFunction("Program.Main", TirType.Void);
        module.EntryPoint = main;
        var ir = new TirBuilder(main.AddBlock("entry"));
        var writeDouble = Declare(module, "System.Console.WriteLine(double)");
        var writeFloat = Declare(module, "System.Console.WriteLine(float)");
        foreach (var value in doubles)
        {
            ir.Call(writeDouble, ir.Const(TirType.F64, value));
        }

        foreach (var value in floats)
        {
            ir.Call(writeFloat, ir.Const(TirType.F32, value));
        }

        ir.Return();
        return module;
    }

    /// <summary>What .NET writes for each value, in the invariant culture, in the order <see cref="Program"/> prints them.</summary>
    public static List<string> Expected(IEnumerable<double> doubles, IEnumerable<float> floats) =>
    [
        .. doubles.Select(value => value.ToString(CultureInfo.InvariantCulture)),
        .. floats.Select(value => value.ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>
    /// Compares the lines a program printed (each ended with "\r\n") with <paramref name="expected"/>, one by one,
    /// and fails with the first differences if any line differs.
    /// </summary>
    public static void AssertLines(IReadOnlyList<string> expected, string output)
    {
        Assert.EndsWith("\r\n", output);
        var lines = output[..^2].Split("\r\n");
        Assert.Equal(expected.Count, lines.Length);
        var mismatches = Enumerable.Range(0, lines.Length).Where(i => lines[i] != expected[i])
            .Select(i => $"line {i + 1}: .NET writes {expected[i]}, the runtime wrote {lines[i]}")
            .ToList();
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {expected.Count} lines differ:\n{string.Join('\n', mismatches.Take(20))}");
    }

    private static TirFunction Declare(TirModule module, string libraryMethod)
    {
        var function = KnownExternals.All.Single(f => f.LibraryMethod == libraryMethod);
        return module.AddExternalFunction(function.DisplayName, function.ReturnType,
            function.ParameterTypes.Select(type => ("value", type)));
    }

    private static double Bits(ulong bits) => BitConverter.UInt64BitsToDouble(bits);

    private static float Bits(uint bits) => BitConverter.UInt32BitsToSingle(bits);
}
