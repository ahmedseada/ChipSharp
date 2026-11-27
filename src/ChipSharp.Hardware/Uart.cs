namespace ChipSharp.Hardware;

/// <summary>
/// The chip's serial ports (UARTs), by number: on the Raspberry Pi Pico, UART0 and UART1, 8 data bits, no parity, one
/// stop bit. <see cref="Open"/> a port before using it. UART0 is also the console's: <c>Console.Write</c> sends on it
/// and <c>Console.Read</c> and <c>Console.ReadLine</c> read from it (on GPIO 0 and 1 at 115200 baud, unless the
/// program opens UART0 on other pins or at another speed).
/// </summary>
/// <remarks>
/// <para>
/// From <see cref="Open"/> on, the chip receives on its own: an interrupt moves each byte that arrives into a buffer
/// of 256 bytes per port, also while the program is busy, so <see cref="Read"/>, <see cref="ReadByte"/> and
/// <see cref="Available"/> never wait and can be called from an interrupt handler. When the buffer is full, the bytes
/// that arrive after are lost. <see cref="Watch"/> runs the program's <c>[Interrupt(Event.Uart)]</c> method each time
/// bytes arrive.
/// </para>
/// <para>
/// A port that is not open, or a pin that cannot be that port's TX or RX, stops the program with a message saying so;
/// <c>chipsharp check</c> finds the pins and ports a program gives as constants before it runs.
/// </para>
/// <para>
/// These methods run on a chip, in a program built with ChipSharp. On .NET itself they throw
/// <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </remarks>
public static class Uart
{
    /// <summary>
    /// Opens UART <paramref name="port"/> (0 or 1 on the Pico) on the pins <paramref name="tx"/> (which sends) and
    /// <paramref name="rx"/> (which receives), at <paramref name="baud"/> bits per second, 8N1. On the Pico, UART0's TX
    /// is GPIO 0, 12, 16 or 28 and its RX GPIO 1, 13, 17 or 29; UART1's TX is GPIO 4, 8, 20 or 24 and its RX GPIO 5,
    /// 9, 21 or 25. The speed can be from 120 to 7812500 baud.
    /// </summary>
    public static void Open(int port, int tx, int rx, int baud) => throw ChipOnly.Method("Uart.Open");

    /// <summary>Sends <paramref name="bytes"/>, waiting while the port's 32-byte send FIFO is full.</summary>
    public static void Write(int port, ReadOnlySpan<byte> bytes) => throw ChipOnly.Method("Uart.Write");

    /// <summary>Sends one byte, waiting while the port's send FIFO is full.</summary>
    public static void WriteByte(int port, byte value) => throw ChipOnly.Method("Uart.WriteByte");

    /// <summary>
    /// Moves the bytes that have arrived into <paramref name="buffer"/>, as many as it holds, and returns how many it
    /// moved: 0 when none has arrived. It does not wait.
    /// </summary>
    public static int Read(int port, Span<byte> buffer) => throw ChipOnly.Method("Uart.Read");

    /// <summary>The next byte that has arrived (0 to 255), or -1 when none has. It does not wait.</summary>
    public static int ReadByte(int port) => throw ChipOnly.Method("Uart.ReadByte");

    /// <summary>How many bytes have arrived and wait to be read.</summary>
    public static int Available(int port) => throw ChipOnly.Method("Uart.Available");

    /// <summary>
    /// Runs the program's <c>[Interrupt(Event.Uart)]</c> method each time bytes arrive on <paramref name="port"/>, after
    /// they are in the buffer (at once when some are waiting already). One method handles every port watched; it can
    /// read them with <see cref="Read"/>, <see cref="ReadByte"/> and <see cref="Available"/>.
    /// </summary>
    public static void Watch(int port) => throw ChipOnly.Method("Uart.Watch");

    /// <summary>Stops watching <paramref name="port"/>: bytes that arrive still go into its buffer, but no longer run the method.</summary>
    public static void Unwatch(int port) => throw ChipOnly.Method("Uart.Unwatch");
}
