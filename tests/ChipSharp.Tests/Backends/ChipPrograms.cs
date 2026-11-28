namespace ChipSharp.Tests.Backends;

/// <summary>
/// Programs of what C# compiles with no heap (see the README's table) written for the small chips: no double, which
/// SDCC's chips do not have, and little memory, so that they run on the STM8S103 (8 KB of flash, 1 KB of RAM) through C
/// and SDCC, and the smallest on the PFS154, as on every other target (<see cref="ConformancePrograms"/>, "Chips/...").
/// The other corpora print doubles nearly everywhere; these print the same features without one.
/// </summary>
internal static class ChipPrograms
{
    /// <summary>Bytes, shorts and ints at their edges, a loop and recursion: small enough for the PFS154.</summary>
    public const string Tiny = """
        using System;

        static class Program
        {
            static byte Sum(byte n) => n == 0 ? (byte)0 : (byte)(n + Sum((byte)(n - 1)));

            static void Main()
            {
                byte total = 0;
                for (byte step = 1; step < 30; step += 7)
                {
                    total += step;
                }

                Console.WriteLine(total);
                Console.WriteLine(Sum(4));
                byte b = 250;
                b += 10;
                Console.WriteLine(b);
                short s = short.MinValue;
                s--;
                Console.WriteLine(s);
                Console.WriteLine(-7 / 2);
                Console.WriteLine(-7 % 2);
            }
        }
        """;

    /// <summary>
    /// Generic methods, structs and constraints over values, an interface a struct implements called through its
    /// constraint, and generic math (INumber&lt;T&gt;) of bytes, shorts and ints.
    /// </summary>
    public const string Generics = """
        using System;
        using System.Numerics;

        interface ICounter
        {
            void Tick();
            int Count { get; }
        }

        struct Counter : ICounter
        {
            int count;
            public void Tick() => count++;
            public int Count => count;
        }

        struct Pair<T> where T : struct
        {
            public T First, Second;
            public Pair(T first, T second) { First = first; Second = second; }
            public Pair<T> Swapped() => new Pair<T>(Second, First);
        }

        static class Program
        {
            static T Sum<T>(ReadOnlySpan<T> values) where T : INumber<T>
            {
                T total = T.Zero;
                foreach (var value in values)
                {
                    total += value;
                }

                return total;
            }

            static T Larger<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;

            static T Next<T>(T value) where T : INumber<T> => value + T.One;

            static void Run<T>(ref T counter, int times) where T : struct, ICounter
            {
                for (int i = 0; i < times; i++)
                {
                    counter.Tick();
                }
            }

            static void Main()
            {
                ReadOnlySpan<byte> bytes = new byte[] { 100, 100, 55 };
                ReadOnlySpan<short> shorts = new short[] { -300, 1000, 32000 };
                Span<int> ints = stackalloc int[] { 1, -2, 3 };
                Console.WriteLine(Sum(bytes));
                Console.WriteLine(Sum(shorts));
                Console.WriteLine(Sum<int>(ints));
                Console.WriteLine(Larger(3, 7) + Larger((short)-1, (short)-5));
                Console.WriteLine(Larger('a', 'z'));
                var pair = new Pair<long>(1, 2).Swapped();
                Console.WriteLine(pair.First * 10 + pair.Second);
                var counter = new Counter();
                Run(ref counter, 5);
                Console.WriteLine(counter.Count);
                Console.WriteLine(Next(int.MaxValue) + " " + Next((byte)255) + " " + Next(-1L));
            }
        }
        """;

    /// <summary>
    /// Spans of an array, of stackalloc and of constant data: slices, ranges and indices from the end, CopyTo, Fill,
    /// Clear, the searches of MemoryExtensions.
    /// </summary>
    public const string Spans = """
        using System;

        static class Program
        {
            static ReadOnlySpan<byte> Packet => new byte[] { 0xA5, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x01 };

            static void Main()
            {
                int[] values = { 5, 6, 7, 8, 9 };
                Span<int> middle = values.AsSpan(1, 3);
                Console.WriteLine(middle[0] + middle[^1]);
                Console.WriteLine(values.AsSpan()[^2..].Length + values.AsSpan()[..2][1]);
                Span<byte> buffer = stackalloc byte[8];
                buffer.Fill(7);
                buffer.Slice(2, 3).Clear();
                Packet.Slice(6, 2).CopyTo(buffer);
                Console.WriteLine(buffer[0] + buffer[1] + buffer[2] + buffer[7]);
                Console.WriteLine(Packet.IndexOf((byte)0x78) + " " + Packet.SequenceEqual(Packet) + " " + Packet.StartsWith(Packet[..3]));
            }
        }
        """;

