using System.Runtime.InteropServices;

namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.BitConverter"/>: the bits of floats and doubles as whole numbers and back (the same on
/// every chip), and the bytes of a value in the chip's own order, as .NET's BitConverter has them on a processor of that
/// order: <see cref="IsLittleEndian"/> says which, true on the PC, the Pico, the Uno and the PFS154, false on the STM8.
/// So a program that reads bytes through <c>BitConverter</c> or <c>MemoryMarshal</c> (which is the chip's order too) and
/// checks <see cref="IsLittleEndian"/>, as portable C# does, gets .NET's results on every chip; the
/// <see cref="BinaryPrimitives"/> of a stated order give them whatever the chip's.
/// </summary>
/// <remarks>
/// <see cref="IsLittleEndian"/> is not a fact the C# is told: it is read from memory once, when the program starts (the
/// first byte of a ushort 1), so the TIR is the same for every target and each compiler knows the answer (LLVM and clang
/// fold it away). The forms of Half, Int128 and UInt128 are not here yet; <c>GetBytes</c> makes an array, so it needs a
/// heap.
/// </remarks>
[Substitute(typeof(System.BitConverter))]
internal static class BitConverter
{
    /// <summary>Whether the chip keeps the low byte of a number first.</summary>
    public static readonly bool IsLittleEndian = LowByteFirst();

    public static int SingleToInt32Bits(float value) =>
        MemoryMarshal.Read<int>(MemoryMarshal.AsBytes(new ReadOnlySpan<float>(ref value)));

    public static uint SingleToUInt32Bits(float value) => (uint)SingleToInt32Bits(value);

