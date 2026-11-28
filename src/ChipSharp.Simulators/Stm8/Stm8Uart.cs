namespace ChipSharp.Simulators.Stm8;

/// <summary>
/// UART1 of the STM8S (RM0016 chapter 22), asynchronous: SR (TXE, TC, RXNE, OR), DR, BRR1/BRR2 (the divider of fMASTER,
/// BRR2 first, BRR1 loading it), CR1 (UARTD, M), CR2 (TIEN, TCIEN, RIEN, TEN, REN), CR3 (the stop bits). A byte written
/// to DR goes to the shift register when it is empty (TXE again at once) and out on TX after its frame (a start bit, 8
/// or 9 data bits, the stop bits) at the baud rate, which sets TC when nothing follows; each byte is what the serial
/// output (<see cref="SerialMonitor"/>) prints. A byte arriving (--send) sets RXNE, or OR when the one before has not
/// been read, and is lost while the receiver is off; reading DR clears RXNE and OR. Its interrupts: vector 17 for TXE
/// and TC, 18 for RXNE and OR. The baud rate is printed when it changes with TX or RX on ("UART1 baud 9600").
/// </summary>
internal sealed class Stm8Uart(SimulatedChip chip, string name, Func<long> ticksPerMasterClock) : ISerialInput
{
    private const int Sr = 0, Dr = 1, Brr1 = 2, Brr2 = 3, Cr1 = 4, Cr2 = 5, Cr3 = 6;
    private const byte Txe = 0x80, Tc = 0x40, Rxne = 0x20, Or = 0x08;

    private readonly byte[] _registers = new byte[11];
    private byte _sr = Txe | Tc;
    private byte _received;
    private int _divider;
    private byte? _pending;
    private long _shiftTicksLeft;
    private byte _shifting;
    private bool _busy;
    private int _printedBaud;

    public string Name { get; } = name;

    /// <summary>What the port writes.</summary>
    public SerialMonitor Output { get; } = new(chip, name);

    public int BaudRate => _divider >= 16 ? (int)Math.Round((double)SimulatedChip.TicksPerSecond / ticksPerMasterClock() / _divider) : 0;

    /// <summary>Whether the TX interrupt (TXE with TIEN, TC with TCIEN) is requested.</summary>
    public bool TransmitPending
    {
        get
        {
            var cr2 = _registers[Cr2];
            return ((_sr & Txe) != 0 && (cr2 & 0x80) != 0) || ((_sr & Tc) != 0 && (cr2 & 0x40) != 0);
        }
    }

    /// <summary>Whether the RX interrupt (RXNE or OR, with RIEN) is requested.</summary>
    public bool ReceivePending => (_sr & (Rxne | Or)) != 0 && (_registers[Cr2] & 0x20) != 0;

    /// <summary>Whether the UART could request an interrupt by itself (one is enabled, or a byte is going out).</summary>
    public bool CanInterrupt => (_registers[Cr2] & 0xE0) != 0;

    private bool Enabled => (_registers[Cr1] & 0x20) == 0;

    public byte Read(int offset)
    {
        switch (offset)
        {
            case Sr:
                return _sr;
            case Dr:
                _sr &= unchecked((byte)~(Rxne | Or));
                return _received;
            default:
                return offset < _registers.Length ? _registers[offset] : (byte)0;
        }
    }

    public void Write(int offset, byte value)
    {
        switch (offset)
        {
            case Sr:
                _sr &= (byte)(value | ~(Tc | Rxne)); // TC and RXNE are cleared by writing 0
                return;
            case Dr:
                _sr &= unchecked((byte)~Tc);
                if (_busy || (_registers[Cr2] & 0x08) == 0)
                {
                    _pending = value; // waits in DR for the shift register, or for TEN
                    _sr &= unchecked((byte)~Txe);
                }
                else
                {
                    StartFrame(value);
                }

                return;
        }

        if (offset >= _registers.Length)
        {
            return;
        }

        _registers[offset] = value;
        switch (offset)
        {
            case Brr1: // BRR2's digits [15:12] and [3:0], BRR1's [11:4]
                _divider = ((_registers[Brr2] & 0xF0) << 8) | (value << 4) | (_registers[Brr2] & 0x0F);
                ClockChanged();
                break;
            case Cr2:
                if ((value & 0x08) != 0 && !_busy && _pending is { } waiting)
                {
                    _pending = null;
                    _sr |= Txe;
                    StartFrame(waiting);
                }

                ClockChanged();
                break;
        }
    }

    /// <summary>A byte's stop bit has arrived on RX.</summary>
    public void Receive(byte value)
    {
        if (!Enabled || (_registers[Cr2] & 0x04) == 0)
        {
            return; // the receiver is off: the byte is lost
        }

        if ((_sr & Rxne) != 0)
        {
            _sr |= Or; // overrun: the byte before has not been read, this one is lost
            return;
        }

        _received = value;
        _sr |= Rxne;
    }

    /// <summary>Lets <paramref name="ticks"/> pass for the byte going out.</summary>
    public void Advance(long ticks)
    {
        if (!_busy)
        {
            return;
        }

        _shiftTicksLeft -= ticks;
        if (_shiftTicksLeft > 0)
        {
            return;
        }

        _busy = false;
        var end = chip.Ticks;
        chip.SetTime(end + _shiftTicksLeft); // when the stop bit ended
        Output.Write(_shifting);
        chip.SetTime(end);
        if (_pending is { } next && (_registers[Cr2] & 0x08) != 0)
        {
            _pending = null;
            _sr |= Txe;
            StartFrame(next);
        }
        else
        {
            _sr |= Tc;
        }
    }

    /// <summary>The ticks until the byte going out is out, or long.MaxValue.</summary>
    public long TicksToEvent() => _busy ? Math.Max(_shiftTicksLeft, 1) : long.MaxValue;

    /// <summary>The master clock or the divider changed: prints the new baud rate, when TX or RX is on.</summary>
    public void ClockChanged()
    {
        var baud = BaudRate;
        if (baud != 0 && baud != _printedBaud && (_registers[Cr2] & 0x0C) != 0)
        {
            _printedBaud = baud;
            chip.Print($"{Name} baud {baud}");
        }
    }

    private void StartFrame(byte value)
    {
        _shifting = value;
        _busy = true;
        var dataBits = (_registers[Cr1] & 0x10) != 0 ? 9 : 8;
        var halfStopBits = ((_registers[Cr3] >> 4) & 3) switch { 2 => 4, 3 => 3, _ => 2 };
        var halfBits = 2 * (1 + dataBits) + halfStopBits;
        _shiftTicksLeft = Math.Max(1, halfBits * Math.Max(_divider, 16) * ticksPerMasterClock() / 2);
    }
}
