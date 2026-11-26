using System.Runtime.InteropServices;

namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.Buffers.Binary.BinaryPrimitives"/>: whole numbers, floats and doubles read from and
/// written to bytes, big-endian (network order, most significant byte first) or little-endian, at any offset, and the
/// bytes of a number reversed. Written as .NET 10 writes them, over <see cref="MemoryMarshal"/>'s Read and Write, which
/// read and write unaligned and stop the program with .NET's ArgumentOutOfRangeException (parameter 'length') on too
/// few bytes; the Try forms say false instead.
/// </summary>
/// <remarks>
/// Every target ChipSharp has is little-endian (their target files say so), so a little-endian read is the value as it
/// is, and a big-endian one has its bytes reversed. The forms of Half, Int128 and UInt128, and ReverseEndianness of a
/// whole span, are not here yet.
/// </remarks>
[Substitute(typeof(System.Buffers.Binary.BinaryPrimitives))]
internal static class BinaryPrimitives
{
    public static sbyte ReverseEndianness(sbyte value) => value;

    public static byte ReverseEndianness(byte value) => value;

    public static short ReverseEndianness(short value) => (short)ReverseEndianness((ushort)value);

    public static ushort ReverseEndianness(ushort value) => (ushort)((value >> 8) + (value << 8));

    public static int ReverseEndianness(int value) => (int)ReverseEndianness((uint)value);

    public static uint ReverseEndianness(uint value) =>
        (value >> 24) | ((value >> 8) & 0x0000FF00u) | ((value << 8) & 0x00FF0000u) | (value << 24);

    public static long ReverseEndianness(long value) => (long)ReverseEndianness((ulong)value);

    public static ulong ReverseEndianness(ulong value) =>
        ((ulong)ReverseEndianness((uint)value) << 32) + ReverseEndianness((uint)(value >> 32));

