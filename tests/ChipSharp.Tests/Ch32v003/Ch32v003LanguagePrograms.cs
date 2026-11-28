namespace ChipSharp.Tests.Ch32v003;

/// <summary>
/// Programs for what RV32EC and the ilp32e ABI make different on the CH32V003, beyond the corpora
/// (<see cref="Ch32v003LanguageTests"/>): calls that pass more than its six argument registers hold, 64-bit values split
/// between a register and the stack, structs passed and returned; every multiplication, division and remainder of
/// 32 and 64 bits through the runtime's helpers, with every sign; float and double arithmetic, rounding, NaN, infinities,
/// zeros and subnormals in compiler-rt's software, and the conversions both ways. Each works on values the program only
/// knows when it runs (a volatile zero mixed in), so that neither Roslyn nor LLVM computes them while compiling.
/// </summary>
internal static class Ch32v003LanguagePrograms
{
    /// <summary>Calls with up to ten arguments of every kind, structs passed and returned, and a recursion that passes them on.</summary>
    public const string Calls = """
        using System;

        struct Pair
        {
            public int A;
            public long B;
        }

        struct Small
        {
            public short X;
            public byte Y;
        }

        struct Triple
        {
            public double X, Y, Z;
        }

        static class Program
        {
            static volatile int zero;

            static int Z => zero;

            static long Seven(int a, int b, int c, int d, int e, int f, int g) =>
                a + 2L * b + 3L * c + 4L * d + 5L * e + 6L * f + 7L * g;

            // x: its low half in a5, its high half on the stack
            static long Split(int a, int b, int c, int d, int e, long x) => a - b + c - d + e + x;

            static long Longs(long a, long b, long c, long d) => a * 3 - b + c * 5 - d;

            static double Doubles(double a, double b, double c, double d) => a * b + c / d;

            static double Mixed(int a, double b, long c, float d, Pair p, Small s, double e, byte f, ushort g, ulong h) =>
                a + b * 2 + c + d + p.A + p.B + s.X + s.Y + e / 4 + f + g + (double)h;

            static Pair MakePair(int a, long b) => new Pair { A = a * 2, B = b - a };

            static Small MakeSmall(int x) => new Small { X = (short)x, Y = (byte)(x >> 8) };

            static Triple Scale(Triple t, double k) => new Triple { X = t.X * k, Y = t.Y * k, Z = t.Z * k };

            static (int, long, double) Three(int a) => (a, a * 1000000007L, a / 3.0);

            static ulong Wide(ulong a, uint b, ulong c, uint d, ulong e) => (a ^ b) + (c ^ d) + e;

            static long Recurse(int depth, long sum, double scale, Pair p, int a, int b, int c)
            {
                if (depth == 0)
                {
                    return sum + p.A + p.B + a * 100 + b * 10 + c + (long)scale;
                }

                return Recurse(depth - 1, sum * 3 + depth, scale * 1.5, new Pair { A = p.A + 1, B = p.B ^ depth }, b, c, a);
            }

            static float Floats(float a, float b, float c, float d, float e, float f, float g, float h) =>
                a - b + c * d - e / f + g * h;

            static void Main()
            {
                Console.WriteLine(Seven(1 + Z, -2 + Z, 3, int.MaxValue + Z, int.MinValue + Z, 6, -7 + Z));
                Console.WriteLine(Split(1, 2 + Z, 3, 4, 5, 0x123456789ABCDEF + Z));
                Console.WriteLine(Split(Z, Z, Z, Z, Z, long.MinValue + Z));
                Console.WriteLine(Longs(long.MaxValue + Z, -1 + Z, 0x100000000L + Z, long.MinValue + Z));
                Console.WriteLine(Doubles(1.5 + Z, -2.25, 1e300 + Z, 1e-10));
                var pair = new Pair { A = 7 + Z, B = -1234567890123 + Z };
                var small = new Small { X = (short)(-300 + Z), Y = (byte)(200 + Z) };
                Console.WriteLine(Mixed(1 + Z, 0.5, 1L << 40, 0.25f, pair, small, 10, 255, 65535, ulong.MaxValue));
                var made = MakePair(21 + Z, 1000000000000 + Z);
                Console.WriteLine($"{made.A} {made.B}");
                var s = MakeSmall(0x1234 + Z);
                Console.WriteLine($"{s.X} {s.Y}");
                var t = Scale(new Triple { X = 1 + Z, Y = -2, Z = 0.1 }, 3);
                Console.WriteLine($"{t.X} {t.Y} {t.Z}");
                var (i, l, d) = Three(10 + Z);
                Console.WriteLine($"{i} {l} {d}");
                Console.WriteLine(Wide(ulong.MaxValue - (ulong)Z, 0x80000000u, 0x123456789ul, uint.MaxValue, 1));
                Console.WriteLine(Recurse(12 + Z, 1, 1, pair, 1, 2, 3));
                Console.WriteLine(Floats(1 + Z, 2, 3, 4, 5, 6, 7, 8.5f));
            }
        }
        """;