    /// <summary>
    /// The byte order: BinaryPrimitives of both orders, which give the same numbers on every chip (the STM8 is big-endian,
    /// the others little-endian), BitConverter's bits of floats, and MemoryMarshal, of the chip's own order, as portable
    /// C# reads it, by BitConverter.IsLittleEndian.
    /// </summary>
    public const string ByteOrder = """
        using System;
        using System.Buffers.Binary;
        using System.Runtime.InteropServices;

        static class Program
        {
            static ReadOnlySpan<byte> Packet => new byte[] { 0xA5, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x01 };

            static void Main()
            {
                ReadOnlySpan<byte> packet = Packet;
                Console.WriteLine(BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(1)));
                Console.WriteLine(BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(1)));
                Console.WriteLine(BinaryPrimitives.ReadInt32BigEndian(packet.Slice(3)));
                Console.WriteLine(BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(3)));
                Console.WriteLine(BinaryPrimitives.ReadInt64BigEndian(packet.Slice(1)));
                Console.WriteLine(BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(2)));
                Console.WriteLine(BinaryPrimitives.ReverseEndianness(0x12345678));
                Span<byte> written = stackalloc byte[8];
                BinaryPrimitives.WriteInt32BigEndian(written, -2);
                BinaryPrimitives.WriteUInt16LittleEndian(written.Slice(4), 0xBEEF);
                Console.WriteLine(written[0] + " " + written[3] + " " + written[4] + " " + written[5]);
                Console.WriteLine(BinaryPrimitives.TryReadInt32LittleEndian(written.Slice(6), out int none) + " " + none);
                BinaryPrimitives.WriteSingleBigEndian(written, 1.5f);
                Console.WriteLine(written[0] + " " + written[1] + " " + BinaryPrimitives.ReadSingleBigEndian(written));

                Console.WriteLine(BitConverter.SingleToInt32Bits(-2.5f));
                Console.WriteLine(BitConverter.Int32BitsToSingle(0x3F000000));
                int read = MemoryMarshal.Read<int>(packet.Slice(3));
                int expected = BitConverter.IsLittleEndian
                    ? BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(3))
                    : BinaryPrimitives.ReadInt32BigEndian(packet.Slice(3));
                Console.WriteLine(read == expected);
                Console.WriteLine(BitConverter.ToInt16(packet.Slice(1)) == MemoryMarshal.Read<short>(packet.Slice(1)));
                Span<int> word = stackalloc int[1];
                word[0] = 0x01020304;
                Console.WriteLine(MemoryMarshal.AsBytes(word)[BitConverter.IsLittleEndian ? 0 : 3]);
            }
        }
        """;

    /// <summary>Record structs, tuples and nullables: equality, with, deconstruction, switch, lifted arithmetic.</summary>
    public const string Values = """
        using System;

        readonly record struct Point(short X, short Y)
        {
            public int Manhattan => Math.Abs(X) + Math.Abs(Y);
        }

        static class Program
        {
            static (int Min, int Max) Range(ReadOnlySpan<int> values)
            {
                int min = int.MaxValue, max = int.MinValue;
                foreach (var value in values)
                {
                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                }

                return (min, max);
            }

            static int? Half(int value) => value % 2 == 0 ? value / 2 : null;

            static string Name((bool, int) state) => state switch
            {
                (true, > 10) => "hot",
                (true, _) => "warm",
                _ => "cold",
            };

            static void Main()
            {
                var a = new Point(3, -4);
                var b = a with { Y = 4 };
                Console.WriteLine(a == b);
                Console.WriteLine(a.Equals(new Point(3, -4)));
                Console.WriteLine(a.GetHashCode() == new Point(3, -4).GetHashCode());
                var (x, y) = b;
                Console.WriteLine(x * 10 + y + b.Manhattan);
                var (min, max) = Range(stackalloc int[] { 4, -9, 12, 0 });
                Console.WriteLine(min + " " + max);
                var t = (1, 2);
                (t.Item1, t.Item2) = (t.Item2, t.Item1);
                Console.WriteLine(t == (2, 1));
                Console.WriteLine(Name((true, 20)) + " " + Name((true, 1)) + " " + Name((false, 50)));
                int? h = Half(10), none = Half(3);
                Console.WriteLine(h.HasValue + " " + none.HasValue + " " + (h ?? -1) + " " + (none ?? -1));
                Console.WriteLine((h + 1).GetValueOrDefault() + (none + 1).GetValueOrDefault());
                Console.WriteLine(h > 4);
                if (Half(8) is int four)
                {
                    Console.WriteLine(four);
                }
            }
        }
        """;

