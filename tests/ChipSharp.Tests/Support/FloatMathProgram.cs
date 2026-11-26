using System.Globalization;
using System.Text;

namespace ChipSharp.Tests.Support;

/// <summary>
/// The differential tests of float math: one C# program that prints the result of every float and double operation
/// ChipSharp compiles (arithmetic, %, comparisons and the branches on them, negation, conversions both ways,
/// Math.Sqrt, Abs, Min and Max) for many values, special ones and random ones. .NET runs it for the expected output;
/// ChipSharp builds it for the PC and the Pico, whose output must be the same, line by line.
/// </summary>
/// <remarks>
/// Every value goes through Math.Max(x, x) first: it is x itself, but LLVM cannot see into the runtime function, so
/// the operations are done when the program runs, by the PC's float hardware or the Pico's soft-float routines,
/// rather than folded while compiling.
/// </remarks>
internal sealed class FloatMathProgram
{
    private const string Helpers = """
        using System;

        class Program
        {
            static double D(double x) => Math.Max(x, x);
            static float F(float x) => MathF.Max(x, x);
            static int I(int x) => x ^ (int)Math.Max(0.0, 0.0);
            static long L(long x) => x ^ (long)Math.Max(0.0, 0.0);

            static void Doubles(double a, double b)
            {
                a = D(a);
                b = D(b);
                Console.WriteLine(a + b);
                Console.WriteLine(a - b);
                Console.WriteLine(a * b);
                Console.WriteLine(a / b);
                Console.WriteLine(a % b);
                Console.WriteLine(Math.Min(a, b));
                Console.WriteLine(Math.Max(a, b));
                Console.WriteLine(a == b);
                Console.WriteLine(a < b);
                Console.WriteLine(a > b);
                Console.WriteLine(a <= b);
                Console.WriteLine(a >= b);
                Console.WriteLine(a != b);
                int branches = 0;
                if (a == b) branches += 1;
                if (a != b) branches += 2;
                if (a < b) branches += 4;
                if (a <= b) branches += 8;
                if (a > b) branches += 16;
                if (a >= b) branches += 32;
                if (!(a < b)) branches += 64;
                if (!(a <= b)) branches += 128;
                if (!(a > b)) branches += 256;
                if (!(a >= b)) branches += 512;
                Console.WriteLine(branches);
            }

            static void Floats(float a, float b)
            {
                a = F(a);
                b = F(b);
                Console.WriteLine(a + b);
                Console.WriteLine(a - b);
                Console.WriteLine(a * b);
                Console.WriteLine(a / b);
                Console.WriteLine(a % b);
                Console.WriteLine(MathF.Min(a, b));
                Console.WriteLine(Math.Max(a, b));
                Console.WriteLine(a == b);
                Console.WriteLine(a < b);
                Console.WriteLine(a > b);
                Console.WriteLine(a <= b);
                Console.WriteLine(a >= b);
                Console.WriteLine(a != b);
                int branches = 0;
                if (a == b) branches += 1;
                if (a != b) branches += 2;
                if (a < b) branches += 4;
                if (a <= b) branches += 8;
                if (a > b) branches += 16;
                if (a >= b) branches += 32;
                if (!(a < b)) branches += 64;
                if (!(a <= b)) branches += 128;
                if (!(a > b)) branches += 256;
                if (!(a >= b)) branches += 512;
                Console.WriteLine(branches);
            }

            static void Double(double a)
            {
                a = D(a);
                Console.WriteLine(-a);
                Console.WriteLine(Math.Sqrt(a));
                Console.WriteLine(Math.Abs(a));
                Console.WriteLine((float)a);
                Console.WriteLine((int)a);
                Console.WriteLine((uint)a);
                Console.WriteLine((long)a);
                Console.WriteLine((ulong)a);
                Console.WriteLine((short)a);
                Console.WriteLine((ushort)a);
                Console.WriteLine((sbyte)a);
                Console.WriteLine((byte)a);
            }

            static void Float(float a)
            {
                a = F(a);
                Console.WriteLine(-a);
                Console.WriteLine(MathF.Sqrt(a));
                Console.WriteLine(Math.Abs(a));
                Console.WriteLine((double)a);
                Console.WriteLine((int)a);
                Console.WriteLine((uint)a);
                Console.WriteLine((long)a);
                Console.WriteLine((ulong)a);
                Console.WriteLine((short)a);
                Console.WriteLine((ushort)a);
                Console.WriteLine((sbyte)a);
                Console.WriteLine((byte)a);
            }

            static void Mixed(float f, double d)
            {
                f = F(f);
                d = D(d);
                Console.WriteLine(f + d);
                Console.WriteLine(f * d);
                Console.WriteLine(d / f);
                Console.WriteLine(f < d);
                Console.WriteLine((float)(f * d));
                float g = (float)d;
                Console.WriteLine(g * f + g);
            }

            static void Int32(int i)
            {
                i = I(i);
                Console.WriteLine((float)i);
                Console.WriteLine((double)i);
                uint u = (uint)i;
                Console.WriteLine((float)u);
                Console.WriteLine((double)u);
            }

            static void Int64(long l)
            {
                l = L(l);
                Console.WriteLine((float)l);
                Console.WriteLine((double)l);
                ulong u = (ulong)l;
                Console.WriteLine((float)u);
                Console.WriteLine((double)u);
            }
        """;

