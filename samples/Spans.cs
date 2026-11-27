using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

// A reading as the sensor lays it out in its packet: little-endian, 8 bytes, each field at a multiple of its size
struct Reading
{
    public ushort Raw;
    public short Temperature; // tenths of a degree
    public int Time;          // milliseconds since the sensor started
}

class Program
{
    // a packet as the sensor sends it (a constant table, in flash on a chip): a marker, a big-endian sequence number,
    // the reading at offset 3 (not a multiple of 4, so read unaligned), a big-endian count of readings and a checksum
    static ReadOnlySpan<byte> Packet => new byte[]
    {
        0xA5, 0x01, 0x02, 0x34, 0x12, 0xEB, 0x00, 0x10, 0x27, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0xCF,
    };

    static ReadOnlySpan<int> Weights => new int[] { 3, 5, 7, 11 };

    static int Sum(ReadOnlySpan<int> values)
    {
        int total = 0;
        foreach (int value in values)
        {
            total += value;
        }

        return total;
    }

    static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        byte sum = 0;
        foreach (byte value in bytes)
        {
            sum = (byte)(sum * 31 + value);
        }

        return sum;
    }

    static void Main()
    {
        // one Sum for every kind of memory: an array, a part of it, the stack and flash
        int[] samples = { 12, -4, 30, 8, 15 };
        Span<int> weighted = stackalloc int[4];
        for (int i = 0; i < weighted.Length; i++)
        {
            weighted[i] = samples[i] * Weights[i];
        }

        Console.WriteLine($"array {Sum(samples)}, slice {Sum(samples.AsSpan(1, 3))}, last two {Sum(samples.AsSpan()[^2..])}");
        Console.WriteLine($"stackalloc {Sum(weighted)}, table {Sum(Weights)}");

        // the packet: its fields big-endian, the reading as a struct from offset 3
        ReadOnlySpan<byte> packet = Packet;
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(1));
        Reading reading = MemoryMarshal.Read<Reading>(packet.Slice(3));
        int count = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(11));
        bool intact = Checksum(packet[..^1]) == packet[^1];
        Console.WriteLine($"packet {sequence}: raw 0x{reading.Raw:X4}, {reading.Temperature / 10}.{reading.Temperature % 10} " +
            $"degrees at {reading.Time} ms, {count} readings, checksum {(intact ? "ok" : "bad")}");

        // the struct's own bytes are the packet's: a checksum over it with AsBytes
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref reading, 1));
        Console.WriteLine($"reading: {bytes.Length} bytes, checksum {Checksum(bytes)}, same as the packet's: " +
            $"{bytes.SequenceEqual(packet.Slice(3, 8))}");

        // a reply, written into a buffer on the stack, the reading at an odd offset again
        Span<byte> reply = stackalloc byte[12];
        reply[0] = 0x5A;
        BinaryPrimitives.WriteUInt16BigEndian(reply.Slice(1), (ushort)(sequence + 1));
        reading.Time += 250;
        MemoryMarshal.Write(reply.Slice(3), in reading);
        reply[^1] = Checksum(reply[..^1]);
        foreach (byte value in reply)
        {
            Console.Write($"{value:X2} ");
        }

        Console.WriteLine();
        Console.WriteLine($"time in the reply: {BinaryPrimitives.ReadInt32LittleEndian(reply.Slice(7))} ms");
    }
}