    /// <summary>
    /// Printed text: enums by name ([Flags] combined), whole numbers with D, X and x formats and alignments, chars, bools
    /// and a struct's ToString in holes, fixed text compared and measured.
    /// </summary>
    public const string Text = """
        using System;

        enum Light : byte { Red, Yellow, Green }

        [Flags]
        enum Access { None = 0, Read = 1, Write = 2, Run = 4 }

        struct Celsius
        {
            public short Tenths;
            public override string ToString() => "C";
        }

        static class Program
        {
            static string Word(int i) => i switch { 0 => "zero", 1 => "one", _ => "many" };

            static void Main()
            {
                var light = Light.Yellow;
                Console.WriteLine(light);
                Console.WriteLine($"next: {light + 1}, then {Light.Red}");
                Console.WriteLine(Access.Read | Access.Run);
                Console.WriteLine($"{Access.None} {(Access)8}");
                Console.WriteLine($"[{42,5}] [{-42,-5}] [{7:D3}] [{255:X4}] [{48879:x}]");
                Console.WriteLine($"{'x'}{true}{(byte)200}{(sbyte)-1}{ushort.MaxValue}");
                Console.WriteLine($"{new Celsius { Tenths = 215 }} and {uint.MaxValue} and {long.MinValue}");
                Console.WriteLine("road: " + Word(1) + " " + Word(2));
                Console.Write('A');
                Console.Write(1);
                Console.WriteLine();
            }
        }
        """;

    /// <summary>
    /// Static tables: constant ones in flash, one of record structs its initializer computes, a span of constant data,
    /// and a buffer the program fills.
    /// </summary>
    public const string Tables = """
        using System;

        readonly record struct Step(byte Light, byte Seconds);

        static class Program
        {
            static readonly byte[] Digits = { 0x3F, 0x06, 0x5B, 0x4F, 0x66 };
            static readonly short[] Offsets = { -100, 0, 100, 32767 };
            static readonly Step[] Steps = [new(1, 4), new(2, 1), new(3, Seconds(5))];
            static readonly int[] Seen = new int[4];
            static ReadOnlySpan<ushort> Ramp => new ushort[] { 0, 1000, 20000, 65535 };

            static byte Seconds(int n) => (byte)(n * 2);

            static void Main()
            {
                int total = 0;
                foreach (var digit in Digits)
                {
                    total += digit;
                }

                Console.WriteLine(total);
                Console.WriteLine(Offsets[0] + Offsets[3]);
                foreach (var step in Steps)
                {
                    Console.WriteLine(step.Light * 100 + step.Seconds);
                }

                for (int i = 0; i < Seen.Length; i++)
                {
                    Seen[i] = Ramp[i] / 10;
                }

                Console.WriteLine(Seen[3] + Seen[2]);
                Console.WriteLine(Array.IndexOf(Digits, (byte)0x5B));
            }
        }
        """;

    /// <summary>Local functions that capture locals (kept in a struct), static ones, and one that changes what it captures.</summary>
    public const string LocalFunctions = """
        using System;

        static class Program
        {
            static int Run(int start)
            {
                int count = start;
                int step = 3;
                void Advance() => count += step;
                int Twice() => count * 2;
                static int Square(int n) => n * n;

                Advance();
                Advance();
                step = 10;
                Advance();
                return Twice() + Square(start);
            }

            static void Main()
            {
                Console.WriteLine(Run(1));
                Console.WriteLine(Run(-4));
            }
        }
        """;

