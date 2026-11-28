namespace ChipSharp.Simulators.Stm8;

/// <summary>
/// TIM4, the STM8S's 8-bit basic timer (RM0016 chapter 19), at the STM8S103's addresses: CR1 (CEN, UDIS, URS, OPM,
/// ARPE), IER (UIE), SR (UIF), EGR (UG), CNTR, PSCR (2^0 to 2^7, loaded at the update event) and ARR (preloaded with
/// ARPE). It counts fMASTER divided by the prescaler from 0 to ARR, where the update event sets UIF (its interrupt,
/// vector 23) and the counter starts again at 0.
/// </summary>
internal sealed class Tim4
{
    private const int Cr1 = 0, Ier = 3, Sr = 4, Egr = 5, Cntr = 6, Pscr = 7, Arr = 8;

    private byte _cr1;
    private byte _ier;
    private byte _sr;
    private byte _counter;
    private byte _pscr;
    private byte _arr = 0xFF;
    private int _activePrescaler;
    private byte _activeArr = 0xFF;
    private long _clocks;

    /// <summary>Whether the update interrupt is requested and enabled.</summary>
    public bool InterruptPending => (_sr & _ier & 1) != 0;

    /// <summary>Whether the timer counts and would request its interrupt.</summary>
    public bool CanInterrupt => (_cr1 & 1) != 0 && (_ier & 1) != 0;

    public byte Read(int offset) => offset switch
    {
        Cr1 => _cr1,
        Ier => _ier,
        Sr => _sr,
        Cntr => _counter,
        Pscr => _pscr,
        Arr => _arr,
        _ => 0,
    };

    public void Write(int offset, byte value)
    {
        switch (offset)
        {
            case Cr1:
                _cr1 = (byte)(value & 0x8F);
                break;
            case Ier:
                _ier = (byte)(value & 1);
                break;
            case Sr:
                _sr &= value; // rc_w0: writing 0 clears
                break;
            case Egr when (value & 1) != 0: // UG: the counter and the prescaler start again, the registers are loaded
                _counter = 0;
                _clocks = 0;
                _activePrescaler = _pscr & 7;
                _activeArr = _arr;
                if ((_cr1 & 0x04) == 0)
                {
                    _sr |= 1;
                }

                break;
            case Cntr:
                _counter = value;
                break;
            case Pscr:
                _pscr = (byte)(value & 7);
                break;
            case Arr:
                _arr = value;
                if ((_cr1 & 0x80) == 0)
                {
                    _activeArr = value;
                }

                break;
        }
    }

    /// <summary>Lets <paramref name="masterClocks"/> fMASTER clocks pass.</summary>
    public void Advance(long masterClocks)
    {
        if ((_cr1 & 1) == 0)
        {
            return;
        }

        _clocks += masterClocks;
        var perCount = 1L << _activePrescaler;
        while (_clocks >= perCount && (_cr1 & 1) != 0)
        {
            _clocks -= perCount;
            if (_counter == _activeArr)
            {
                _counter = 0;
                if ((_cr1 & 0x02) == 0) // UDIS
                {
                    _activePrescaler = _pscr & 7;
                    _activeArr = _arr;
                    _sr |= 1;
                    perCount = 1L << _activePrescaler;
                }

                if ((_cr1 & 0x08) != 0) // OPM: one pulse
                {
                    _cr1 &= 0xFE;
                }
            }
            else
            {
                _counter++;
            }
        }
    }

    /// <summary>The fMASTER clocks until the next update event, or long.MaxValue when the timer does not count.</summary>
    public long ClocksToUpdate() =>
        (_cr1 & 1) == 0 ? long.MaxValue : ((_activeArr - _counter) + 1L) * (1L << _activePrescaler) - _clocks;
}

