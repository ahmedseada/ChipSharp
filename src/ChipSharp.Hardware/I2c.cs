namespace ChipSharp.Hardware;

/// <summary>
/// The chip's I2C buses: two wires, SDA (data) and SCL (clock), that the chip shares with sensors, clocks, memories and
/// port expanders, each at its own 7-bit address. The chip leads every transfer and the device at the address answers.
/// <see cref="Open"/> a bus before using it. On the Raspberry Pi Pico there are two, bus 0 and bus 1 (I2C0 and I2C1),
/// each on pins of its own: SDA on GPIO 0, 4, 8, 12, 16, 20, 24 or 28 and SCL on GPIO 1, 5, 9, ..., 29 for bus 0;
/// SDA on GPIO 2, 6, 10, ..., 26 and SCL on GPIO 3, 7, 11, ..., 27 for bus 1.
/// </summary>
/// <remarks>
/// <para>
/// A transfer says whether it worked: true when the device answered its address and took every byte, false when no
/// device answered (nothing at that address, or it is not wired), when the device refused a byte, or when the bus did
/// not move for a while (a device holding the clock low): a transfer never waits for ever. Nothing allocates; the
/// bytes come from and go to spans, e.g. of <c>stackalloc</c>.
/// </para>
/// <para>
/// A bus, a pin, a frequency or an address the chip does not have is a mistake in the program: <c>chipsharp check</c>
/// names it when the value is a constant, and on the chip the program stops with a message that says what is wrong.
/// </para>
/// <para>
/// These methods run on a chip whose target has I2C (<c>"i2c": true</c> among its capabilities), in a program built
/// with ChipSharp. On .NET itself they throw <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </remarks>
public static class I2c
{
    /// <summary>
    /// Sets up <paramref name="bus"/> on the pins <paramref name="sda"/> and <paramref name="scl"/> (with the chip's
    /// pull-ups on; a long bus or a fast one needs resistors of its own, e.g. 4.7 kΩ to 3.3 V), with its clock at
    /// <paramref name="frequency"/> hertz: 100_000 (standard mode), 400_000 (fast mode) or up to 1_000_000 (fast mode
    /// plus); from 2_000. Call it again to change the frequency.
    /// </summary>
    public static void Open(int bus, int sda, int scl, int frequency) => throw ChipOnly.Method("I2c.Open");

    /// <summary>
    /// Sends <paramref name="data"/> to the device at <paramref name="address"/> (0 to 127), from a start to a stop.
    /// True when the device answered and took every byte. An empty span sends nothing, and says false.
    /// </summary>
    public static bool Write(int bus, int address, ReadOnlySpan<byte> data) => throw ChipOnly.Method("I2c.Write");

    /// <summary>
    /// Reads as many bytes as <paramref name="data"/> holds from the device at <paramref name="address"/> (0 to 127).
    /// True when the device answered; then every byte of <paramref name="data"/> is the device's. An empty span reads
    /// nothing, and says false.
    /// </summary>
    public static bool Read(int bus, int address, Span<byte> data) => throw ChipOnly.Method("I2c.Read");

    /// <summary>
    /// Sends <paramref name="write"/> to the device at <paramref name="address"/>, then, with a repeated start and no
    /// stop in between, reads <paramref name="read"/> from it: how most devices give the value of a register (write
    /// the register's number, read its value). True when the device answered both and took every byte.
    /// </summary>
    public static bool WriteRead(int bus, int address, ReadOnlySpan<byte> write, Span<byte> read) =>
        throw ChipOnly.Method("I2c.WriteRead");

    /// <summary>
    /// True when a device answers at <paramref name="address"/>: it reads one byte from it, which most devices allow at
    /// any time.
    /// </summary>
    public static bool Probe(int bus, int address) => throw ChipOnly.Method("I2c.Probe");

    /// <summary>
    /// Looks for devices: <see cref="Probe"/>s every address from 0x08 to 0x77 (the others are reserved), puts each
    /// that answers into <paramref name="addresses"/>, in order, and returns how many it found. It stops when
    /// <paramref name="addresses"/> is full.
    /// </summary>
    public static int Scan(int bus, Span<byte> addresses) => throw ChipOnly.Method("I2c.Scan");
}
