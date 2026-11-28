using System.Globalization;

namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// UART0 (TRM v1.1, chapter UART Controller; registers by the SVD), asynchronous, without flow control: the TX FIFO (128
/// bytes; a write of FIFO adds a byte) goes out a frame at a time (a start bit, CONF0's BIT_NUM data bits, a parity bit
/// with PARITY_EN, STOP_BIT_NUM's stop bits) at the baud rate: the function clock (PCR_UART0_SCLK_CONF: XTAL, RC_FAST or
/// PLL_F80M, divided by DIV_NUM + DIV_B / DIV_A + 1) over CLKDIV + CLKDIV_FRAG / 16. Each byte is what the serial output
/// (<see cref="SerialMonitor"/>) prints. --send's bytes come into the RX FIFO (128 bytes; past that RXFIFO_OVF, and the
/// byte is lost), which a read of FIFO takes from. STATUS has both FIFOs' counts. The _SYNC registers (CLKDIV_SYNC,
/// CONF0_SYNC, TOUT_CONF_SYNC) take effect when REG_UPDATE is written (it clears itself), as on the chip. Interrupts
/// (INT_RAW, INT_ENA, INT_ST, INT_CLR): RXFIFO_FULL (the RX FIFO holds RXFIFO_FULL_THRHD bytes or more), TXFIFO_EMPTY
/// (the TX FIFO holds fewer than TXFIFO_EMPTY_THRHD), RXFIFO_OVF, RXFIFO_TOUT (with RX_TOUT_EN, the RX line idle for
/// RX_TOUT_THRHD bit times with bytes in the FIFO) and TX_DONE; INT_ST is the interrupt source UART0 (47). The baud rate
/// is printed when a byte goes out or comes in at another than the last printed ("UART0 baud 115200"). After reset (and the ROM, which prints at 115200) the
/// clock is XTAL and the divider 416 + 11/16.
/// </summary>
internal sealed class Esp32c5Uart(SimulatedChip chip, string name, Func<uint> clockConfiguration) : ISerialInput
{
    private const int FifoSize = 128;
    private const uint RxFull = 1 << 0, TxEmpty = 1 << 1, RxOverflow = 1 << 4, RxTimeout = 1 << 8, TxDone = 1 << 14;

    private readonly Queue<byte> _tx = new();
    private readonly Queue<byte> _rx = new();
    private uint _clkdiv = 416 | (11u << 20);
    private uint _clkdivActive = 416 | (11u << 20);
    private uint _conf0 = 0x1C;
    private uint _conf0Active = 0x1C;
    private uint _conf1 = 0x6060;
    private uint _tout = 0x28;
    private uint _toutActive = 0x28;
    private uint _raw = TxEmpty;
    private uint _enable;
    private bool _shifting;
    private long _frameEnd = long.MaxValue;
    private long _timeoutAt = long.MaxValue;
    private int _printedBaud;

    private readonly Dictionary<uint, uint> _other = [];

    public string Name { get; } = name;

    /// <summary>What the port writes.</summary>
    public SerialMonitor Output { get; } = new(chip, name);

    /// <summary>Called when a byte arrived.</summary>
    public Action? Changed { get; set; }

    /// <summary>The tick the last byte of the TX FIFO will have gone out, or long.MaxValue when nothing is going out.</summary>
    public long IdleTick => _shifting ? _frameEnd + (_tx.Count - 1) * FrameTicks() : long.MaxValue;

    public int BaudRate => (int)Math.Round(Baud);

    /// <summary>The baud rate the registers give (0 without a clock).</summary>
    public double Baud
    {
        get
        {
            var divider = (_clkdivActive & 0xFFF) + ((_clkdivActive >> 20) & 0xF) / 16.0;
            return divider < 1 ? 0 : FunctionClock / divider;
        }
    }

    /// <summary>The function clock, in hertz, from PCR_UART0_SCLK_CONF.</summary>
    private double FunctionClock
    {
        get
        {
            var conf = clockConfiguration();
            if ((conf & (1u << 22)) == 0)
            {
                return 0;
            }

            double source = ((conf >> 20) & 3) switch
            {
                0 => Esp32c5Map.XtalHz,
                1 => Esp32c5Map.RcFastHz,
                2 => Esp32c5Map.Pll80Hz,
                _ => 0,
            };
            var a = conf & 0x3F;
            var b = (conf >> 6) & 0x3F;
            return source / (((conf >> 12) & 0xFF) + 1 + (a == 0 ? 0 : (double)b / a));
        }
    }

    /// <summary>The bytes in the TX FIFO, the one going out not counted (it is in the shift register).</summary>
    private int TxFifoCount => _tx.Count - (_shifting ? 1 : 0);

    /// <summary>The interrupt source UART0: INT_ST.</summary>
    public bool InterruptPending => (_raw & _enable) != 0;

    /// <summary>Whether its interrupts could follow from bytes to come (RX).</summary>
    public bool ReceiveInterrupts => (_enable & (RxFull | RxOverflow | RxTimeout)) != 0;

    /// <summary>The tick of its next event (the frame going out ends, the RX timeout), or long.MaxValue.</summary>
    public long NextEvent => Math.Min(_frameEnd, _timeoutAt);

    /// <summary>A byte's stop bit has arrived on RX.</summary>
    public void Receive(byte value)
    {
        if (_rx.Count >= FifoSize)
        {
            _raw |= RxOverflow;
        }
        else
        {
            _rx.Enqueue(value);
        }

        PrintBaud();
        var bit = BitTicks();
        _timeoutAt = (_toutActive & 1) != 0 && bit > 0
            ? chip.Ticks + (long)(Math.Max((_toutActive >> 2) & 0x3FF, 1) * bit)
            : long.MaxValue;
        UpdateLevels();
        Changed?.Invoke();
    }