/// <summary>
/// TIM1 (the 16-bit advanced timer, RM0016 chapter 17) or TIM2 (the 16-bit general purpose one, chapter 18), as the
/// STM8S103 has them: the counter, the prescaler (TIM2: 2^0 to 2^15; TIM1: 1 to 65536, PSCRH:PSCRL + 1), the auto-reload
/// (preloaded with ARPE), TIM1's repetition counter and its counting down (DIR; the center-aligned modes count up), the
/// update event and its interrupt (UIF: vector 11 for TIM1, 13 for TIM2), and the capture/compare channels as outputs
/// (TIM1: 4 on PC6, PC7, PC3, PC4; TIM2: 3 on PD4, PD3, PA3): each sets CCiIF when the counter reaches CCRi (their
/// interrupt: vector 12, 14), and drives its pin when CCiE is on (TIM1: with BKR's MOE too) in the output compare mode
/// OCiM selects (frozen, active or inactive on match, toggle, forced, PWM mode 1 or 2), CCiP inverting it; the pin is
/// measured as PWM. Input capture, the slave modes, the complementary outputs, dead time and break are not there.
/// The 16-bit registers are read and written as RM0016 says: the high byte first, CNTRL latched by reading CNTRH, a
/// write of the high byte held until the low byte is written.
/// </summary>
internal sealed class Stm8Timer16
{
    private readonly SimulatedChip _chip;
    private readonly bool _advanced;
    private readonly (GpioPort Port, int Bit)[] _pins;
    private readonly Func<long> _ticksPerMasterClock;

    // offsets (TIM1, TIM2)
    private readonly int _ier, _sr1, _sr2, _egr, _ccmr1, _ccer1, _ccer2, _cntrh, _pscr, _arrh, _ccr1h;

    private readonly byte[] _ccmr = new byte[4];
    private readonly ushort[] _ccr = new ushort[4];
    private readonly ushort[] _activeCcr = new ushort[4];
    private readonly bool?[] _driven = new bool?[4];
    private readonly bool[] _reference = new bool[4];
    private byte _cr1;
    private byte _ierValue;
    private byte _sr1Value;
    private byte _sr2Value;
    private byte _ccer1Value;
    private byte _ccer2Value;
    private byte _bkr;
    private byte _rcr;
    private int _repetitions;
    private ushort _counter;
    private ushort _prescaler;
    private ushort _arr = 0xFFFF;
    private int _activePrescaler;
    private ushort _activeArr = 0xFFFF;
    private long _clocks;
    private byte _writeLatch;
    private byte _readLatch;
    private bool _readLatched;
    private readonly byte[] _other = new byte[0x20];

    private Stm8Timer16(SimulatedChip chip, bool advanced, (GpioPort, int)[] pins, Func<long> ticksPerMasterClock)
    {
        _chip = chip;
        _advanced = advanced;
        _pins = pins;
        _ticksPerMasterClock = ticksPerMasterClock;
        if (advanced)
        {
            (_ier, _sr1, _sr2, _egr, _ccmr1, _ccer1, _ccer2, _cntrh, _pscr, _arrh, _ccr1h) =
                (4, 5, 6, 7, 8, 0xC, 0xD, 0xE, 0x10, 0x12, 0x15);
        }
        else
        {
            (_ier, _sr1, _sr2, _egr, _ccmr1, _ccer1, _ccer2, _cntrh, _pscr, _arrh, _ccr1h) =
                (3, 4, 5, 6, 7, 0xA, 0xB, 0xC, 0xE, 0xF, 0x11);
        }
    }

    /// <summary>TIM1, its channels on <paramref name="pins"/>.</summary>
    public static Stm8Timer16 Tim1(SimulatedChip chip, (GpioPort, int)[] pins, Func<long> ticksPerMasterClock) =>
        new(chip, true, pins, ticksPerMasterClock);

    /// <summary>TIM2, its channels on <paramref name="pins"/>.</summary>
    public static Stm8Timer16 Tim2(SimulatedChip chip, (GpioPort, int)[] pins, Func<long> ticksPerMasterClock) =>
        new(chip, false, pins, ticksPerMasterClock);

    /// <summary>Whether the update interrupt is requested and enabled.</summary>
    public bool UpdatePending => (_sr1Value & _ierValue & 1) != 0;

    /// <summary>Whether a capture/compare interrupt is requested and enabled.</summary>
    public bool ComparePending => (_sr1Value & _ierValue & 0x1E) != 0;

    /// <summary>Whether the timer counts and would request an interrupt.</summary>
    public bool CanInterrupt => (_cr1 & 1) != 0 && (_ierValue & 0x1F) != 0;

    /// <summary>Whether the timer counts and drives a pin (whose edges a sleeping CPU must not skip).</summary>
    public bool DrivesPins => (_cr1 & 1) != 0 && _driven.Any(level => level is not null);

    private int Channels => _pins.Length;