    /// <summary>
    /// Multiplication, division and remainder of every pair of edge values, in 32 and 64 bits, signed and unsigned; the
    /// shifts of 64 bits by every amount; Math.BigMul and Math.DivRem.
    /// </summary>
    public const string Arithmetic = """
        using System;

        static class Program
        {
            static volatile int zero;

            static readonly int[] Ints =
            [
                0, 1, -1, 2, -2, 3, -7, 10, -10, 255, 65536, 123456789, -987654321, int.MaxValue, int.MinValue,
                int.MinValue + 1,
            ];

            static readonly long[] Longs =
            [
                0, 1, -1, 3, -10, 0xFFFFFFFF, 0x100000000, -0x100000000, 4294967297, 1234567890123456789,
                -987654321987654321, long.MaxValue, long.MinValue, long.MinValue + 1,
            ];

            static void Main()
            {
                foreach (int x in Ints)
                {
                    int a = x ^ zero;
                    foreach (int b in Ints)
                    {
                        Console.Write($"{a} {b}: {a * b} {(uint)a * (uint)b}");
                        if (b != 0)
                        {
                            Console.Write($" {(uint)a / (uint)b} {(uint)a % (uint)b}");
                            if (a != int.MinValue || b != -1)
                            {
                                Console.Write($" {a / b} {a % b}");
                            }
                        }

                        Console.WriteLine();
                    }
                }

                foreach (long x in Longs)
                {
                    long a = x ^ zero;
                    foreach (long b in Longs)
                    {
                        Console.Write($"{a} {b}: {a * b} {(ulong)a * (ulong)b}");
                        if (b != 0)
                        {
                            Console.Write($" {(ulong)a / (ulong)b} {(ulong)a % (ulong)b}");
                            if (a != long.MinValue || b != -1)
                            {
                                Console.Write($" {a / b} {a % b}");
                            }
                        }

                        Console.WriteLine();
                    }
                }

                long value = -0x123456789ABCDEF ^ zero;
                for (int n = 0; n < 70; n += 3)
                {
                    int shift = n + zero;
                    Console.WriteLine($"{n}: {value << shift} {value >> shift} {(ulong)value >> shift} {1 << shift} {-1 >> shift}");
                }

                long product = Math.BigMul(int.MinValue + zero, int.MinValue + zero);
                long high = Math.BigMul(long.MaxValue - zero, -3L + zero, out long low);
                ulong unsignedHigh = Math.BigMul(ulong.MaxValue - (ulong)zero, 0xFFFFFFFF00000001ul, out ulong unsignedLow);
                Console.WriteLine($"{product} {high} {low} {unsignedHigh} {unsignedLow}");
                var (quotient, remainder) = Math.DivRem(-17 + zero, 5);
                var (wideQuotient, wideRemainder) = Math.DivRem(long.MinValue + zero, 1000000007L);
                Console.WriteLine($"{quotient} {remainder} {wideQuotient} {wideRemainder}");
            }
        }
        """;

