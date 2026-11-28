using ChipSharp.Simulators.Devices;

namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// The ESP32-C5's I2C controller (TRM chapter 27; registers by the SVD, behaviour as ESP-IDF's i2c_ll.h drives it) as a
/// controller (master) at 0x6000_4000, driving an <see cref="I2cBus"/> and its devices. A list of up to 8 commands in
/// COMD0-7 runs when CTR's TRANS_START is written (CONF_UPGATE and TRANS_START read 0 again at once): RESTART (a start,
/// or a repeated start), WRITE (n bytes from the send FIFO, the first after a RESTART being the address byte, whose
/// device answers or does not: NACK when the command checks the acknowledgement), READ (n bytes into the receive FIFO),
/// STOP, END (the controller waits there, the clock held low, for the next list). The list runs against the bus when it
/// starts and its time is the bytes' (nine clocks each, the clock from the crystal over the divider of
/// PCR_I2C_SCLK_CONF, and SCL_LOW_PERIOD, SCL_HIGH_PERIOD): INT_RAW shows its end (TRANS_COMPLETE after a STOP, END_DETECT
/// after an END), a refused address or byte (NACK, the list stops there), or a device that holds the clock low (the
/// TIME_OUT of the TO register's 2^n source clocks, no end). FSM_RST resets the state machine (and stops a transfer on the
/// bus); a FIFO's reset empties it.
/// </summary>
internal sealed class Esp32c5I2c(SimulatedChip chip, I2cBus bus, Func<uint> clockConfiguration)
{
    private const uint SclLow = 0x00, Ctr = 0x04, Sr = 0x08, To = 0x0C, FifoConf = 0x18, Data = 0x1C, IntRaw = 0x20,
        IntClr = 0x24, IntEna = 0x28, IntStatus = 0x2C, SclHigh = 0x38, Comd0 = 0x58;
    private const uint TransStart = 1 << 5, FsmRst = 1 << 10, ConfUpgate = 1 << 11, RxReset = 1 << 12, TxReset = 1 << 13;
    private const uint EndDetect = 1 << 3, TransComplete = 1 << 7, TimeOut = 1 << 8, Nack = 1 << 10, FifoUnderflow = 1 << 6;
    private const int FifoSize = 32;

    private readonly Dictionary<uint, uint> _registers = [];
    private readonly Queue<byte> _send = new();
    private readonly Queue<byte> _receive = new();
    private uint _raw;
    private uint _pending;
    private long _doneAt;
    private bool _needAddress = true;
    private bool _open;
    private bool _stuck;

    /// <summary>The bus the controller drives: I2C0.</summary>
    public I2cBus Bus { get; } = bus;

    public uint Read(uint offset)
    {
        Settle();
        switch (offset)
        {
            case Data:
                return _receive.Count > 0 ? _receive.Dequeue() : 0u;
            case IntRaw:
                return _raw;
            case IntStatus:
                return _raw & _registers.GetValueOrDefault(IntEna);
            case Sr:
                return ((uint)_receive.Count << 8) | ((uint)_send.Count << 18) | (chip.Ticks < _doneAt ? 1u << 4 : 0u);
            case Ctr:
                return _registers.GetValueOrDefault(Ctr) & ~(TransStart | ConfUpgate | FsmRst);
            default:
                return _registers.GetValueOrDefault(offset);
        }
    }

    public void Write(uint offset, uint value)
    {
        Settle();
        switch (offset)
        {
            case Data:
                if (_send.Count < FifoSize)
                {
                    _send.Enqueue((byte)value);
                }

                return;
            case IntClr:
                _raw &= ~value;
                return;
            case FifoConf:
                if ((value & TxReset) != 0)
                {
                    _send.Clear();
                }

                if ((value & RxReset) != 0)
                {
                    _receive.Clear();
                }

                _registers[offset] = value;
                return;
            case Ctr:
                _registers[offset] = value & ~(TransStart | ConfUpgate | FsmRst);
                if ((value & FsmRst) != 0)
                {
                    Reset();
                }

                if ((value & TransStart) != 0)
                {
                    Start();
                }

                return;
            case >= Comd0 and < Comd0 + 4 * 8:
                _registers[offset] = value & 0x3FFF; // the command's done bit is not modelled
                return;
            default:
                _registers[offset] = value;
                return;
        }
    }

