using System.Globalization;

namespace ChipSharp.Simulators.Ch32v003;

/// <summary>
/// USART1 (CH32V003 RM V1.6, chapter 12), asynchronous, without flow control: STATR (PE, FE, NE, ORE, IDLE, RXNE, TC,
/// TXE; reset 0xC0), DATAR, BRR (DIV_Mantissa and DIV_Fraction: the baud rate is HCLK / (16 × USARTDIV), HCLK / BRR),
/// CTLR1 (UE, M, PCE, PS, the interrupt enables, TE, RE), CTLR2 (STOP), CTLR3 and GPR kept. A byte written to DATAR
/// with UE and TE goes to the shift register (TXE again) and out as a frame (a start bit, 8 or 9 data bits (M; the parity
/// bit the last), the stop bits) at the baud rate, then TC; its stop bit is when the serial output
/// (<see cref="SerialMonitor"/>) has it, unless the TX pin (PD5, or USART1_RM's) is not an alternate-function output:
/// then it does not leave the chip, as is printed once. --send's bytes arrive with UE and RE: RXNE and DATAR, or, while
/// RXNE is still set, ORE (the byte is lost); a byte that arrives with the receiver off is lost; a frame's time after the
/// last byte, IDLE. Reading STATR then DATAR clears ORE and IDLE; RXNE and TC are cleared by a write of 0 to them, or by
/// reading DATAR (RXNE) and by a write of DATAR after reading STATR (TC). The interrupt USART1 (32): TXE with TXEIE, TC
/// with TCIE, RXNE or ORE with RXNEIE, IDLE with IDLEIE, PE with PEIE. The baud rate is printed when a byte goes out or
/// comes in at another than the last printed ("USART1 baud 115200").
/// </summary>
internal sealed class Ch32v003Usart(SimulatedChip chip, Func<long> hclk) : ISerialInput
{
    private const uint Pe = 1, Ore = 8, Idle = 16, Rxne = 32, Tc = 64, Txe = 128;
    private const uint Ue = 1 << 13, Te = 1 << 3, Re = 1 << 2;

    private uint _status = Tc | Txe;
    private uint _brr;
    private uint _ctlr1;
    private uint _ctlr2;
    private uint _ctlr3;
    private uint _gpr;
    private uint _received;
    private int? _transmit;
    private int? _shifting;
    private long _frameEnd = long.MaxValue;
    private long _idleAt = long.MaxValue;
    private bool _statusRead;
    private int _printedBaud;
    private bool _toldPin;

    public string Name => "USART1";

    /// <summary>What the port writes.</summary>
    public SerialMonitor Output { get; } = new(chip, "USART1");

    /// <summary>Called when its state (the interrupt, its TX pin) may have changed.</summary>
    public Action? Changed { get; set; }

    /// <summary>Whether the TX pin is an alternate-function output: what it sends leaves the chip.</summary>
    public Func<bool> TxConnected { get; set; } = () => true;

    /// <summary>Whether the RX pin is a digital input: what is sent to it reaches the receiver.</summary>
    public Func<bool> RxConnected { get; set; } = () => true;

    /// <summary>The TX and RX pins' names, for what is printed.</summary>
    public Func<(string Tx, string Rx)> PinNames { get; set; } = () => ("PD5", "PD6");

    public int BaudRate => _brr == 0 ? 0 : (int)Math.Round((double)hclk() / _brr);

    /// <summary>Whether the USART drives its TX pin (UE and TE).</summary>
    public bool Transmitting => (_ctlr1 & (Ue | Te)) == (Ue | Te);

    /// <summary>The interrupt USART1.</summary>
    public bool InterruptPending
    {
        get
        {
            var c = _ctlr1;
            return ((_status & Txe) != 0 && (c & 0x80) != 0) || ((_status & Tc) != 0 && (c & 0x40) != 0) ||
                ((_status & (Rxne | Ore)) != 0 && (c & 0x20) != 0) || ((_status & Idle) != 0 && (c & 0x10) != 0) ||
                ((_status & Pe) != 0 && (c & 0x100) != 0);
        }
    }

    /// <summary>Whether its interrupts could follow from bytes to come (RXNEIE, IDLEIE).</summary>
    public bool ReceiveInterrupts => (_ctlr1 & 0x30) != 0;

    /// <summary>The tick of its next event (the frame going out ends, the line goes idle), or long.MaxValue.</summary>
    public long NextEvent => Math.Min(_frameEnd, _idleAt);

    /// <summary>The tick it will have sent what it holds, or long.MaxValue when it sends nothing.</summary>
    public long IdleTick => _shifting is null ? long.MaxValue : _frameEnd + (_transmit is null ? 0 : FrameTicks());