    /// <summary>
    /// Doubles in software: every operation of edge values (zeros, subnormals, the largest, infinities, NaN),
    /// comparisons, rounding to nearest even, and every conversion to and from whole numbers (saturating, as .NET's).
    /// </summary>
    public const string Doubles = """
        using System;

        static class Program
        {
            static volatile int zero;

            static readonly double[] Values =
            [
                0.0, -0.0, 1.0, -1.5, 0.1, 3.0, 1e308, -1e-308, 5e-324, 2.2250738585072014E-308, double.MaxValue,
                double.PositiveInfinity, double.NegativeInfinity, double.NaN,
            ];

            static readonly long[] Whole =
            [
                0, 1, -1, 16777217, 2147483647, -2147483648, 9007199254740993, long.MaxValue, long.MinValue,
                long.MaxValue - 511, 0x7FFFFFFFFFFFFDFF,
            ];

            static void P(double x)
            {
                Console.Write(x);
                Console.Write(' ');
            }

            static void P(long x)
            {
                Console.Write(x);
                Console.Write(' ');
            }

            static void Main()
            {
                foreach (double x in Values)
                {
                    double a = x + zero;
                    foreach (double b in Values)
                    {
                        P(a + b);
                        P(a - b);
                        P(a * b);
                        P(a / b);
                        P(a % b);
                        Console.WriteLine((a < b ? 1 : 0) + (a == b ? 2 : 0) + (a >= b ? 4 : 0) + (a != b ? 8 : 0));
                    }

                    P((int)a);
                    P((long)a);
                    P((uint)a);
                    P((long)(ulong)a);
                    P((short)a);
                    P((byte)a);
                    Console.WriteLine((float)a);
                    P(Math.Round(a));
                    P(Math.Floor(a));
                    P(Math.Ceiling(a));
                    P(Math.Truncate(a));
                    Console.WriteLine(Math.Sqrt(Math.Abs(a)));
                }

                foreach (long x in Whole)
                {
                    long w = x + zero;
                    P(w);
                    P((double)w);
                    P((double)(ulong)w);
                    Console.WriteLine((double)(int)w);
                }

                // rounding to nearest, ties to even
                double one = 1.0 + zero;
                P(one + 1e-16);
                P(one + 1.1102230246251565E-16);
                P(1e16 + one);
                P(5e-324 * (one / 2));
                P(5e-324 * (one * 1.5));
                Console.WriteLine(0.1 + 0.2 * one);
                P(1.0 / (zero - 0.0));
                P(-one / 0.0);
                P(0.0 / (one - 1));
                P(Math.Round(2.5 * one));
                P(Math.Round(-2.5 * one));
                P(Math.Round(3.5 * one));
                Console.WriteLine(Math.Round(0.125 * one, 2));
            }
        }
        """;

    /// <summary>
    /// Floats in software, the same way, and the conversions between floats, doubles and whole numbers, whose rounding
    /// is to nearest even.
    /// </summary>
    public const string Floats = """
        using System;

        static class Program
        {
            static volatile int zero;

            static readonly float[] Values =
            [
                0f, -0f, 1f, -1.5f, 0.1f, 3f, 3e38f, 1e-45f, 1.17549435E-38f, float.PositiveInfinity, float.NaN,
            ];

            static readonly long[] Whole =
            [
                0, 1, -1, 16777217, 2147483647, -2147483648, 9007199254740993, long.MaxValue, long.MinValue,
                long.MaxValue - 511, 0x7FFFFFFFFFFFFDFF,
            ];

            static readonly double[] Wide = [1e39, -1e39, 1e-46, 1.401298464324817E-45, 1.00000005960464477539, 1.0000001788139343, 0.1, double.NaN];

            static void P(float x)
            {
                Console.Write(x);
                Console.Write(' ');
            }

            static void P(long x)
            {
                Console.Write(x);
                Console.Write(' ');
            }

            static void Main()
            {
                foreach (float x in Values)
                {
                    float a = x + zero;
                    foreach (float b in Values)
                    {
                        P(a + b);
                        P(a - b);
                        P(a * b);
                        P(a / b);
                        P(a % b);
                        Console.WriteLine((a < b ? 1 : 0) + (a == b ? 2 : 0) + (a >= b ? 4 : 0) + (a != b ? 8 : 0));
                    }

                    P((int)a);
                    P((long)a);
                    P((uint)a);
                    P((long)(ulong)a);
                    Console.WriteLine(MathF.Sqrt(MathF.Abs(a)));
                }

                foreach (long x in Whole)
                {
                    long w = x + zero;
                    P((float)w);
                    P((float)(ulong)w);
                    Console.WriteLine((float)(uint)w);
                }

                foreach (double x in Wide)
                {
                    Console.WriteLine((float)(x + zero));
                }
            }
        }
        """;

    /// <summary>A recursion with no end through two methods, each with a struct on its frame, after a line.</summary>
    public const string Mutual = """
        using System;

        struct Frame
        {
            public long A, B, C;
        }

        static class Program
        {
            static long Down(int n, Frame frame) => n == 0 ? frame.A : Up(n + 1, new Frame { A = frame.B, B = frame.C, C = n }) + 1;

            static long Up(int n, Frame frame) => n == 0 ? frame.C : Down(n + 1, new Frame { A = frame.C + n, B = frame.A, C = frame.B }) * 2;

            static void Main()
            {
                Console.WriteLine("start");
                Console.WriteLine(Down(1, new Frame { A = 1, B = 2, C = 3 }));
            }
        }
        """;

