namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's C# for <see cref="ChipSharp.Hardware.Spi"/>: each span handed to the chip's runtime
/// (<see cref="ChipRuntime"/>) as a reference to its first byte and its length (an empty one as a reference to a local
/// and 0).
/// </summary>
[Substitute(typeof(ChipSharp.Hardware.Spi))]
internal static class Spi
{
    public static void Open(int bus, int sck, int tx, int rx, int frequency, int mode) =>
        ChipRuntime.SpiOpen(bus, sck, tx, rx, frequency, mode);

    public static void Write(int bus, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            byte none = 0;
            ChipRuntime.SpiWrite(bus, in none, 0);
        }
        else
        {
            ChipRuntime.SpiWrite(bus, in data[0], data.Length);
        }
    }

    public static void Read(int bus, Span<byte> data)
    {
        if (data.IsEmpty)
        {
            byte none = 0;
            ChipRuntime.SpiRead(bus, ref none, 0);
        }
        else
        {
            ChipRuntime.SpiRead(bus, ref data[0], data.Length);
        }
    }

    public static void Transfer(int bus, ReadOnlySpan<byte> write, Span<byte> read)
    {
        byte none = 0;
        if (write.IsEmpty && read.IsEmpty)
        {
            ChipRuntime.SpiTransfer(bus, in none, 0, ref none, 0);
        }
        else if (write.IsEmpty)
        {
            ChipRuntime.SpiTransfer(bus, in none, 0, ref read[0], read.Length);
        }
        else if (read.IsEmpty)
        {
            ChipRuntime.SpiTransfer(bus, in write[0], write.Length, ref none, 0);
        }
        else
        {
            ChipRuntime.SpiTransfer(bus, in write[0], write.Length, ref read[0], read.Length);
        }
    }
}