    /// <summary>Lets the frame going out end, and the RX timeout come, when they are due.</summary>
    public void Advance()
    {
        while (_frameEnd <= chip.Ticks)
        {
            var end = chip.Ticks;
            chip.SetTime(_frameEnd);
            Output.Write(_tx.Dequeue());
            chip.SetTime(end);
            _shifting = false;
            var finished = _frameEnd;
            _frameEnd = long.MaxValue;
            if (_tx.Count > 0)
            {
                StartFrame(finished);
            }
            else
            {
                _raw |= TxDone;
            }
        }

        if (_timeoutAt <= chip.Ticks)
        {
            _timeoutAt = long.MaxValue;
            if (_rx.Count > 0)
            {
                _raw |= RxTimeout;
            }
        }

        UpdateLevels();
    }

    public uint Read(uint offset)
    {
        switch (offset)
        {
            case 0x00:
                var value = _rx.Count > 0 ? _rx.Dequeue() : 0u;
                UpdateLevels();
                return value;
            case 0x04:
                return _raw;
            case 0x08:
                return _raw & _enable;
            case 0x0C:
                return _enable;
            case 0x14:
                return _clkdiv;
            case 0x1C: // STATUS: RXD and TXD idle high
                return (uint)Math.Min(_rx.Count, 255) | ((uint)Math.Min(TxFifoCount, 255) << 16) | 0xE000_C000;
            case 0x20:
                return _conf0;
            case 0x24:
                return _conf1;
            case 0x64:
                return _tout;
            case 0x70: // FSM_STATUS: the transmitter's state, 0 when idle
                return _shifting ? 0x20u : 0;
            case 0x88:
                return _other.GetValueOrDefault(offset, 0x0300_0000u);
            case 0x8C:
                return 0x0231_2220;
            case 0x90: // AFIFO_STATUS: the asynchronous FIFOs are empty
                return 0xA;
            case 0x98:
                return 0; // REG_UPDATE clears itself once the registers are synchronized
            case 0x9C:
                return _other.GetValueOrDefault(offset, 0x500u);
            default:
                return _other.GetValueOrDefault(offset);
        }
    }

    public void Write(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x00:
                if (TxFifoCount < FifoSize && (_conf0Active & (1u << 23)) == 0)
                {
                    _tx.Enqueue((byte)value);
                    if (!_shifting)
                    {
                        StartFrame(chip.Ticks);
                    }
                }

                break;
            case 0x04:
                _raw = value & 0xFFFFF;
                break;
            case 0x0C:
                _enable = value & 0xFFFFF;
                break;
            case 0x10:
                _raw &= ~value;
                break;
            case 0x14:
                _clkdiv = value & 0x00F0_0FFF;
                break;
            case 0x20:
                _conf0 = value;
                break;
            case 0x24:
                _conf1 = value;
                break;
            case 0x64:
                _tout = value & 0xFFF;
                break;
            case 0x98:
                if ((value & 1) != 0)
                {
                    Synchronize();
                }

                break;
            default:
                _other[offset] = value;
                break;
        }

        UpdateLevels();
    }


    private void Synchronize()
    {
        _clkdivActive = _clkdiv;
        _toutActive = _tout;
        _conf0Active = _conf0;
        if ((_conf0 & (1u << 22)) != 0)
        {
            _rx.Clear(); // RXFIFO_RST
        }

        if ((_conf0 & (1u << 23)) != 0 && !_shifting)
        {
            _tx.Clear(); // TXFIFO_RST
        }
    }

    /// <summary>Prints the baud rate when a byte goes out or comes in at another than the last printed.</summary>
    private void PrintBaud()
    {
        var baud = BaudRate;
        if (baud > 0 && baud != _printedBaud)
        {
            chip.Print(string.Create(CultureInfo.InvariantCulture, $"{Name} baud {baud}"));
            _printedBaud = baud;
        }
    }

    /// <summary>A bit time, in ticks; 0 without a clock.</summary>
    private double BitTicks()
    {
        var baud = Baud;
        return baud > 0 ? SimulatedChip.TicksPerSecond / baud : 0;
    }

    /// <summary>A frame's ticks: a start bit, the data bits, the parity bit, the stop bits.</summary>
    private long FrameTicks()
    {
        var dataBits = 5 + (int)((_conf0Active >> 2) & 3);
        var parity = (_conf0Active & 2) != 0 ? 1 : 0;
        var halfStops = ((_conf0Active >> 4) & 3) switch { 2 => 3, 3 => 4, _ => 2 };
        return Math.Max(1, (long)Math.Round((1 + dataBits + parity + halfStops / 2.0) * BitTicks()));
    }

    private void StartFrame(long from)
    {
        if (BitTicks() <= 0 || _tx.Count == 0)
        {
            return; // no clock: the byte waits
        }

        PrintBaud();
        _shifting = true;
        _frameEnd = from + FrameTicks();
        _raw &= ~TxDone;
    }

    private void UpdateLevels()
    {
        var full = Math.Max(_conf1 & 0xFF, 1);
        var empty = (_conf1 >> 8) & 0xFF;
        if (_rx.Count >= full)
        {
            _raw |= RxFull;
        }

        if (TxFifoCount < empty)
        {
            _raw |= TxEmpty;
        }
    }
}
