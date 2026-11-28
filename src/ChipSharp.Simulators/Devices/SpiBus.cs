using System.Globalization;

namespace ChipSharp.Simulators.Devices;

/// <summary>A device on an SPI bus: it takes each byte the controller sends, and may send one back.</summary>
public interface ISpiDevice
{
    /// <summary>The device's name: "74HC595".</summary>
    string Name { get; }

    /// <summary>One byte from the bus in SPI mode 0 to 3; the byte the device sends back at the same time, or null for none.</summary>
    byte? Transfer(byte value, int mode);
}

/// <summary>
/// An SPI bus with its devices, as a chip's controller drives it: each byte goes to every device whose chip select pin is
/// low (or that has none), and the answer of the last that gives one comes back (0 when none does). It prints the mode and
/// clock when a transfer first uses them, and what its devices report, as tools/rp2040-sim/devices does:
/// <code>
/// SPI0 mode 0, 1000000 Hz            the mode and clock the program set
/// SPI0 74HC595 Q3 high               a shift register's output changed (its latch went high)
/// </code>
/// </summary>
public sealed class SpiBus(string name, Action<string> log)
{
    private readonly List<(ISpiDevice Device, (GpioPort Port, int Bit)? Select)> _devices = [];
    private string? _settings;

    /// <summary>The bus's name: "SPI0".</summary>
    public string Name { get; } = name;

    /// <summary>The devices, each with its chip select pin, or none.</summary>
    public IReadOnlyList<(ISpiDevice Device, (GpioPort Port, int Bit)? Select)> Devices => _devices;

    public void Add(ISpiDevice device, (GpioPort Port, int Bit)? select) => _devices.Add((device, select));

    /// <summary>Prints a line of the bus's: "SPI0 74HC595 Q3 high".</summary>
    public void Report(string text) => log($"{Name} {text}");

    /// <summary>A byte the controller sends in <paramref name="mode"/> at <paramref name="hertz"/>; the byte that comes back.</summary>
    public byte Transfer(byte value, int mode, int hertz)
    {
        var settings = string.Create(CultureInfo.InvariantCulture, $"mode {mode}, {hertz} Hz");
        if (settings != _settings)
        {
            Report(settings);
            _settings = settings;
        }

        byte answer = 0;
        foreach (var (device, select) in _devices)
        {
            if (select is not { } pin || (pin.Port.Levels & (1 << pin.Bit)) == 0)
            {
                answer = device.Transfer(value, mode) ?? answer;
            }
        }

        return answer;
    }
}

/// <summary>
/// The 74HC595 shift register on an SPI bus, as tools/rp2040-sim/devices/shift-register.mjs models it: TX to its serial
/// input (DS), SCK to its shift clock (SHCP), and a pin of the program's to its latch (STCP). After a byte, sent most
/// significant bit first, bit n of the byte is at output Qn; its eight outputs show what it holds when the latch pin goes
/// from low to high, and each that changes is reported. Its bits move in on the clock's rising edge, so SPI modes 0 and 3
/// suit it; modes 1 and 2 change the data on that very edge, which a real one would read wrong: this one says so (once)
/// and moves the bits in anyway.
/// </summary>
public sealed class ShiftRegister(SpiBus bus) : ISpiDevice
{
    private byte _shifted;
    private byte _outputs;
    private bool _warned;

    public string Name => "74HC595";

    /// <summary>One byte from the bus; nothing comes back (its Q7S, the serial output, is not wired to RX).</summary>
    public byte? Transfer(byte value, int mode)
    {
        if (mode is 1 or 2 && !_warned)
        {
            bus.Report($"74HC595: SPI mode {mode} changes the data on the clock's rising edge, where the 74HC595 reads it; " +
                "use mode 0 or 3");
            _warned = true;
        }

        _shifted = value;
        return null;
    }

    /// <summary>The latch pin went high: the outputs take what the shift register holds.</summary>
    public void Latched()
    {
        for (var bit = 0; bit < 8; bit++)
        {
            var mask = 1 << bit;
            if (((_outputs ^ _shifted) & mask) != 0)
            {
                bus.Report($"74HC595 Q{bit} {((_shifted & mask) != 0 ? "high" : "low")}");
            }
        }

        _outputs = _shifted;
    }
}

/// <summary>RX wired to TX: every byte comes back as it was sent, while its chip select (when it has one) is low.</summary>
public sealed class Loopback : ISpiDevice
{
    public string Name => "loopback";

    public byte? Transfer(byte value, int mode) => value;
}