    private static readonly string[] PairLines =
        ["a + b", "a - b", "a * b", "a / b", "a % b", "Min", "Max", "a == b", "a < b", "a > b", "a <= b", "a >= b",
            "a != b", "branches"];

    private static readonly string[] DoubleLines =
        ["-a", "Sqrt", "Abs", "(float)", "(int)", "(uint)", "(long)", "(ulong)", "(short)", "(ushort)", "(sbyte)", "(byte)"];

    private static readonly string[] FloatLines =
        ["-a", "Sqrt", "Abs", "(double)", "(int)", "(uint)", "(long)", "(ulong)", "(short)", "(ushort)", "(sbyte)", "(byte)"];

    private static readonly string[] MixedLines = ["f + d", "f * d", "d / f", "f < d", "(float)(f * d)", "g * f + g"];

    private static readonly string[] ConversionLines = ["(float)", "(double)", "(float)unsigned", "(double)unsigned"];

    private readonly StringBuilder _main = new();
    private readonly List<string> _lines = [];

    /// <summary>The number of values the program computes and prints.</summary>
    public int Count => _lines.Count;

    /// <summary>What each line of the output is, e.g. "Doubles(1.5, NaN): a % b", in order.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Every arithmetic and comparison result of each pair.</summary>
    public FloatMathProgram Doubles(IEnumerable<(double A, double B)> pairs) =>
        Add(pairs, p => $"Doubles({Literal(p.A)}, {Literal(p.B)})", PairLines);

    public FloatMathProgram Floats(IEnumerable<(float A, float B)> pairs) =>
        Add(pairs, p => $"Floats({Literal(p.A)}, {Literal(p.B)})", PairLines);

    /// <summary>Negation, square root, Abs and every conversion of each value.</summary>
    public FloatMathProgram Double(IEnumerable<double> values) => Add(values, v => $"Double({Literal(v)})", DoubleLines);

    public FloatMathProgram Float(IEnumerable<float> values) => Add(values, v => $"Float({Literal(v)})", FloatLines);

    /// <summary>A float and a double together, and a double rounded to float, then float arithmetic.</summary>
    public FloatMathProgram Mixed(IEnumerable<(float F, double D)> pairs) =>
        Add(pairs, p => $"Mixed({Literal(p.F)}, {Literal(p.D)})", MixedLines);

