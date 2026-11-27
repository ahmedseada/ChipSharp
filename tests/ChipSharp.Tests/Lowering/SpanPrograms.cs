namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Programs with spans (Span&lt;T&gt;, ReadOnlySpan&lt;T&gt;, stackalloc, constant data, MemoryExtensions,
/// BinaryPrimitives, MemoryMarshal), which the PC tests (<see cref="SpanProgramTests"/>) and the Pico tests run and
/// compare with .NET.
/// </summary>
internal static class SpanPrograms
{
    public const string Views = """
        using System;

        class Program
        {
            static ReadOnlySpan<byte> Digits => new byte[] { 0x3F, 0x06, 0x5B, 0x4F, 0x66 };
            static ReadOnlySpan<int> Steps => new int[] { 10, -20, 30, -40 };
            static ReadOnlySpan<short> Shorts => [7, -8, 9];
            static ReadOnlySpan<long> Longs => new long[] { long.MinValue, 2, long.MaxValue };
            static ReadOnlySpan<double> Halves => new double[] { 0.5, -1.5, 2.25 };
            static ReadOnlySpan<bool> Flags => new bool[] { true, false, true };
            static readonly int[] Table = { 2, 3, 5, 7, 11 };

            static int Sum(ReadOnlySpan<int> values)
            {
                int total = 0;
                foreach (int value in values)
                {
                    total += value;
                }

                return total;
            }

            static T Total<T>(ReadOnlySpan<T> values) where T : System.Numerics.INumber<T>
            {
                T total = T.Zero;
                for (int i = 0; i < values.Length; i++)
                {
                    total += values[i];
                }

                return total;
            }

            static Span<int> Tail(Span<int> values) => values.Slice(1);

            static ReadOnlySpan<int> Middle(int[] values) => values.AsSpan(1, values.Length - 2);

            static void Double(Span<int> values)
            {
                foreach (ref int value in values)
                {
                    value *= 2;
                }
            }

            static void Main()
            {
                int[] array = { 1, 2, 3, 4, 5, 6 };
                Console.WriteLine(Sum(array));
                Console.WriteLine(Sum(array.AsSpan(2)));
                Console.WriteLine(Sum(array.AsSpan(1, 3)));
                Console.WriteLine(Sum(new Span<int>(array, 4, 2)));
                Console.WriteLine(Sum(Middle(array)));

                Span<int> span = array;
                Console.WriteLine(span[0] + span[^1]);
                Console.WriteLine(Sum(span[1..4]) + Sum(span[..2]) + Sum(span[3..]));
                Console.WriteLine(Tail(Tail(span)).Length);
                Double(span.Slice(2, 2));
                Console.WriteLine(Sum(span));
                ref int third = ref span[2];
                third = 100;
                Console.WriteLine(array[2]);

                Span<int> buffer = stackalloc int[4];
                buffer[1] = 7;
                buffer[3] = -1;
                Console.WriteLine(Sum(buffer));
                Span<byte> bytes = stackalloc byte[] { 9, 8, 7 };
                Span<long> wide = stackalloc long[] { 1, long.MaxValue };
                Console.WriteLine(bytes[0] + bytes[2]);
                Console.WriteLine(wide[1]);

                Console.WriteLine(Digits[1] + Digits.Length);
                Console.WriteLine(Sum(Steps));
                Console.WriteLine(Shorts[1] + Shorts[2]);
                Console.WriteLine(Longs[0] + Longs[2]);
                Console.WriteLine(Total(Halves));
                Console.WriteLine(Flags[0] && !Flags[1]);
                Console.WriteLine(Sum(Table) + Total<int>(Table));
                Console.WriteLine(Total<int>(buffer));

                Span<int> none = default;
                ReadOnlySpan<int> empty = Span<int>.Empty;
                Console.WriteLine(none.IsEmpty && empty.IsEmpty && none.Length == 0);
                Console.WriteLine(span == array.AsSpan());
                Console.WriteLine(span.Slice(1) != array.AsSpan());
            }
        }
        """;

    public const string Copies = """
        using System;

        struct Point : IEquatable<Point>
        {
            public int X;
            public int Y;

            public Point(int x, int y)
            {
                X = x;
                Y = y;
            }

            public bool Equals(Point other) => X == other.X && Y == other.Y;
        }

        ref struct Reader
        {
            private ReadOnlySpan<byte> _data;
            private int _position;

            public Reader(ReadOnlySpan<byte> data)
            {
                _data = data;
                _position = 0;
            }

            public bool More => _position < _data.Length;

            public byte Next() => _data[_position++];

            public ReadOnlySpan<byte> Rest => _data.Slice(_position);
        }

        class Program
        {
            static void Print(ReadOnlySpan<int> values)
            {
                foreach (int value in values)
                {
                    Console.Write(value);
                    Console.Write(" ");
                }

                Console.WriteLine();
            }

            static void Main()
            {
                int[] values = { 5, 3, 9, 3, 1 };
                Span<int> span = values;
                Console.WriteLine(span.IndexOf(3));
                Console.WriteLine(span.LastIndexOf(3));
                Console.WriteLine(span.IndexOf(4));
                Console.WriteLine(span.Contains(9));
                Console.WriteLine(span.IndexOf(new int[] { 9, 3 }));
                Console.WriteLine(span.LastIndexOf(new int[] { 3 }));
                Console.WriteLine(span.IndexOf(ReadOnlySpan<int>.Empty));
                Console.WriteLine(span.LastIndexOf(ReadOnlySpan<int>.Empty));
                Console.WriteLine(span.StartsWith(new int[] { 5, 3 }));
                Console.WriteLine(span.EndsWith(new int[] { 3, 1 }));
                Console.WriteLine(span.SequenceEqual(new int[] { 5, 3, 9, 3, 1 }));
                Console.WriteLine(span.SequenceEqual(new int[] { 5, 3, 9 }));

                Span<int> copy = stackalloc int[6];
                span.CopyTo(copy);
                Print(copy);
                span.Slice(0, 3).CopyTo(span.Slice(2));
                Print(span);
                span.Slice(2).CopyTo(span);
                Print(span);
                Console.WriteLine(copy.TryCopyTo(span));
                Console.WriteLine(span.TryCopyTo(copy));
                span.Reverse();
                Print(span);
                copy.Fill(4);
                copy.Slice(4).Clear();
                Print(copy);

                ReadOnlySpan<double> doubles = new double[] { 1.5, double.NaN, -0.0 };
                Console.WriteLine(doubles.IndexOf(double.NaN));
                Console.WriteLine(doubles.IndexOf(0.0));
                ReadOnlySpan<byte> bytes = new byte[] { 1, 2, 3, 2 };
                Console.WriteLine(bytes.LastIndexOf((byte)2));
                Console.WriteLine(bytes.StartsWith((byte)1) && bytes.EndsWith((byte)2));
                Span<bool> flags = stackalloc bool[3];
                flags.Fill(true);
                flags[1] = false;
                Console.WriteLine(flags.IndexOf(false));

                Span<Point> points = stackalloc Point[3];
                points[1] = new Point(2, 3);
                points[2].X = 7;
                Span<Point> others = stackalloc Point[3];
                points.CopyTo(others);
                others[0].Y = -1;
                Console.WriteLine(points[1].Equals(others[1]) && !points[0].Equals(others[0]));
                Console.WriteLine(others[2].X + others[0].Y);

                var reader = new Reader(new byte[] { 4, 5, 6 });
                int sum = 0;
                while (reader.More)
                {
                    sum = sum * 10 + reader.Next();
                    if (sum > 40)
                    {
                        Console.WriteLine(reader.Rest.Length);
                    }
                }

                Console.WriteLine(sum);
            }
        }
        """;

    public const string Bytes = """
        using System;
        using System.Buffers.Binary;
        using System.Runtime.InteropServices;

        struct Header
        {
            public byte Kind;
            public ushort Length;
            public int Stamp;
        }

        class Program
        {
            static void Main()
            {
                Span<byte> packet = stackalloc byte[24];
                BinaryPrimitives.WriteInt16BigEndian(packet, -2);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(2), 0xABCD);
                BinaryPrimitives.WriteInt32BigEndian(packet.Slice(4), -123456789);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(8), 0xDEADBEEF);
                BinaryPrimitives.WriteInt64BigEndian(packet.Slice(12), long.MinValue + 77);
                for (int i = 0; i < 20; i++)
                {
                    Console.Write(packet[i]);
                    Console.Write(i % 10 == 9 ? "\n" : ",");
                }

                Console.WriteLine(BinaryPrimitives.ReadInt16BigEndian(packet));
                Console.WriteLine(BinaryPrimitives.ReadInt16LittleEndian(packet));
                Console.WriteLine(BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(2)));
                Console.WriteLine(BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2)));
                Console.WriteLine(BinaryPrimitives.ReadInt32BigEndian(packet.Slice(4)));
                Console.WriteLine(BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8)));
                Console.WriteLine(BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(5)));
                Console.WriteLine(BinaryPrimitives.ReadInt64BigEndian(packet.Slice(12)));
                Console.WriteLine(BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(9)));

                BinaryPrimitives.WriteSingleBigEndian(packet.Slice(1), 1.25f);
                BinaryPrimitives.WriteDoubleLittleEndian(packet.Slice(7), -0.1);
                Console.WriteLine(BinaryPrimitives.ReadSingleBigEndian(packet.Slice(1)));
                Console.WriteLine(BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(1)));
                Console.WriteLine(BinaryPrimitives.ReadDoubleLittleEndian(packet.Slice(7)));
                Console.WriteLine(BinaryPrimitives.ReadDoubleBigEndian(packet.Slice(7)));

                Console.WriteLine(BinaryPrimitives.TryReadInt32LittleEndian(packet.Slice(20), out int last));
                Console.WriteLine(last);
                Console.WriteLine(BinaryPrimitives.TryReadInt32LittleEndian(packet.Slice(21), out int none));
                Console.WriteLine(none);
                Console.WriteLine(BinaryPrimitives.TryWriteUInt64BigEndian(packet.Slice(17), 1));
                Console.WriteLine(BinaryPrimitives.TryWriteUInt64BigEndian(packet.Slice(16), 0x0102030405060708));
                Console.WriteLine(BinaryPrimitives.TryReadDoubleBigEndian(packet.Slice(16), out double bits));
                Console.WriteLine(bits);

                Console.WriteLine(BinaryPrimitives.ReverseEndianness((short)0x1234));
                Console.WriteLine(BinaryPrimitives.ReverseEndianness((ushort)0xFF00));
                Console.WriteLine(BinaryPrimitives.ReverseEndianness(0x12345678));
                Console.WriteLine(BinaryPrimitives.ReverseEndianness(0x80000001u));
                Console.WriteLine(BinaryPrimitives.ReverseEndianness(0x0102030405060708L));
                Console.WriteLine(BinaryPrimitives.ReverseEndianness(ulong.MaxValue - 1));
                Console.WriteLine(BinaryPrimitives.ReverseEndianness((byte)7) + BinaryPrimitives.ReverseEndianness((sbyte)-7));

                // a header at an odd offset: read and written unaligned
                var header = new Header { Kind = 3, Length = 512, Stamp = -9 };
                MemoryMarshal.Write(packet.Slice(3), in header);
                var back = MemoryMarshal.Read<Header>(packet.Slice(3));
                Console.WriteLine(back.Kind + back.Length + back.Stamp);
                Console.WriteLine(MemoryMarshal.Read<int>(packet.Slice(1)) == MemoryMarshal.Read<int>(packet.Slice(1, 4)));
                MemoryMarshal.Write(packet.Slice(5), 0x7FFFFFFF);
                Console.WriteLine(MemoryMarshal.Read<int>(packet.Slice(5)));
                Console.WriteLine(MemoryMarshal.Read<long>(packet.Slice(9)));
                Console.WriteLine(MemoryMarshal.TryRead(packet.Slice(20), out Header tooShort));
                Console.WriteLine(tooShort.Stamp);
                Console.WriteLine(MemoryMarshal.TryWrite(packet.Slice(13), in header));
                Console.WriteLine(MemoryMarshal.Read<bool>(packet.Slice(13)));

                Span<int> words = stackalloc int[] { 1, 256, -1 };
                ReadOnlySpan<byte> raw = MemoryMarshal.AsBytes(words);
                int checksum = 0;
                foreach (byte b in raw)
                {
                    checksum = checksum * 31 + b;
                }

                Console.WriteLine(raw.Length);
                Console.WriteLine(checksum);
                Span<short> halves = MemoryMarshal.Cast<int, short>(words);
                Console.WriteLine(halves.Length);
                halves[1] = 1;
                Console.WriteLine(words[0]);
                Console.WriteLine(MemoryMarshal.Cast<byte, int>(packet.Slice(0, 7)).Length);
                Console.WriteLine(MemoryMarshal.Cast<int, long>(words).Length);

                int counter = 5;
                Span<int> one = MemoryMarshal.CreateSpan(ref counter, 1);
                one[0]++;
                ReadOnlySpan<byte> counterBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref counter, 1));
                Console.WriteLine(counter + counterBytes[0]);
                Console.WriteLine(new Span<int>(ref counter)[0]);
            }
        }
        """;

    /// <summary>A program whose index outside a span stops it, after what it printed before.</summary>
    public const string IndexOutOfRange = """
        using System;

        class Program
        {
            static int At(ReadOnlySpan<int> values, int index) => values[index];

            static void Main()
            {
                Span<int> values = stackalloc int[] { 1, 2, 3 };
                Console.WriteLine(At(values, 2));
                Console.WriteLine(At(values, 3));
                Console.WriteLine("not printed");
            }
        }
        """;

    /// <summary>A program whose slice outside its span stops it.</summary>
    public const string SliceOutOfRange = """
        using System;

        class Program
        {
            static void Main()
            {
                int[] values = { 1, 2, 3 };
                Console.WriteLine(values.AsSpan(1, 2).Length);
                Console.WriteLine(values.AsSpan(2).Slice(1, 1).Length);
                Console.WriteLine("not printed");
            }
        }
        """;

    /// <summary>A program whose CopyTo a shorter span stops it.</summary>
    public const string DestinationTooShort = """
        using System;

        class Program
        {
            static void Main()
            {
                Span<int> source = stackalloc int[] { 1, 2, 3 };
                Span<int> destination = stackalloc int[2];
                source.Slice(1).CopyTo(destination);
                Console.WriteLine(destination[1]);
                source.CopyTo(destination);
                Console.WriteLine("not printed");
            }
        }
        """;

    /// <summary>A program whose read of more bytes than there are stops it.</summary>
    public const string ReadPastTheEnd = """
        using System;
        using System.Buffers.Binary;

        class Program
        {
            static void Main()
            {
                ReadOnlySpan<byte> bytes = new byte[] { 1, 2, 3, 4, 5 };
                Console.WriteLine(BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(1)));
                Console.WriteLine(BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(2)));
                Console.WriteLine("not printed");
            }
        }
        """;

    /// <summary>What each failing program prints before it stops, and what it stops with on standard error.</summary>
    public static IEnumerable<(string Name, string Source, string Printed, string Error)> Failures =>
    [
        ("IndexOutOfRange", IndexOutOfRange, "3\r\n",
            "Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array.\r\n"),
        ("SliceOutOfRange", SliceOutOfRange, "2\r\n",
            "Unhandled exception. System.ArgumentOutOfRangeException: Specified argument was out of the range of valid " +
            "values.\r\n"),
        ("DestinationTooShort", DestinationTooShort, "3\r\n",
            "Unhandled exception. System.ArgumentException: Destination is too short. (Parameter 'destination')\r\n"),
        ("ReadPastTheEnd", ReadPastTheEnd, "33752069\r\n",
            "Unhandled exception. System.ArgumentOutOfRangeException: Specified argument was out of the range of valid " +
            "values. (Parameter 'length')\r\n"),
    ];

    /// <summary>The programs that run to the end, by name.</summary>
    public static IEnumerable<(string Name, string Source)> All =>
        [("Views", Views), ("Copies", Copies), ("Bytes", Bytes)];
}