    public byte Read(int offset)
    {
        if (offset == 0)
        {
            return _cr1;
        }

        if (offset == _ier)
        {
            return _ierValue;
        }

        if (offset == _sr1)
        {
            return _sr1Value;
        }

        if (offset == _sr2)
        {
            return _sr2Value;
        }

        if (offset == _ccer1)
        {
            return _ccer1Value;
        }

        if (offset == _ccer2)
        {
            return _ccer2Value;
        }

        if (offset >= _ccmr1 && offset < _ccmr1 + Channels)
        {
            return _ccmr[offset - _ccmr1];
        }

        if (offset == _cntrh)
        {
            _readLatch = (byte)_counter;
            _readLatched = true;
            return (byte)(_counter >> 8);
        }

        if (offset == _cntrh + 1)
        {
            var low = _readLatched ? _readLatch : (byte)_counter;
            _readLatched = false;
            return low;
        }

        if (_advanced && offset == _pscr)
        {
            return (byte)(_prescaler >> 8);
        }

        if (offset == (_advanced ? _pscr + 1 : _pscr))
        {
            return (byte)_prescaler;
        }

        if (offset == _arrh)
        {
            return (byte)(_arr >> 8);
        }

        if (offset == _arrh + 1)
        {
            return (byte)_arr;
        }

        if (offset >= _ccr1h && offset < _ccr1h + 2 * Channels)
        {
            var ccr = _ccr[(offset - _ccr1h) / 2];
            return (offset - _ccr1h) % 2 == 0 ? (byte)(ccr >> 8) : (byte)ccr;
        }

        if (_advanced && offset == 0x14)
        {
            return _rcr;
        }

        if (_advanced && offset == 0x1D)
        {
            return _bkr;
        }

        return offset < _other.Length ? _other[offset] : (byte)0;
    }

    public void Write(int offset, byte value)
    {
        if (offset == 0)
        {
            _cr1 = value;
        }
        else if (offset == _ier)
        {
            _ierValue = value;
        }
        else if (offset == _sr1)
        {
            _sr1Value &= value;
        }
        else if (offset == _sr2)
        {
            _sr2Value &= value;
        }
        else if (offset == _egr)
        {
            if ((value & 1) != 0)
            {
                Update(generated: true);
            }

            _sr1Value |= (byte)(value & 0x1E); // CCiG: a capture/compare event
        }
        else if (offset >= _ccmr1 && offset < _ccmr1 + Channels)
        {
            _ccmr[offset - _ccmr1] = value;
            DrivePins();
        }
        else if (offset == _ccer1)
        {
            _ccer1Value = value;
            DrivePins();
        }
        else if (offset == _ccer2)
        {
            _ccer2Value = value;
            DrivePins();
        }
        else if (offset == _cntrh || (_advanced && offset == _pscr) || offset == _arrh ||
                 (offset >= _ccr1h && offset < _ccr1h + 2 * Channels && (offset - _ccr1h) % 2 == 0))
        {
            _writeLatch = value; // the high byte, written with the low one
        }
        else if (offset == _cntrh + 1)
        {
            _counter = (ushort)((_writeLatch << 8) | value);
            DrivePins();
        }
        else if (offset == (_advanced ? _pscr + 1 : _pscr))
        {
            _prescaler = _advanced ? (ushort)((_writeLatch << 8) | value) : (ushort)(value & 0x0F);
        }
        else if (offset == _arrh + 1)
        {
            _arr = (ushort)((_writeLatch << 8) | value);
            if ((_cr1 & 0x80) == 0)
            {
                _activeArr = _arr;
            }
        }
        else if (offset >= _ccr1h && offset < _ccr1h + 2 * Channels)
        {
            var channel = (offset - _ccr1h) / 2;
            _ccr[channel] = (ushort)((_writeLatch << 8) | value);
            if ((_ccmr[channel] & 0x08) == 0) // OCiPE: preload
            {
                _activeCcr[channel] = _ccr[channel];
            }

            DrivePins();
        }
        else if (_advanced && offset == 0x14)
        {
            _rcr = value;
        }
        else if (_advanced && offset == 0x1D)
        {
            _bkr = value;
            DrivePins();
        }
        else if (offset < _other.Length)
        {
            _other[offset] = value;
        }
    }