    /// <summary>Each int as a float and a double, read as signed and as unsigned.</summary>
    public FloatMathProgram Int32(IEnumerable<int> values) =>
        Add(values, v => $"Int32({v.ToString(CultureInfo.InvariantCulture)})", ConversionLines);

    public FloatMathProgram Int64(IEnumerable<long> values) =>
        Add(values, v => $"Int64({v.ToString(CultureInfo.InvariantCulture)}L)", ConversionLines);

    /// <summary>The C# program.</summary>
    public string Source() => $"{Helpers}\n    static void Main()\n    {{\n{_main}    }}\n}}\n";

    /// <summary>
    /// Compares what ChipSharp's program printed with what .NET printed, line by line, and fails with the first
    /// differences, each with the operation and values it came from.
    /// </summary>
    public void AssertSameOutput(string dotnet, string chipSharp)
    {
        string[] expected = dotnet.Split("\r\n");
        string[] actual = chipSharp.Split("\r\n");
        Assert.Equal(_lines.Count + 1, expected.Length); // the last line ends with \r\n too
        var mismatches = Enumerable.Range(0, Math.Max(expected.Length, actual.Length))
            .Where(i => i >= expected.Length || i >= actual.Length || expected[i] != actual[i])
            .Select(i => $"{(i < _lines.Count ? _lines[i] : "(the end)")}: .NET {(i < expected.Length ? expected[i] : "-")}, " +
                $"ChipSharp {(i < actual.Length ? actual[i] : "-")}")
            .ToList();
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {_lines.Count} values differ:\n{string.Join('\n', mismatches.Take(25))}");
    }

    /// <summary>A double as C# writes it: the shortest text that reads back as the same value.</summary>
    public static string Literal(double value) => value switch
    {
        double.NaN => "double.NaN",
        double.PositiveInfinity => "double.PositiveInfinity",
        double.NegativeInfinity => "double.NegativeInfinity",
        _ => value.ToString("R", CultureInfo.InvariantCulture) + "d",
    };

    public static string Literal(float value) => value switch
    {
        float.NaN => "float.NaN",
        float.PositiveInfinity => "float.PositiveInfinity",
        float.NegativeInfinity => "float.NegativeInfinity",
        _ => value.ToString("R", CultureInfo.InvariantCulture) + "f",
    };

    /// <summary>
    /// Doubles whose results are worth checking: zeros, NaN, infinities, subnormals, the limits, everyday values,
    /// halves (conversions round toward zero), and the edges of every integer type's range.
    /// </summary>
    public static double[] SpecialDoubles { get; } =
    [
        0.0, -0.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, -double.Epsilon,
        BitConverter.UInt64BitsToDouble(0x000FFFFFFFFFFFFF), BitConverter.UInt64BitsToDouble(0x0010000000000000),
        double.MaxValue, double.MinValue, 1, -1, 0.5, -0.5, 1.5, 2.5, -2.5, 0.1, 0.2, 0.3, 1.0 / 3, 3, 7, Math.PI, Math.E,
        1e-300, 1e300, 1e10, -1e10, 127.9, 128.5, -128.9, -129.5, 255.9, 256.1, 300.7, -300.7, 32767.9, 32768.5,
        -32769.5, 65535.9, 65536.5, 2147483647.5, 2147483648, -2147483648.5, -2147483649, 3e9, 4294967295.5,
        4294967296, 5e9, 9007199254740993, 9.2233720368547748E+18, 9.2233720368547758E+18, -9.2233720368547758E+18,
        -9.2233720368547779E+18, 1e19, 1.8446744073709550E+19, 1.8446744073709552E+19, -1e19, 1e20,
    ];