    /// <summary>Floats without a double: arithmetic, comparisons, conversions both ways (saturating), printing.</summary>
    public const string Floats = """
        using System;

        static class Program
        {
            static float Mean(ReadOnlySpan<float> values)
            {
                float sum = 0;
                foreach (var value in values)
                {
                    sum += value;
                }

                return sum / values.Length;
            }

            static void Main()
            {
                float a = 1.5f, b = -0.25f, big = 3e9f, minusOne = -1f, over = 300.5f;
                Console.WriteLine(a + b);
                Console.WriteLine(a * b);
                Console.WriteLine(a / b);
                Console.WriteLine(Mean(stackalloc float[] { 1, 2, 3.5f }));
                Console.WriteLine(a > b);
                Console.WriteLine((int)(b - 2.5f) + " " + (int)big + " " + (uint)minusOne + " " + (byte)over);
                Console.WriteLine((float)int.MaxValue);
                Console.WriteLine(0.1f + 0.2f);
                Console.WriteLine(1f / 3);
                Console.WriteLine(float.MaxValue);
                Console.WriteLine(-float.Epsilon);
            }
        }
        """;

    /// <summary>MathF's functions, the runtime's on a chip: Sqrt, Abs, Min, Max, Floor, Round, Sin, and % (fmodf).</summary>
    public const string MathFunctions = """
        using System;

        static class Program
        {
            static void Main()
            {
                Console.WriteLine(MathF.Sqrt(2f));
                Console.WriteLine(1.5f % 0.4f);
                Console.WriteLine(MathF.Abs(-3.5f) + MathF.Max(1f, 2f) + MathF.Min(-1f, 4f));
                Console.WriteLine(MathF.Floor(-2.5f) + " " + MathF.Round(2.5f) + " " + MathF.Round(3.5f));
                Console.WriteLine(MathF.Round(MathF.Sin(1f), 4));
                Console.WriteLine(Math.Clamp(15, 0, 10) + Math.Abs(-7) + Math.Sign(-3));
            }
        }
        """;

    /// <summary>Whole numbers of 64 bits: arithmetic, shifts, and &amp;, | and ^ of values known as the program is compiled.</summary>
    public const string Longs = """
        using System;

        static class Program
        {
            static long Mix(long a, long b) => (a ^ b) + (a & b) * 3 - (a | b);

            static void Main()
            {
                long big = -1099511627776;
                Console.WriteLine(big | 255);
                Console.WriteLine(big & 0xFFFF0000FFFF);
                Console.WriteLine(big ^ -1);
                Console.WriteLine(big >> 20);
                Console.WriteLine((ulong)big >> 20);
                Console.WriteLine(1L << 40);
                ulong u = 0xFEDCBA9876543210;
                Console.WriteLine(u / 1000 + " " + u % 1000);
                Console.WriteLine(Mix(123456789012, -98765));
                long most = long.MaxValue;
                Console.WriteLine(most * 3);
                Console.WriteLine(Math.BigMul(0x7FFFFFFF, 0x7FFFFFFF));
            }
        }
        """;

    /// <summary>
    /// Dates and times without a double, small enough for the STM8S103's 8 KB: a date and a TimeSpan made, added and
    /// subtracted, their parts, a comparison.
    /// </summary>
    public const string Dates = """
        using System;

        static class Program
        {
            static void Main()
            {
                var start = new DateTime(2024, 2, 28, 22, 30, 0);
                var later = start + new TimeSpan(1, 3, 15, 30);
                Console.WriteLine(later.Year * 10000 + later.Month * 100 + later.Day);
                Console.WriteLine(later.Hour * 100 + later.Minute);
                var span = later - start;
                Console.WriteLine(span.Days * 1000000 + span.Hours * 10000 + span.Minutes * 100 + span.Seconds);
                Console.WriteLine(later > start);
            }
        }
        """;

    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Tiny), Tiny),
        (nameof(Generics), Generics),
        (nameof(Spans), Spans),
        (nameof(ByteOrder), ByteOrder),
        (nameof(Values), Values),
        (nameof(Text), Text),
        (nameof(Tables), Tables),
        (nameof(LocalFunctions), LocalFunctions),
        (nameof(Floats), Floats),
        (nameof(MathFunctions), MathFunctions),
        (nameof(Longs), Longs),
        (nameof(Dates), Dates),
    ];
}
