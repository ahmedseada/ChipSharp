namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// The USB Serial/JTAG controller's serial port (TRM v1.1, chapter USB Serial/JTAG Controller; the SVD's USB_DEVICE),
/// the console the devkit's USB port shows, with a host that has the port open: EP1 (a write adds a byte to the 64-byte
/// IN FIFO, a read takes one from the OUT FIFO), EP1_CONF (WR_DONE sends what the IN FIFO holds; SERIAL_IN_EP_DATA_FREE
/// while it has room; SERIAL_OUT_EP_DATA_AVAIL while the OUT FIFO has bytes) and the interrupts (INT_RAW, INT_ST,
/// INT_ENA, INT_CLR) SERIAL_IN_EMPTY (the IN FIFO was taken by the host) and SERIAL_OUT_RECV_PKT (bytes came from the
/// host); INT_ST is the interrupt source USB_SERIAL_JTAG (54). The host takes a packet (what WR_DONE sent, or 64 bytes
/// once the FIFO is full) <see cref="PacketTicks"/> after it is ready: the FIFO has no room until then, and the bytes are
/// printed then ("USB: Hello"). Bytes the program never sends (no WR_DONE, the FIFO not full) are not printed, as the
/// host would not see them. --send's bytes for the port (USB=...) come a microsecond apart and wait at the host while
/// the OUT FIFO (64 bytes) is full: USB loses none.
/// </summary>
internal sealed class Esp32c5UsbSerialJtag(SimulatedChip chip) : ISerialInput
{
    private const int FifoSize = 64;
    private const uint OutReceived = 1 << 2, InEmpty = 1 << 3;

    /// <summary>How long the host takes to fetch a packet: 50 microseconds.</summary>
    public const long PacketTicks = SimulatedChip.TicksPerSecond / 20_000;

    private readonly List<byte> _in = [];
    private readonly Queue<byte> _out = new();
    private readonly Queue<byte> _host = new();
    private readonly Dictionary<uint, uint> _other = [];
    private long _sendAt = long.MaxValue;
    private uint _raw = InEmpty;
    private uint _enable;

    public string Name => "USB";

    /// <summary>The bytes a microsecond apart: what 10 bits a byte at 10 MBd gives --send's schedule.</summary>
    public int BaudRate => 10_000_000;

    public SerialMonitor Output { get; } = new(chip, "USB");

    /// <summary>Called when a byte arrived from the host.</summary>
    public Action? Changed { get; set; }

    /// <summary>The interrupt source USB_SERIAL_JTAG: INT_ST.</summary>
    public bool InterruptPending => (_raw & _enable) != 0;

    public bool ReceiveInterrupts => (_enable & OutReceived) != 0;

    /// <summary>The tick the host takes the IN FIFO's packet, or long.MaxValue.</summary>
    public long NextEvent => _sendAt;

    private bool InFree => _sendAt == long.MaxValue && _in.Count < FifoSize;

    public void Receive(byte value)
    {
        _host.Enqueue(value);
        Fill();
        Changed?.Invoke();
    }

    public void Advance()
    {
        if (_sendAt > chip.Ticks)
        {
            return;
        }

        foreach (var b in _in)
        {
            Output.Write(b);
        }

        _in.Clear();
        _sendAt = long.MaxValue;
        _raw |= InEmpty;
    }

    public uint Read(uint offset)
    {
        switch (offset)
        {
            case 0x00:
                var value = _out.Count > 0 ? _out.Dequeue() : 0u;
                Fill();
                return value;
            case 0x04:
                return (InFree ? 2u : 0) | (_out.Count > 0 ? 4u : 0);
            case 0x08:
                return _raw;
            case 0x0C:
                return _raw & _enable;
            case 0x10:
                return _enable;
            case 0x18:
                return _other.GetValueOrDefault(offset, 0x4200u);
            case 0x80:
                return 0x0231_1240;
            default:
                return _other.GetValueOrDefault(offset);
        }
    }

    public void Write(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x00:
                if (InFree)
                {
                    _in.Add((byte)value);
                    if (_in.Count == FifoSize)
                    {
                        Send();
                    }
                }

                break;
            case 0x04:
                if ((value & 1) != 0 && _sendAt == long.MaxValue && _in.Count > 0)
                {
                    Send();
                }

                break;
            case 0x08:
                _raw = value & 0xFFFF;
                break;
            case 0x10:
                _enable = value & 0xFFFF;
                break;
            case 0x14:
                _raw &= ~value;
                break;
            default:
                _other[offset] = value;
                break;
        }
    }

    private void Send()
    {
        _sendAt = chip.Ticks + PacketTicks;
        _raw &= ~InEmpty;
    }

    /// <summary>The host's bytes go into the OUT FIFO while it has room.</summary>
    private void Fill()
    {
        var any = false;
        while (_host.Count > 0 && _out.Count < FifoSize)
        {
            _out.Enqueue(_host.Dequeue());
            any = true;
        }

        if (any)
        {
            _raw |= OutReceived;
        }
    }
}
