namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's C# for <see cref="ChipSharp.Hardware.Uart"/>: the spans byte by byte, through the runtime's functions
/// (on the RP2040, rp2040_uart.c), which check the port and its pins.
/// </summary>
[Substitute(typeof(ChipSharp.Hardware.Uart))]
internal static class Uart
{
    public static void Open(int port, int tx, int rx, int baud) => ChipRuntime.UartOpen(port, tx, rx, baud);

    public static void Write(int port, ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            ChipRuntime.UartWriteByte(port, bytes[i]);
        }
    }

    public static void WriteByte(int port, byte value) => ChipRuntime.UartWriteByte(port, value);

    public static int Read(int port, Span<byte> buffer)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            var value = ChipRuntime.UartReadByte(port);
            if (value < 0)
            {
                break;
            }

            buffer[count++] = (byte)value;
        }

        return count;
    }

    public static int ReadByte(int port) => ChipRuntime.UartReadByte(port);

    public static int Available(int port) => ChipRuntime.UartAvailable(port);

    public static void Watch(int port) => ChipRuntime.UartWatch(port);

    public static void Unwatch(int port) => ChipRuntime.UartUnwatch(port);
}
