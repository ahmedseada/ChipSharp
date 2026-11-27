namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's C# for <see cref="ChipSharp.Hardware.I2c"/>: each span handed to the chip's runtime
/// (<see cref="ChipRuntime"/>) as a reference to its first byte and its length; <see cref="WriteRead"/> as a write
/// without a stop and a read after a repeated start; <see cref="Scan"/> as a <see cref="Probe"/> of each address.
/// </summary>
[Substitute(typeof(ChipSharp.Hardware.I2c))]
internal static class I2c
{
    /// <summary>The first and last addresses a scan tries: 0x00 to 0x07 and 0x78 to 0x7F are reserved.</summary>
    private const int FirstAddress = 0x08;
    private const int LastAddress = 0x77;

    public static void Open(int bus, int sda, int scl, int frequency) => ChipRuntime.I2cOpen(bus, sda, scl, frequency);

    public static bool Write(int bus, int address, ReadOnlySpan<byte> data) => Send(bus, address, data, true);

    public static bool Read(int bus, int address, Span<byte> data)
    {
        if (data.IsEmpty)
        {
            byte none = 0;
            return ChipRuntime.I2cRead(bus, address, ref none, 0);
        }

        return ChipRuntime.I2cRead(bus, address, ref data[0], data.Length);
    }

    public static bool WriteRead(int bus, int address, ReadOnlySpan<byte> write, Span<byte> read)
    {
        if (write.IsEmpty)
        {
            return Read(bus, address, read);
        }

        if (read.IsEmpty)
        {
            return Write(bus, address, write);
        }

        return Send(bus, address, write, false) && ChipRuntime.I2cRead(bus, address, ref read[0], read.Length);
    }

    public static bool Probe(int bus, int address)
    {
        byte answer = 0;
        return ChipRuntime.I2cRead(bus, address, ref answer, 1);
    }

    public static int Scan(int bus, Span<byte> addresses)
    {
        var found = 0;
        for (var address = FirstAddress; address <= LastAddress && found < addresses.Length; address++)
        {
            if (Probe(bus, address))
            {
                addresses[found] = (byte)address;
                found++;
            }
        }

        return found;
    }

    private static bool Send(int bus, int address, ReadOnlySpan<byte> data, bool stop)
    {
        if (data.IsEmpty)
        {
            byte none = 0;
            return ChipRuntime.I2cWrite(bus, address, in none, 0, stop);
        }

        return ChipRuntime.I2cWrite(bus, address, in data[0], data.Length, stop);
    }
}