    /// <summary>
    /// A recursion with no end that prints a double at every level: the text of a double, the deepest the runtime goes,
    /// below the last function that checks the stack.
    /// </summary>
    public const string Printing = """
        using System;

        static class Program
        {
            static double Deep(int n)
            {
                Console.WriteLine(n * 0.5);
                return Deep(n + 1) + n;
            }

            static void Main()
            {
                Console.WriteLine("start");
                Console.WriteLine(Deep(1));
            }
        }
        """;

    /// <summary>Recursions that end, deep for 2 KB of SRAM, and a frame of 768 bytes on the stack in a loop.</summary>
    public const string Finite = """
        using System;

        static class Program
        {
            static int Depth(int n) => n == 0 ? 0 : 1 + Depth(n - 1) * 1;

            static long Sum(ReadOnlySpan<int> values, int from) => from == values.Length ? 0 : values[from] + Sum(values, from + 1) / 2;

            static int Buffer(int seed)
            {
                Span<int> buffer = stackalloc int[192];
                for (int i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = seed + i;
                }

                int sum = 0;
                foreach (int value in buffer)
                {
                    sum += value;
                }

                return sum;
            }

            static void Main()
            {
                Console.WriteLine(Depth(40));
                Console.WriteLine(Sum(stackalloc int[] { 5, 6, 7, 8, 9, 10, 11 }, 0));
                for (int i = 0; i < 3; i++)
                {
                    Console.WriteLine(Buffer(i));
                }
            }
        }
        """;

    /// <summary>
    /// The runtime's helpers in Main and in an interrupt at once: the timer event every 5 ms runs the same
    /// multiplications, divisions and double arithmetic as Main, each twice, and so does Main, which spends nearly all
    /// its time in them and sums its results (<see cref="Work"/>, the same C# for the test to sum). The handler takes a
    /// good part of a millisecond (a double's division alone calls __muldi3, bit by bit, several times on a core without
    /// M), so it comes every 5 ms, not more often: more often, Main would never run.
    /// </summary>
    public const string Interrupted = """
        using System;
        using ChipSharp.Hardware;

        static class Program
        {
            static volatile int zero, ticks, wrong;

            static long Work(int i)
            {
                long a = 0x123456789ABL * (i + 1) + zero;
                long b = i * 7919L + 13;
                double d = i * 0.37 + zero;
                return a / b + a % b + (long)(d * 1e6 / 3.0) + a * b + (uint)a / (uint)(b | 1) + (long)Math.Sqrt(d * d + 1);
            }

            [Interrupt(Event.Timer)]
            static void Tick()
            {
                int i = ticks++ & 63;
                if (Work(i + 1000) != Work(i + 1000))
                {
                    wrong++;
                }
            }

            static void Main()
            {
                Timing.EveryMilliseconds(5);
                long sum = 0;
                for (int i = 0; i < 150; i++)
                {
                    long first = Work(i);
                    if (first != Work(i))
                    {
                        wrong++;
                    }

                    sum += first;
                }

                Timing.Stop();
                Console.WriteLine(sum);
                Console.WriteLine(wrong);
                Console.WriteLine(ticks > 20);
            }
        }
        """;

    /// <summary><see cref="Interrupted"/>'s Work, on the PC.</summary>
    public static long Work(int i)
    {
        long a = 0x123456789ABL * (i + 1);
        long b = i * 7919L + 13;
        double d = i * 0.37;
        return unchecked(a / b + a % b + (long)(d * 1e6 / 3.0) + a * b + (uint)a / (uint)(b | 1) + (long)Math.Sqrt(d * d + 1));
    }

    /// <summary>Divisions that .NET stops with an exception, after a line each: by zero, and the smallest value by -1.</summary>
    public static IReadOnlyList<(string Name, string Source, string Failure)> Failures { get; } =
    [
        ("IntByZero", Failing("int", "7", "0"), "ChipSharp_Runtime_DivideByZero"),
        ("LongRemainderByZero", Failing("long", "7", "0", "%"), "ChipSharp_Runtime_DivideByZero"),
        ("IntMinByMinusOne", Failing("int", "int.MinValue", "-1"), "ChipSharp_Runtime_Overflow"),
        ("LongMinByMinusOne", Failing("long", "long.MinValue", "-1"), "ChipSharp_Runtime_Overflow"),
    ];

    private static string Failing(string type, string dividend, string divisor, string operation = "/") => $$"""
        using System;

        static class Program
        {
            static volatile int zero;

            static void Main()
            {
                {{type}} a = {{dividend}} + zero, b = {{divisor}} + zero;
                Console.WriteLine(a {{operation}} 3);
                Console.WriteLine(a {{operation}} b);
                Console.WriteLine("not printed");
            }
        }
        """;
}