    /// <summary>What the controller's time brings: the end of the list that ran.</summary>
    private void Settle()
    {
        if (_pending != 0 && chip.Ticks >= _doneAt)
        {
            _raw |= _pending;
            _pending = 0;
        }
    }

    /// <summary>The state machine resets: a transfer on the bus ends, the list in progress is forgotten.</summary>
    private void Reset()
    {
        if (_open)
        {
            Bus.Stop();
            _open = false;
        }

        _pending = 0;
        _needAddress = true;
    }

    /// <summary>The controller's clock in hertz: the crystal (or RC_FAST) over the divider, from PCR_I2C_SCLK_CONF.</summary>
    private double SourceHertz
    {
        get
        {
            var configuration = clockConfiguration();
            var source = (configuration & (1u << 20)) != 0 ? Esp32c5Map.RcFastHz : Esp32c5Map.XtalHz;
            return (double)source / (((configuration >> 12) & 0xFF) + 1);
        }
    }

    private void Start()
    {
        var source = SourceHertz;
        var cycles = (_registers.GetValueOrDefault(SclLow) & 0x1FF) + 1u + (_registers.GetValueOrDefault(SclHigh) & 0x1FF)
            + ((_registers.GetValueOrDefault(SclHigh) >> 9) & 0x7F);
        var hertz = (int)Math.Round(source / cycles);
        var bitTicks = cycles * SimulatedChip.TicksPerSecond / source;
        double clocks = 0;
        uint events = 0;
        for (uint index = 0; index < 8 && events == 0; index++)
        {
            var command = _registers.GetValueOrDefault(Comd0 + 4 * index);
            var count = (int)(command & 0xFF);
            var checkAcknowledge = (command & 0x100) != 0;
            switch ((command >> 11) & 7)
            {
                case 6: // RESTART
                    _needAddress = true;
                    clocks += 1;
                    break;
                case 1: // WRITE
                    events = Write(count, checkAcknowledge, hertz, ref clocks, source, bitTicks);
                    break;
                case 3: // READ
                    for (var i = 0; i < count; i++)
                    {
                        _receive.Enqueue(_open ? Bus.Read() : (byte)0xFF);
                        clocks += 1;
                    }

                    break;
                case 2: // STOP
                    if (_open)
                    {
                        Bus.Stop();
                        _open = false;
                    }

                    _needAddress = true;
                    clocks += 1;
                    events = TransComplete;
                    break;
                case 4: // END
                    events = EndDetect;
                    break;
                default:
                    events = TransComplete; // no more commands
                    break;
            }
        }

        if (events == 0)
        {
            events = TransComplete;
        }

        // the list ends once its clocks have passed (a byte is nine), or, for a device that holds the clock, at the time-out
        if (_stuck)
        {
            _stuck = false;
            _doneAt = chip.Ticks + (long)(Math.Pow(2, _registers.GetValueOrDefault(To) & 0x1F) * SimulatedChip.TicksPerSecond / source);
            _pending |= TimeOut;
            return;
        }

        _doneAt = chip.Ticks + (long)(clocks * bitTicks);
        _pending |= events;
    }

    /// <summary>A WRITE command: <paramref name="count"/> bytes from the send FIFO; the events that end the list, or 0 to go on.</summary>
    private uint Write(int count, bool checkAcknowledge, int hertz, ref double clocks, double source, double bitTicks)
    {
        for (var i = 0; i < count; i++)
        {
            if (_send.Count == 0)
            {
                clocks += 1;
                return FifoUnderflow;
            }

            var value = _send.Dequeue();
            clocks += 9;
            if (_needAddress)
            {
                _needAddress = false;
                var answered = Bus.Connect(value >> 1, (value & 1) != 0, hertz);
                _open = true;
                if (answered is null)
                {
                    // a device holding the clock low: the controller's time-out, 2^TO source clocks, ends nothing
                    _stuck = true;
                    return TimeOut;
                }

                if (answered == false)
                {
                    return Nack; // the list stops at the address nothing answered
                }
            }
            else if (!Bus.Write(value) && checkAcknowledge)
            {
                return Nack;
            }
        }

        return 0;
    }
}
