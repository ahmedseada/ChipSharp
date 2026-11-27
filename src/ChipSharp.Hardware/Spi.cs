namespace ChipSharp.Hardware;

/// <summary>
/// The chip's SPI buses: a clock (SCK), the chip's data out (TX, also called MOSI or SDO) and its data in (RX, MISO,
/// SDI), for fast devices: shift registers, memories, converters, radios. <see cref="Open"/> a bus before using it.
/// With every byte the chip sends it receives one, most significant bit first. On the Raspberry Pi Pico there are two,
/// bus 0 and bus 1 (SPI0 and SPI1), each on pins of its own: for bus 0 SCK on GPIO 2, 6, 18 or 22, TX on 3, 7, 19 or 23
/// and RX on 0, 4, 16 or 20; for bus 1 SCK on GPIO 10, 14 or 26, TX on 11, 15 or 27 and RX on 8, 12, 24 or 28.
/// </summary>
/// <remarks>
/// <para>
/// Chip select stays a <see cref="Pin"/> the program drives, so that one bus serves several devices, each with a pin
/// of its own: make it an output and high at the start (<c>Pin.Output(17); Pin.Write(17, true);</c>), then around
/// each transfer drive it low, transfer, and drive it high again. The methods return once the last bit has left, so
/// the pin can go high right after. A 74HC595 shift register takes its latch (RCLK) the same way: its outputs show
/// what it was sent when the pin goes high.
/// </para>
/// <para>
/// SPI has no answer to wait for, so the methods return nothing. A bus, a pin, a frequency or a mode the chip does not
/// have is a mistake in the program: <c>chipsharp check</c> names it when the value is a constant, and on the chip the
/// program stops with a message that says what is wrong. Nothing allocates.
/// </para>
/// <para>
/// These methods run on a chip whose target has SPI (<c>"spi": true</c> among its capabilities), in a program built
/// with ChipSharp. On .NET itself they throw <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </remarks>
public static class Spi
{
    /// <summary>
    /// Sets up <paramref name="bus"/> as the bus's controller on the pins <paramref name="sck"/>, <paramref name="tx"/>
    /// and <paramref name="rx"/> (-1 for none: no <paramref name="rx"/> for a device that only listens, such as a shift
    /// register; no <paramref name="tx"/> for one that only talks), with its clock at <paramref name="frequency"/>
    /// hertz, from 2_000 to 62_500_000 (the chip takes the nearest it can make below it), in SPI
    /// <paramref name="mode"/> 0 to 3: 0 (the clock low when idle, bits read on its rising edge) suits most devices,
    /// 1 reads on the falling edge, 2 and 3 are 0 and 1 with the clock high when idle. Call it again to change them.
    /// </summary>
    public static void Open(int bus, int sck, int tx, int rx, int frequency, int mode) => throw ChipOnly.Method("Spi.Open");

    /// <summary>Sends <paramref name="data"/>, and drops what comes back.</summary>
    public static void Write(int bus, ReadOnlySpan<byte> data) => throw ChipOnly.Method("Spi.Write");

    /// <summary>Fills <paramref name="data"/> with what the device sends while the chip sends zeros.</summary>
    public static void Read(int bus, Span<byte> data) => throw ChipOnly.Method("Spi.Read");

    /// <summary>
    /// Sends <paramref name="write"/> and, at the same time, fills <paramref name="read"/>, which must be as long, with
    /// what comes back: the byte that arrives while the first is sent is <c>read[0]</c>, and so on. The two may be the
    /// same span.
    /// </summary>
    public static void Transfer(int bus, ReadOnlySpan<byte> write, Span<byte> read) => throw ChipOnly.Method("Spi.Transfer");
}
