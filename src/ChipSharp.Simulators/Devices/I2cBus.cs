using System.Globalization;

namespace ChipSharp.Simulators.Devices;

/// <summary>
/// A device on an I2C bus, as the bus hands it each step of a transfer (tools/rp2040-sim/devices' devices, in C#, for
/// chipsim's chips): a start addressed to it, each byte written or read, the stop.
/// </summary>
public interface II2cDevice
{
    /// <summary>The device's name, as the bus prints it: "MPU-6050".</summary>
    string Name { get; }

    /// <summary>A device that holds the clock low for ever once addressed: no transfer on its bus ends from then on.</summary>
    bool HoldsClock => false;

    /// <summary>A start (or a repeated start) addressed to the device, for a read or a write: true to answer it.</summary>
    bool Start(bool read);

    /// <summary>A byte the controller writes: true to take it (an ACK).</summary>
    bool Write(byte value);

    /// <summary>The byte the device sends when the controller reads one.</summary>
    byte Read();

    void Stop();

    /// <summary>The run ends: what the device has not printed yet (an LCD that has not settled).</summary>
    void Finish()
    {
    }
}

/// <summary>
/// An I2C bus with its devices, as a chip's controller drives it, which prints what happens on it, as
/// tools/rp2040-sim/devices/catalog.mjs does for the Pico and the Uno:
/// <code>
/// I2C0 100000 Hz                     the clock the program set, when a transfer first uses it
/// I2C0 0x68 write 75, read 68        a transfer, from its start to its stop: the bytes each way, in hex, each part
///                                    after a repeated start after a comma
/// I2C0 0x23 write: no answer         nothing answered the address
/// I2C0 0x68 write 6b 00 (refused)    the device refused a byte
/// I2C0 0x50 write: the stuck device holds the clock low
/// </code>
/// The controller calls <see cref="Start"/> at a start or repeated start, <see cref="Connect"/> with the address,
/// <see cref="Write"/> and <see cref="Read"/> for each byte, and <see cref="Stop"/> at the stop. Every address without a
/// device leaves the controller without an answer (a NACK), as an empty bus does.
/// </summary>
public sealed class I2cBus(string name, Action<string> log)
{
    private readonly Dictionary<int, II2cDevice> _devices = [];
    private List<Part>? _parts;
    private int _address;
    private II2cDevice? _device;
    private int? _frequency;

    /// <summary>The bus's name: "I2C0".</summary>
    public string Name { get; } = name;

    /// <summary>The devices, by address.</summary>
    public IReadOnlyDictionary<int, II2cDevice> Devices => _devices;

    /// <summary>Puts a device on the bus at an address (7 bits), in place of one that was there.</summary>
    public void Add(int address, II2cDevice device) => _devices[address] = device;

    public void Start() => _parts ??= [];

    /// <summary>
    /// The device at <paramref name="address"/> answers (true) or not (false), for a read or a write, at the bus's clock
    /// (hertz); null when it holds the clock low, and the controller never hears from it.
    /// </summary>
    public bool? Connect(int address, bool read, int hertz)
    {
        if (hertz != _frequency)
        {
            log(string.Create(CultureInfo.InvariantCulture, $"{Name} {hertz} Hz"));
            _frequency = hertz;
        }

        _parts ??= [];
        _address = address;
        _device = Devices.GetValueOrDefault(address);
        if (_device is { HoldsClock: true })
        {
            log($"{Name} 0x{address:x2} {(read ? "read" : "write")}: the {_device.Name} device holds the clock low");
            return null;
        }

        var answered = _device is not null && _device.Start(read);
        _parts.Add(new Part(read, answered));
        return answered;
    }

    /// <summary>A byte the controller writes: true when the device took it.</summary>
    public bool Write(byte value)
    {
        var part = _parts![^1];
        var taken = _device!.Write(value);
        part.Bytes.Add(value);
        part.Refused = !taken;
        return taken;
    }

    /// <summary>The byte the device sends.</summary>
    public byte Read()
    {
        var value = _device!.Read();
        _parts![^1].Bytes.Add(value);
        return value;
    }

    public void Stop()
    {
        _device?.Stop();
        if (_parts is { Count: > 0 })
        {
            var text = _parts.Select(part => (part.Read ? "read" : "write") + (!part.Answered ? ": no answer"
                : string.Concat(part.Bytes.Select(value => $" {value:x2}")) + (part.Refused ? " (refused)" : "")));
            log($"{Name} 0x{_address:x2} {string.Join(", ", text)}");
        }

        _parts = null;
        _device = null;
    }

    /// <summary>The run ends: the devices print what they have not printed yet.</summary>
    public void Finish()
    {
        foreach (var device in Devices.Values)
        {
            device.Finish();
        }
    }

    private sealed class Part(bool read, bool answered)
    {
        public bool Read { get; } = read;

        public bool Answered { get; } = answered;

        public List<byte> Bytes { get; } = [];

        public bool Refused { get; set; }
    }
}

/// <summary>
/// An I2C device made of 8-bit registers, as most sensors are: a write's first byte chooses the register, the bytes after
/// it are written from there on, and a read reads from the chosen register on, one register further each byte. A device
/// of this kind gives its registers' values (<see cref="Value"/>) and what writing them does (<see cref="Written"/>).
/// </summary>
public abstract class RegisterDevice(string name) : II2cDevice
{
    public string Name { get; } = name;

    protected byte[] Registers { get; } = new byte[256];

    protected int Pointer { get; set; }

    /// <summary>Whether this write has chosen its register yet.</summary>
    protected bool Chosen { get; set; }

    public virtual bool Start(bool read)
    {
        if (!read)
        {
            Chosen = false;
        }

        return true;
    }

    public virtual bool Write(byte value)
    {
        if (!Chosen)
        {
            Pointer = value;
            Chosen = true;
        }
        else
        {
            Registers[Pointer] = value;
            Written(Pointer, value);
            Pointer = (Pointer + 1) & 0xFF;
        }

        return true;
    }

    public virtual byte Read()
    {
        var value = Value(Pointer);
        Pointer = (Pointer + 1) & 0xFF;
        return value;
    }

    public virtual void Stop()
    {
    }

    /// <summary>A register's value, as a read sees it: what it holds, unless the device works it out.</summary>
    protected virtual byte Value(int register) => Registers[register];

    protected virtual void Written(int register, byte value)
    {
    }

    /// <summary>A 16-bit value in two registers, most significant byte first (big-endian), as the MPU-6050 has them.</summary>
    protected static void SetBigEndian(byte[] registers, int at, int value)
    {
        registers[at] = (byte)(value >> 8);
        registers[at + 1] = (byte)value;
    }

    /// <summary>A 16-bit value in two registers, least significant byte first (little-endian), as the BMP280's calibration.</summary>
    protected static void SetLittleEndian(byte[] registers, int at, int value)
    {
        registers[at] = (byte)value;
        registers[at + 1] = (byte)(value >> 8);
    }
}

/// <summary>A device that answers its address, then never lets the clock go: every transfer on its bus waits for it from then on.</summary>
public sealed class StuckDevice : II2cDevice
{
    public string Name => "stuck";

    public bool HoldsClock => true;

    public bool Start(bool read) => true;

    public bool Write(byte value) => true;

    public byte Read() => 0xFF;

    public void Stop()
    {
    }
}