    /// <summary>Its registers to their reset values (RCC_APB2PRSTR's USART1RST).</summary>
    public void Reset()
    {
        (_status, _brr, _ctlr1, _ctlr2, _ctlr3, _gpr) = (Tc | Txe, 0, 0, 0, 0, 0);
        (_transmit, _shifting, _frameEnd, _idleAt) = (null, null, long.MaxValue, long.MaxValue);
        Changed?.Invoke();
    }

    public void Receive(byte value)
    {
        if ((_ctlr1 & (Ue | Re)) != (Ue | Re) || !RxConnected())
        {
            return; // the receiver is off, or the pin is not an input: the byte is lost
        }

        PrintBaud();
        if ((_status & Rxne) != 0)
        {
            _status |= Ore;
        }
        else
        {
            _received = value;
            _status |= Rxne;
        }

        _status &= ~Idle;
        _idleAt = chip.Ticks + FrameTicks();
        Changed?.Invoke();
    }

    /// <summary>Lets the frame going out end, and the line go idle, when they are due.</summary>
    public void Advance()
    {
        while (_frameEnd <= chip.Ticks)
        {
            var end = chip.Ticks;
            chip.SetTime(_frameEnd);
            if (TxConnected())
            {
                Output.Write((byte)_shifting!.Value);
            }
            else if (!_toldPin)
            {
                _toldPin = true;
                chip.Print($"USART1 TX ({PinNames().Tx}) is not an alternate-function output: what USART1 sends does not leave the chip");
            }

            chip.SetTime(end);
            var finished = _frameEnd;
            _shifting = null;
            _frameEnd = long.MaxValue;
            if (_transmit is { } next)
            {
                StartFrame(next, finished);
            }
            else
            {
                _status |= Tc;
            }
        }

        if (_idleAt <= chip.Ticks)
        {
            _idleAt = long.MaxValue;
            _status |= Idle;
        }

        Changed?.Invoke();
    }

    public uint Read(uint offset)
    {
        switch (offset)
        {
            case 0x00:
                _statusRead = true;
                return _status;
            case 0x04:
                var value = _received;
                _status &= ~Rxne;
                if (_statusRead)
                {
                    _status &= ~(Ore | Idle | Pe | 2 | 4);
                }

                _statusRead = false;
                Changed?.Invoke();
                return value;
            case 0x08:
                return _brr;
            case 0x0C:
                return _ctlr1;
            case 0x10:
                return _ctlr2;
            case 0x14:
                return _ctlr3;
            case 0x18:
                return _gpr;
            default:
                return 0;
        }
    }

    public void Write(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x00: // RW0: CTS, LBD, TC and RXNE are cleared by writing 0
                _status &= value | ~(Tc | Rxne | 0x300u);
                break;
            case 0x04:
                if (_statusRead)
                {
                    _status &= ~Tc;
                }

                _statusRead = false;
                if ((_ctlr1 & (Ue | Te)) == (Ue | Te))
                {
                    _status &= ~(Txe | Tc);
                    var data = (int)(value & 0x1FF);
                    if (_shifting is null)
                    {
                        StartFrame(data, chip.Ticks);
                    }
                    else
                    {
                        _transmit = data;
                    }
                }

                break;
            case 0x08:
                _brr = value & 0xFFFF;
                break;
            case 0x0C:
                _ctlr1 = value & 0x3FFF;
                break;
            case 0x10:
                _ctlr2 = value & 0x7F7F;
                break;
            case 0x14:
                _ctlr3 = value & 0x7FF;
                break;
            case 0x18:
                _gpr = value & 0xFFFF;
                break;
        }

        Changed?.Invoke();
    }

    /// <summary>A byte moves into the shift register and starts going out: TXE again.</summary>
    private void StartFrame(int data, long from)
    {
        _transmit = null;
        if (_brr < 16)
        {
            _transmit = data; // no baud rate (BRR below 16 is not one): the byte waits
            return;
        }

        PrintBaud();
        _shifting = data;
        _frameEnd = from + FrameTicks();
        _status |= Txe;
    }

    private void PrintBaud()
    {
        var baud = BaudRate;
        if (baud > 0 && baud != _printedBaud)
        {
            chip.Print(string.Create(CultureInfo.InvariantCulture, $"USART1 baud {baud}"));
            _printedBaud = baud;
        }
    }

    /// <summary>A frame's ticks: a start bit, 8 or 9 data bits, the stop bits (CTLR2's STOP: 1, 0.5, 2, 1.5).</summary>
    private long FrameTicks()
    {
        if (_brr == 0)
        {
            return 1;
        }

        var dataBits = (_ctlr1 & (1 << 12)) != 0 ? 9 : 8;
        var stops = ((_ctlr2 >> 12) & 3) switch { 1 => 0.5, 2 => 2, 3 => 1.5, _ => 1 };
        var bitTicks = (double)SimulatedChip.TicksPerSecond * _brr / hclk();
        return Math.Max(1, (long)Math.Round((1 + dataBits + stops) * bitTicks));
    }
}