    /// <summary>Lets <paramref name="masterClocks"/> fMASTER clocks pass.</summary>
    public void Advance(long masterClocks)
    {
        if ((_cr1 & 1) == 0)
        {
            return;
        }

        _clocks += masterClocks;
        var perCount = PerCount();
        var end = _chip.Ticks;
        while (_clocks >= perCount && (_cr1 & 1) != 0)
        {
            _clocks -= perCount;
            _chip.SetTime(end - _clocks * _ticksPerMasterClock()); // the count's own tick, for the pins' edges
            Count();
            perCount = PerCount();
        }

        _chip.SetTime(end);
    }

    /// <summary>The fMASTER clocks until the counter next does something that can raise an interrupt, or long.MaxValue.</summary>
    public long ClocksToEvent()
    {
        if ((_cr1 & 1) == 0)
        {
            return long.MaxValue;
        }

        var down = Down;
        var counts = down ? _counter + 1L : _activeArr - _counter + 1L;
        for (var channel = 0; channel < Channels; channel++)
        {
            var ccr = _activeCcr[channel];
            if (ccr <= _activeArr)
            {
                var distance = down ? _counter - ccr : ccr - _counter;
                if (distance > 0)
                {
                    counts = Math.Min(counts, distance);
                }
            }
        }

        return counts * PerCount() - _clocks;
    }

    private bool Down => _advanced && (_cr1 & 0x70) == 0x10; // DIR, edge-aligned

    private long PerCount() => _advanced ? _activePrescaler + 1L : 1L << _activePrescaler;

    private void Count()
    {
        var overflow = false;
        if (Down)
        {
            if (_counter == 0)
            {
                _counter = _activeArr;
                overflow = true;
            }
            else
            {
                _counter--;
            }
        }
        else if (_counter >= _activeArr)
        {
            _counter = 0;
            overflow = true;
        }
        else
        {
            _counter++;
        }

        if (overflow)
        {
            if (!_advanced || _repetitions == 0)
            {
                _repetitions = _rcr;
                Update(generated: false);
            }
            else
            {
                _repetitions--;
            }
        }

        for (var channel = 0; channel < Channels; channel++)
        {
            if (_counter == _activeCcr[channel])
            {
                _sr1Value |= (byte)(2 << channel);
                var mode = (_ccmr[channel] >> 4) & 7;
                _reference[channel] = mode switch
                {
                    1 => true,
                    2 => false,
                    3 => !_reference[channel],
                    _ => _reference[channel],
                };
            }
        }

        DrivePins();
    }

    /// <summary>The update event (an overflow, or UG): the preloaded registers are loaded, UIF set (unless UDIS, or URS for UG).</summary>
    private void Update(bool generated)
    {
        if (generated)
        {
            _counter = Down ? _activeArr : (ushort)0;
            _clocks = 0;
        }

        if ((_cr1 & 0x02) != 0 && !generated) // UDIS
        {
            return;
        }

        _activePrescaler = _prescaler;
        _activeArr = _arr;
        for (var channel = 0; channel < Channels; channel++)
        {
            _activeCcr[channel] = _ccr[channel];
        }

        if (!generated || (_cr1 & 0x04) == 0)
        {
            _sr1Value |= 1;
        }

        if (!generated && (_cr1 & 0x08) != 0) // OPM
        {
            _cr1 &= 0xFE;
        }
    }

    /// <summary>Drives the pins of the channels whose output is enabled, at their output compare level.</summary>
    private void DrivePins()
    {
        for (var channel = 0; channel < Channels; channel++)
        {
            var ccer = channel < 2 ? _ccer1Value : _ccer2Value;
            var shift = 4 * (channel % 2);
            var enabled = (ccer & (1 << shift)) != 0 && (!_advanced || (_bkr & 0x80) != 0);
            var (port, bit) = _pins[channel];
            if (!enabled)
            {
                if (_driven[channel] is not null)
                {
                    _driven[channel] = null;
                    port.DriveFromPeripheral(bit, false, false, 0);
                }

                continue;
            }

            var mode = (_ccmr[channel] >> 4) & 7;
            var below = Down ? _counter > _activeCcr[channel] : _counter < _activeCcr[channel];
            var active = mode switch
            {
                4 => false,
                5 => true,
                6 => Down ? !below : below,
                7 => Down ? below : !below,
                _ => _reference[channel],
            };
            var high = active ^ ((ccer & (2 << shift)) != 0);
            if (high != _driven[channel])
            {
                _driven[channel] = high;
                port.DriveFromPeripheral(bit, true, high, (_activeArr + 1L) * PerCount() * _ticksPerMasterClock());
            }
        }
    }
}