    /// <summary>The same for floats.</summary>
    public static float[] SpecialFloats { get; } =
    [
        0f, -0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.Epsilon, -float.Epsilon,
        BitConverter.UInt32BitsToSingle(0x007FFFFF), BitConverter.UInt32BitsToSingle(0x00800000), float.MaxValue,
        float.MinValue, 1, -1, 0.5f, -0.5f, 1.5f, 2.5f, -2.5f, 0.1f, 0.2f, 0.3f, 1f / 3, 3, 7, MathF.PI, MathF.E,
        1e-30f, 1e30f, 1e10f, -1e10f, 127.9f, 128.5f, -128.9f, -129.5f, 255.9f, 256.1f, 300.7f, -300.7f, 32767.9f,
        32768.5f, -32769.5f, 65535.9f, 65536.5f, 16777217f, 2147483520f, 2147483648f, -2147483648f, -2147483904f,
        3e9f, 4294967040f, 4294967296f, 5e9f, 9.223371E+18f, 9.223372E+18f, -9.223372E+18f, -9.2233731E+18f, 1e19f,
        1.8446743E+19f, 1.8446744E+19f, -1e19f, 1e20f,
    ];

    /// <summary>Ints and longs whose conversions round, tie, or sit at the edges.</summary>
    public static int[] SpecialInt32s { get; } =
        [0, 1, -1, 7, 16777216, 16777217, 16777219, -16777217, 33554435, int.MaxValue, int.MinValue, int.MaxValue - 64, -123456789];

    public static long[] SpecialInt64s { get; } =
    [
        0, 1, -1, int.MaxValue, 1L << 53, (1L << 53) + 1, (1L << 53) + 3, -(1L << 53) - 1, (1L << 62) + (1L << 38) + 1,
        (1L << 62) + (1L << 38), -((1L << 62) + (1L << 38) + 1), long.MaxValue, long.MinValue,
        unchecked((long)0x8000_0080_0000_0001), unchecked((long)0x8000_0400_0000_0001), unchecked((long)0x8000_0000_0000_0400),
        -2, unchecked((long)0xFFFF_FF80_0000_0001), 1234567890123456789,
    ];

    /// <summary>
    /// <paramref name="count"/> random doubles from a fixed seed: a third any bit pattern, a third everyday numbers
    /// (up to nine digits, scaled by a power of ten), a third whole numbers around the integer types' edges.
    /// </summary>
    public static IEnumerable<double> RandomDoubles(int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            yield return (i % 3) switch
            {
                0 => BitConverter.UInt64BitsToDouble((ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63)),
                1 => random.Next(-1_000_000, 1_000_000) * Math.Pow(10, random.Next(-12, 22)) / 1000,
                _ => Math.ScaleB(random.NextDouble() * 2 - 1, random.Next(0, 66)),
            };
        }
    }

    public static IEnumerable<float> RandomFloats(int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            yield return (i % 3) switch
            {
                0 => BitConverter.UInt32BitsToSingle((uint)random.Next() ^ ((uint)random.Next(2) << 31)),
                1 => (float)(random.Next(-1_000_000, 1_000_000) * Math.Pow(10, random.Next(-12, 14)) / 1000),
                _ => (float)Math.ScaleB(random.NextDouble() * 2 - 1, random.Next(0, 66)),
            };
        }
    }

    public static IEnumerable<long> RandomInt64s(int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            yield return random.NextInt64() >> random.Next(0, 40);
        }
    }

    /// <summary>Every value with every other one.</summary>
    public static IEnumerable<(T, T)> AllPairs<T>(IReadOnlyList<T> values) =>
        values.SelectMany(a => values.Select(b => (a, b)));

    /// <summary>Consecutive values paired up: (v0, v1), (v2, v3), ...</summary>
    public static IEnumerable<(T, T)> Pairs<T>(IEnumerable<T> values) => values.Chunk(2).Select(p => (p[0], p[^1]));

    private FloatMathProgram Add<T>(IEnumerable<T> calls, Func<T, string> call, string[] lines)
    {
        foreach (var item in calls)
        {
            var text = call(item);
            _main.Append("        ").Append(text).Append(";\n");
            _lines.AddRange(lines.Select(line => $"{text}: {line}"));
        }

        return this;
    }
}
