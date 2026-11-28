using ChipSharp.Simulators.Devices;

namespace ChipSharp.Simulators.Stm8;

/// <summary>
/// The STM8S's I2C controller (RM0016 chapter 21) as a bus controller, at the STM8S103's addresses, driving an
/// <see cref="I2cBus"/> and its devices: CR1 (PE), CR2 (START, STOP, ACK, POS, SWRST), FREQR, CCRL and CCRH (the clock:
/// fMASTER / (2 * CCR) in standard mode, / (3 * CCR) in fast mode, / (25 * CCR) with DUTY), DR, SR1 (SB, ADDR, BTF, RXNE,
/// TXE), SR2 (AF) and SR3 (MSL, BUSY, TRA). Each step takes its bits' time at the bus's clock: a start a bit, the address
/// and each byte nine (eight and the acknowledgement).
/// <para>
/// As RM0016's sequences have it: START makes a start (or a repeated start) and sets SB; DR written then is the address,
/// which the device answers (ADDR, and TRA for a write) or does not (AF); reading SR1 and then SR3 clears ADDR. As
/// transmitter each byte written to DR goes out once the one before has (TXE again at once), the device takes it or
/// refuses it (AF), and BTF is set when the last is out and DR empty. As receiver the controller takes bytes one after
/// the other from ADDR's clearing on, each acknowledged when ACK is set as it ends (with POS, when ACK was set as the one
/// before ended), and stops taking them after one it did not acknowledge; a byte that comes while DR is full waits
/// behind it (BTF, the clock held low) until DR is read. STOP makes the stop after the byte going on, or at once. A device
/// that holds the clock low never ends its step; SWRST resets the controller, the bus free.
/// </para>
/// </summary>
internal sealed class Stm8I2c(SimulatedChip chip, I2cBus bus, Func<long> ticksPerMasterClock)
{
    private const int Cr1 = 0, Cr2 = 1, Dr = 6, Sr1 = 7, Sr2 = 8, Sr3 = 9, Ccrl = 0xB, Ccrh = 0xC;
    private const byte Pe = 0x01, Start = 0x01, StopBit = 0x02, Ack = 0x04, Pos = 0x08, Swrst = 0x80;
    private const byte Sb = 0x01, Addr = 0x02, Btf = 0x04, Rxne = 0x40, Txe = 0x80, Af = 0x04;
    private const byte Msl = 0x01, Busy = 0x02, Tra = 0x04;

    private enum Step
    {
        None,
        Start,
        Address,
        Transmit,
        Receive,
        Stop,
        Stuck,
    }

    private readonly byte[] _registers = new byte[0x0F];
    private byte _sr1;
    private byte _sr2;
    private byte _sr3;
    private Step _step;
    private long _ticksLeft;
    private byte _shifting;
    private byte? _pending;
    private byte _data;
    private byte? _held;
    private bool _heldAcknowledged;
    private bool _receiving;
    private bool _latchedAck;
    private bool _stopAsked;

    /// <summary>The bus the controller drives: I2C0.</summary>
    public I2cBus Bus { get; } = bus;

    public byte Read(int offset)
    {
        switch (offset)
        {
            case Dr:
                return ReadData();
            case Sr1:
                return _sr1;
            case Sr2:
                return _sr2;
            case Sr3:
                var sr3 = _sr3;
                if ((_sr1 & Addr) != 0)
                {
                    AddressCleared(); // SR1 read, then SR3: ADDR clears
                }

                return sr3;
            default:
                return offset < _registers.Length ? _registers[offset] : (byte)0;
        }
    }

    public void Write(int offset, byte value)
    {
        switch (offset)
        {
            case Cr1:
                _registers[Cr1] = value;
                if ((value & Pe) == 0)
                {
                    Idle();
                }

                return;
            case Cr2:
                WriteControl(value);
                return;
            case Dr:
                WriteData(value);
                return;
            case Sr1:
                return; // read-only
            case Sr2:
                _sr2 &= value; // rc_w0
                return;
            case Sr3:
                return;
        }

        if (offset < _registers.Length)
        {
            _registers[offset] = value;
        }
    }

    /// <summary>Lets <paramref name="ticks"/> pass for the step going on.</summary>
    public void Advance(long ticks)
    {
        if (_step is Step.None or Step.Stuck)
        {
            return;
        }

        _ticksLeft -= ticks;
        while (_step is not (Step.None or Step.Stuck) && _ticksLeft <= 0)
        {
            var end = chip.Ticks;
            chip.SetTime(end + _ticksLeft); // when the step ended, for what the bus prints
            var done = _step;
            _step = Step.None;
            Complete(done);
            chip.SetTime(end);
        }
    }

    /// <summary>The bus's clock as CCR gives it, in hertz.</summary>
    private int Hertz => (int)Math.Round((double)SimulatedChip.TicksPerSecond / ticksPerMasterClock() / PeriodClocks);

    /// <summary>fMASTER clocks a bit takes.</summary>
    private long PeriodClocks
    {
        get
        {
            var ccrh = _registers[Ccrh];
            var ccr = Math.Max(((ccrh & 0x0F) << 8) | _registers[Ccrl], 1);
            return (ccrh & 0x80) == 0 ? 2L * ccr : (ccrh & 0x40) != 0 ? 25L * ccr : 3L * ccr;
        }
    }

    private bool Enabled => (_registers[Cr1] & Pe) != 0;

    private void Begin(Step step, int bits)
    {
        _step = step;
        _ticksLeft += bits * PeriodClocks * ticksPerMasterClock();
        if (_ticksLeft <= 0)
        {
            _ticksLeft = 1;
        }
    }