    public static float Int32BitsToSingle(int value) =>
        MemoryMarshal.Read<float>(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(ref value)));

    public static float UInt32BitsToSingle(uint value) => Int32BitsToSingle((int)value);

    public static long DoubleToInt64Bits(double value) =>
        MemoryMarshal.Read<long>(MemoryMarshal.AsBytes(new ReadOnlySpan<double>(ref value)));

    public static ulong DoubleToUInt64Bits(double value) => (ulong)DoubleToInt64Bits(value);

    public static double Int64BitsToDouble(long value) =>
        MemoryMarshal.Read<double>(MemoryMarshal.AsBytes(new ReadOnlySpan<long>(ref value)));

    public static double UInt64BitsToDouble(ulong value) => Int64BitsToDouble((long)value);

    public static bool TryWriteBytes(Span<byte> destination, bool value) => MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteBytes(Span<byte> destination, char value) => MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteBytes(Span<byte> destination, short value) => MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteBytes(Span<byte> destination, ushort value) => MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteBytes(Span<byte> destination, int value) => MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteBytes(Span<byte> destination, uint value) => MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteBytes(Span<byte> destination, long value) => MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteBytes(Span<byte> destination, ulong value) => MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteBytes(Span<byte> destination, float value) => MemoryMarshal.TryWrite(destination, in value);

    public static bool TryWriteBytes(Span<byte> destination, double value) => MemoryMarshal.TryWrite(destination, in value);

    public static bool ToBoolean(ReadOnlySpan<byte> value) => Checked(value, 1)[0] != 0;

    public static char ToChar(ReadOnlySpan<byte> value) => MemoryMarshal.Read<char>(Checked(value, 2));

    public static short ToInt16(ReadOnlySpan<byte> value) => MemoryMarshal.Read<short>(Checked(value, 2));

    public static ushort ToUInt16(ReadOnlySpan<byte> value) => MemoryMarshal.Read<ushort>(Checked(value, 2));

    public static int ToInt32(ReadOnlySpan<byte> value) => MemoryMarshal.Read<int>(Checked(value, 4));

    public static uint ToUInt32(ReadOnlySpan<byte> value) => MemoryMarshal.Read<uint>(Checked(value, 4));

    public static long ToInt64(ReadOnlySpan<byte> value) => MemoryMarshal.Read<long>(Checked(value, 8));

    public static ulong ToUInt64(ReadOnlySpan<byte> value) => MemoryMarshal.Read<ulong>(Checked(value, 8));

    public static float ToSingle(ReadOnlySpan<byte> value) => MemoryMarshal.Read<float>(Checked(value, 4));

    public static double ToDouble(ReadOnlySpan<byte> value) => MemoryMarshal.Read<double>(Checked(value, 8));

    public static bool ToBoolean(byte[] value, int startIndex) => Bytes(value, startIndex, 1)[0] != 0;

    public static char ToChar(byte[] value, int startIndex) => MemoryMarshal.Read<char>(Bytes(value, startIndex, 2));

    public static short ToInt16(byte[] value, int startIndex) => MemoryMarshal.Read<short>(Bytes(value, startIndex, 2));

    public static ushort ToUInt16(byte[] value, int startIndex) => MemoryMarshal.Read<ushort>(Bytes(value, startIndex, 2));

    public static int ToInt32(byte[] value, int startIndex) => MemoryMarshal.Read<int>(Bytes(value, startIndex, 4));

    public static uint ToUInt32(byte[] value, int startIndex) => MemoryMarshal.Read<uint>(Bytes(value, startIndex, 4));

    public static long ToInt64(byte[] value, int startIndex) => MemoryMarshal.Read<long>(Bytes(value, startIndex, 8));

    public static ulong ToUInt64(byte[] value, int startIndex) => MemoryMarshal.Read<ulong>(Bytes(value, startIndex, 8));

    public static float ToSingle(byte[] value, int startIndex) => MemoryMarshal.Read<float>(Bytes(value, startIndex, 4));

    public static double ToDouble(byte[] value, int startIndex) => MemoryMarshal.Read<double>(Bytes(value, startIndex, 8));

    public static byte[] GetBytes(bool value) => [value ? (byte)1 : (byte)0];

    public static byte[] GetBytes(char value) => Copy(MemoryMarshal.AsBytes(new ReadOnlySpan<char>(ref value)));

    public static byte[] GetBytes(short value) => Copy(MemoryMarshal.AsBytes(new ReadOnlySpan<short>(ref value)));

    public static byte[] GetBytes(ushort value) => Copy(MemoryMarshal.AsBytes(new ReadOnlySpan<ushort>(ref value)));

    public static byte[] GetBytes(int value) => Copy(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(ref value)));

    public static byte[] GetBytes(uint value) => Copy(MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(ref value)));

    public static byte[] GetBytes(long value) => Copy(MemoryMarshal.AsBytes(new ReadOnlySpan<long>(ref value)));

    public static byte[] GetBytes(ulong value) => Copy(MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>(ref value)));

    public static byte[] GetBytes(float value) => Copy(MemoryMarshal.AsBytes(new ReadOnlySpan<float>(ref value)));

    public static byte[] GetBytes(double value) => Copy(MemoryMarshal.AsBytes(new ReadOnlySpan<double>(ref value)));

    /// <summary>The first byte of a ushort 1 is 1 where the low byte comes first.</summary>
    private static bool LowByteFirst()
    {
        ushort one = 1;
        return MemoryMarshal.AsBytes(new ReadOnlySpan<ushort>(ref one))[0] == 1;
    }

    /// <summary>
    /// The <paramref name="count"/> bytes of <paramref name="value"/> from <paramref name="startIndex"/> on, checked as
    /// .NET checks them: ArgumentNullException, then ArgumentOutOfRangeException of startIndex, then ArgumentException of
    /// value, with .NET's messages.
    /// </summary>
    private static ReadOnlySpan<byte> Bytes(byte[] value, int startIndex, int count)
    {
        if (value is null)
        {
            Runtime.ArgumentNullOf("value");
        }

        if ((uint)startIndex >= (uint)value!.Length)
        {
            Runtime.ArgumentOutOfRangeOf("startIndex",
                "Index was out of range. Must be non-negative and less than the size of the collection.");
        }

        if (startIndex > value.Length - count)
        {
            Runtime.ArgumentInvalid("Destination array is not long enough to copy all the items in the collection. " +
                "Check array index and length.", "value");
        }

        return new ReadOnlySpan<byte>(value, startIndex, count);
    }

    /// <summary>The bytes, when there are <paramref name="count"/> at least; else .NET's ArgumentOutOfRangeException of value.</summary>
    private static ReadOnlySpan<byte> Checked(ReadOnlySpan<byte> value, int count)
    {
        if (value.Length < count)
        {
            Runtime.ArgumentOutOfRangeOf("value", "Specified argument was out of the range of valid values.");
        }

        return value;
    }

    private static byte[] Copy(ReadOnlySpan<byte> bytes)
    {
        var copy = new byte[bytes.Length];
        bytes.CopyTo(copy);
        return copy;
    }
}