    /// <summary>The bytes of an nint reversed, as many as the target's pointer has.</summary>
    public static nint ReverseEndianness(nint value)
    {
        MemoryExtensions.Reverse(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));
        return value;
    }

    public static nuint ReverseEndianness(nuint value)
    {
        MemoryExtensions.Reverse(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));
        return value;
    }

    public static short ReadInt16BigEndian(ReadOnlySpan<byte> source) => ReverseEndianness(MemoryMarshal.Read<short>(source));

    public static short ReadInt16LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<short>(source);

    public static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> source) => ReverseEndianness(MemoryMarshal.Read<ushort>(source));

    public static ushort ReadUInt16LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<ushort>(source);

    public static int ReadInt32BigEndian(ReadOnlySpan<byte> source) => ReverseEndianness(MemoryMarshal.Read<int>(source));

    public static int ReadInt32LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<int>(source);

    public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> source) => ReverseEndianness(MemoryMarshal.Read<uint>(source));

    public static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<uint>(source);

    public static long ReadInt64BigEndian(ReadOnlySpan<byte> source) => ReverseEndianness(MemoryMarshal.Read<long>(source));

    public static long ReadInt64LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<long>(source);

    public static ulong ReadUInt64BigEndian(ReadOnlySpan<byte> source) => ReverseEndianness(MemoryMarshal.Read<ulong>(source));

    public static ulong ReadUInt64LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<ulong>(source);

    public static nint ReadIntPtrBigEndian(ReadOnlySpan<byte> source) => ReverseEndianness(MemoryMarshal.Read<nint>(source));

    public static nint ReadIntPtrLittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<nint>(source);

    public static nuint ReadUIntPtrBigEndian(ReadOnlySpan<byte> source) => ReverseEndianness(MemoryMarshal.Read<nuint>(source));

    public static nuint ReadUIntPtrLittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<nuint>(source);

    public static float ReadSingleBigEndian(ReadOnlySpan<byte> source) =>
        Single(ReverseEndianness(MemoryMarshal.Read<int>(source)));

    public static float ReadSingleLittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<float>(source);

    public static double ReadDoubleBigEndian(ReadOnlySpan<byte> source) =>
        Double(ReverseEndianness(MemoryMarshal.Read<long>(source)));

    public static double ReadDoubleLittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<double>(source);

    public static bool TryReadInt16BigEndian(ReadOnlySpan<byte> source, out short value)
    {
        var success = MemoryMarshal.TryRead(source, out short read);
        value = ReverseEndianness(read);
        return success;
    }

    public static bool TryReadInt16LittleEndian(ReadOnlySpan<byte> source, out short value) =>
        MemoryMarshal.TryRead(source, out value);

    public static bool TryReadUInt16BigEndian(ReadOnlySpan<byte> source, out ushort value)
    {
        var success = MemoryMarshal.TryRead(source, out ushort read);
        value = ReverseEndianness(read);
        return success;
    }

    public static bool TryReadUInt16LittleEndian(ReadOnlySpan<byte> source, out ushort value) =>
        MemoryMarshal.TryRead(source, out value);

    public static bool TryReadInt32BigEndian(ReadOnlySpan<byte> source, out int value)
    {
        var success = MemoryMarshal.TryRead(source, out int read);
        value = ReverseEndianness(read);
        return success;
    }

    public static bool TryReadInt32LittleEndian(ReadOnlySpan<byte> source, out int value) =>
        MemoryMarshal.TryRead(source, out value);

    public static bool TryReadUInt32BigEndian(ReadOnlySpan<byte> source, out uint value)
    {
        var success = MemoryMarshal.TryRead(source, out uint read);
        value = ReverseEndianness(read);
        return success;
    }

    public static bool TryReadUInt32LittleEndian(ReadOnlySpan<byte> source, out uint value) =>
        MemoryMarshal.TryRead(source, out value);

    public static bool TryReadInt64BigEndian(ReadOnlySpan<byte> source, out long value)
    {
        var success = MemoryMarshal.TryRead(source, out long read);
        value = ReverseEndianness(read);
        return success;
    }

    public static bool TryReadInt64LittleEndian(ReadOnlySpan<byte> source, out long value) =>
        MemoryMarshal.TryRead(source, out value);

    public static bool TryReadUInt64BigEndian(ReadOnlySpan<byte> source, out ulong value)
    {
        var success = MemoryMarshal.TryRead(source, out ulong read);
        value = ReverseEndianness(read);
        return success;
    }

    public static bool TryReadUInt64LittleEndian(ReadOnlySpan<byte> source, out ulong value) =>
        MemoryMarshal.TryRead(source, out value);

    public static bool TryReadSingleBigEndian(ReadOnlySpan<byte> source, out float value)
    {
        var success = MemoryMarshal.TryRead(source, out int read);
        value = Single(ReverseEndianness(read));
        return success;
    }

    public static bool TryReadSingleLittleEndian(ReadOnlySpan<byte> source, out float value) =>
        MemoryMarshal.TryRead(source, out value);

    public static bool TryReadDoubleBigEndian(ReadOnlySpan<byte> source, out double value)
    {
        var success = MemoryMarshal.TryRead(source, out long read);
        value = Double(ReverseEndianness(read));
        return success;
    }

    public static bool TryReadDoubleLittleEndian(ReadOnlySpan<byte> source, out double value) =>
        MemoryMarshal.TryRead(source, out value);

    public static void WriteInt16BigEndian(Span<byte> destination, short value)
    {
        var reversed = ReverseEndianness(value);
        MemoryMarshal.Write(destination, in reversed);
    }

    public static void WriteInt16LittleEndian(Span<byte> destination, short value) => MemoryMarshal.Write(destination, in value);

    public static void WriteUInt16BigEndian(Span<byte> destination, ushort value)
    {
        var reversed = ReverseEndianness(value);
        MemoryMarshal.Write(destination, in reversed);
    }

    public static void WriteUInt16LittleEndian(Span<byte> destination, ushort value) => MemoryMarshal.Write(destination, in value);

    public static void WriteInt32BigEndian(Span<byte> destination, int value)
    {
        var reversed = ReverseEndianness(value);
        MemoryMarshal.Write(destination, in reversed);
    }

    public static void WriteInt32LittleEndian(Span<byte> destination, int value) => MemoryMarshal.Write(destination, in value);

    public static void WriteUInt32BigEndian(Span<byte> destination, uint value)
    {
        var reversed = ReverseEndianness(value);
        MemoryMarshal.Write(destination, in reversed);
    }

    public static void WriteUInt32LittleEndian(Span<byte> destination, uint value) => MemoryMarshal.Write(destination, in value);

    public static void WriteInt64BigEndian(Span<byte> destination, long value)
    {
        var reversed = ReverseEndianness(value);
        MemoryMarshal.Write(destination, in reversed);
    }

    public static void WriteInt64LittleEndian(Span<byte> destination, long value) => MemoryMarshal.Write(destination, in value);

    public static void WriteUInt64BigEndian(Span<byte> destination, ulong value)
    {
        var reversed = ReverseEndianness(value);
        MemoryMarshal.Write(destination, in reversed);
    }

    public static void WriteUInt64LittleEndian(Span<byte> destination, ulong value) => MemoryMarshal.Write(destination, in value);

    public static void WriteIntPtrBigEndian(Span<byte> destination, nint value)
    {
        var reversed = ReverseEndianness(value);
        MemoryMarshal.Write(destination, in reversed);
    }

    public static void WriteIntPtrLittleEndian(Span<byte> destination, nint value) => MemoryMarshal.Write(destination, in value);

    public static void WriteUIntPtrBigEndian(Span<byte> destination, nuint value)
    {
        var reversed = ReverseEndianness(value);
        MemoryMarshal.Write(destination, in reversed);
    }

    public static void WriteUIntPtrLittleEndian(Span<byte> destination, nuint value) => MemoryMarshal.Write(destination, in value);

    public static void WriteSingleBigEndian(Span<byte> destination, float value)
    {
        var reversed = ReverseEndianness(Bits(value));
        MemoryMarshal.Write(destination, in reversed);
    }

    public static void WriteSingleLittleEndian(Span<byte> destination, float value) => MemoryMarshal.Write(destination, in value);

    public static void WriteDoubleBigEndian(Span<byte> destination, double value)
    {
        var reversed = ReverseEndianness(Bits(value));
        MemoryMarshal.Write(destination, in reversed);
    }

    public static void WriteDoubleLittleEndian(Span<byte> destination, double value) => MemoryMarshal.Write(destination, in value);

    public static bool TryWriteInt16BigEndian(Span<byte> destination, short value)
    {
        var reversed = ReverseEndianness(value);
        return MemoryMarshal.TryWrite(destination, in reversed);
    }

    public static bool TryWriteInt16LittleEndian(Span<byte> destination, short value) =>
        MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteUInt16BigEndian(Span<byte> destination, ushort value)
    {
        var reversed = ReverseEndianness(value);
        return MemoryMarshal.TryWrite(destination, in reversed);
    }

    public static bool TryWriteUInt16LittleEndian(Span<byte> destination, ushort value) =>
        MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteInt32BigEndian(Span<byte> destination, int value)
    {
        var reversed = ReverseEndianness(value);
        return MemoryMarshal.TryWrite(destination, in reversed);
    }

    public static bool TryWriteInt32LittleEndian(Span<byte> destination, int value) =>
        MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteUInt32BigEndian(Span<byte> destination, uint value)
    {
        var reversed = ReverseEndianness(value);
        return MemoryMarshal.TryWrite(destination, in reversed);
    }

    public static bool TryWriteUInt32LittleEndian(Span<byte> destination, uint value) =>
        MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteInt64BigEndian(Span<byte> destination, long value)
    {
        var reversed = ReverseEndianness(value);
        return MemoryMarshal.TryWrite(destination, in reversed);
    }

    public static bool TryWriteInt64LittleEndian(Span<byte> destination, long value) =>
        MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteUInt64BigEndian(Span<byte> destination, ulong value)
    {
        var reversed = ReverseEndianness(value);
        return MemoryMarshal.TryWrite(destination, in reversed);
    }

    public static bool TryWriteUInt64LittleEndian(Span<byte> destination, ulong value) =>
        MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteSingleBigEndian(Span<byte> destination, float value)
    {
        var reversed = ReverseEndianness(Bits(value));
        return MemoryMarshal.TryWrite(destination, in reversed);
    }

    public static bool TryWriteSingleLittleEndian(Span<byte> destination, float value) =>
        MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteDoubleBigEndian(Span<byte> destination, double value)
    {
        var reversed = ReverseEndianness(Bits(value));
        return MemoryMarshal.TryWrite(destination, in reversed);
    }

    public static bool TryWriteDoubleLittleEndian(Span<byte> destination, double value) =>
        MemoryMarshal.TryWrite(destination, in value);

    /// <summary>The float whose bits <paramref name="bits"/> are, as BitConverter.Int32BitsToSingle gives it.</summary>
    private static float Single(int bits) => MemoryMarshal.Read<float>(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(ref bits)));

    /// <summary>The double whose bits <paramref name="bits"/> are, as BitConverter.Int64BitsToDouble gives it.</summary>
    private static double Double(long bits) => MemoryMarshal.Read<double>(MemoryMarshal.AsBytes(new ReadOnlySpan<long>(ref bits)));

    /// <summary>The bits of a float, as BitConverter.SingleToInt32Bits gives them.</summary>
    private static int Bits(float value) => MemoryMarshal.Read<int>(MemoryMarshal.AsBytes(new ReadOnlySpan<float>(ref value)));

    /// <summary>The bits of a double, as BitConverter.DoubleToInt64Bits gives them.</summary>
    private static long Bits(double value) => MemoryMarshal.Read<long>(MemoryMarshal.AsBytes(new ReadOnlySpan<double>(ref value)));
}