    private void WriteControl(byte value)
    {
        if ((value & Swrst) != 0)
        {
            // the controller starts again: what it was doing ends, and the bus is free
            if ((_sr3 & Msl) != 0)
            {
                Bus.Stop();
            }

            Array.Clear(_registers);
            _registers[Cr2] = Swrst;
            Idle();
            return;
        }

        var before = _registers[Cr2];
        _registers[Cr2] = (byte)(value & 0x0F);
        if (!Enabled)
        {
            _registers[Cr2] &= unchecked((byte)~(Start | StopBit));
            return;
        }

        if ((value & Start) != 0 && (before & Start) == 0 && _step is Step.None)
        {
            _ticksLeft = 0;
            Begin(Step.Start, 1);
        }

        if ((value & StopBit) != 0 && (before & StopBit) == 0)
        {
            _stopAsked = true;
            if (_step is Step.None)
            {
                _ticksLeft = 0;
                Begin(Step.Stop, 1);
            }
        }
    }

    private void WriteData(byte value)
    {
        if (!Enabled)
        {
            return;
        }

        if ((_sr1 & Sb) != 0)
        {
            // the address and the direction, after SB: SB clears
            _sr1 &= unchecked((byte)~Sb);
            _shifting = value;
            _ticksLeft = 0;
            Begin(Step.Address, 9);
            return;
        }

        if ((_sr3 & (Msl | Tra)) != (Msl | Tra))
        {
            _data = value;
            return;
        }

        _sr1 &= unchecked((byte)~Btf);
        if (_step is Step.None)
        {
            _shifting = value;
            _sr1 |= Txe; // it moves to the shift register at once: DR is empty again
            _ticksLeft = 0;
            Begin(Step.Transmit, 9);
        }
        else
        {
            _pending = value;
            _sr1 &= unchecked((byte)~Txe);
        }
    }

    private byte ReadData()
    {
        var value = _data;
        _sr1 &= unchecked((byte)~Rxne);
        if (_held is { } held)
        {
            // the byte behind it moves into DR, and the clock goes on
            _held = null;
            _data = held;
            _sr1 = (byte)((_sr1 | Rxne) & ~Btf);
            if (_heldAcknowledged)
            {
                ReceiveNext();
            }
            else if (_stopAsked && _step is Step.None)
            {
                _ticksLeft = 0;
                Begin(Step.Stop, 1);
            }
        }

        return value;
    }

    // ADDR cleared: a transmitter's DR is empty; a receiver takes its first byte
    private void AddressCleared()
    {
        _sr1 &= unchecked((byte)~Addr);
        if ((_sr3 & Tra) != 0)
        {
            _sr1 |= Txe;
            return;
        }

        _receiving = true;
        _latchedAck = (_registers[Cr2] & Ack) != 0;
        ReceiveNext();
    }

    private void ReceiveNext()
    {
        if (_stopAsked || !_receiving)
        {
            if (_step is Step.None)
            {
                _ticksLeft = 0;
                Begin(Step.Stop, 1);
            }

            return;
        }

        _ticksLeft = 0;
        Begin(Step.Receive, 9);
    }

    private void Complete(Step step)
    {
        switch (step)
        {
            case Step.Start:
                _registers[Cr2] &= unchecked((byte)~Start);
                _sr1 = Sb;
                _sr3 = Msl | Busy;
                _receiving = false;
                _held = null;
                Bus.Start();
                break;
            case Step.Address:
                var read = (_shifting & 1) != 0;
                switch (Bus.Connect(_shifting >> 1, read, Hertz))
                {
                    case null:
                        _step = Step.Stuck; // the device holds the clock low
                        return;
                    case true:
                        _sr1 |= Addr;
                        _sr3 = (byte)(Msl | Busy | (read ? 0 : Tra));
                        break;
                    default:
                        _sr2 |= Af;
                        break;
                }

                break;
            case Step.Transmit:
                if (!Bus.Write(_shifting))
                {
                    _sr2 |= Af;
                    _pending = null;
                }
                else if (_pending is { } next)
                {
                    _pending = null;
                    _shifting = next;
                    _sr1 |= Txe;
                    Begin(Step.Transmit, 9);
                    return;
                }
                else
                {
                    _sr1 |= Txe | Btf;
                }

                break;
            case Step.Receive:
                var value = Bus.Read();
                var acknowledged = (_registers[Cr2] & Pos) != 0 ? _latchedAck : (_registers[Cr2] & Ack) != 0;
                _latchedAck = (_registers[Cr2] & Ack) != 0;
                _receiving = acknowledged;
                if ((_sr1 & Rxne) == 0)
                {
                    _data = value;
                    _sr1 |= Rxne;
                    if (acknowledged && !_stopAsked)
                    {
                        Begin(Step.Receive, 9);
                        return;
                    }
                }
                else
                {
                    _held = value;
                    _heldAcknowledged = acknowledged;
                    _sr1 |= Btf;
                    return; // the clock waits for DR to be read
                }

                break;
            case Step.Stop:
                _registers[Cr2] &= unchecked((byte)~(StopBit | Start | Pos));
                _sr1 &= Rxne;
                _sr3 = 0;
                _stopAsked = false;
                _receiving = false;
                _pending = null;
                Bus.Stop();
                return;
        }

        // the stop asked for, once nothing else goes on
        if (_stopAsked && _step is Step.None && _held is null)
        {
            Begin(Step.Stop, 1);
        }
    }

    private void Idle()
    {
        _step = Step.None;
        _ticksLeft = 0;
        _sr1 = 0;
        _sr2 = 0;
        _sr3 = 0;
        _pending = null;
        _held = null;
        _receiving = false;
        _stopAsked = false;
    }
}
