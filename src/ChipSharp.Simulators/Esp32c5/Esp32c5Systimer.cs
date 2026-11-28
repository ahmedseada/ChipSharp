namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// SYSTIMER (TRM v1.1, chapter 14; registers by the SVD): two 52-bit counters, UNIT0 (counting after reset) and UNIT1,
/// at CNT_CLK's 16 MHz, a simulator tick a count; and three comparators. CONF (the units' and the comparators' WORK_EN),
/// UNITn_OP (UPDATE latches the count into UNITn_VALUE_HI/LO and sets VALUE_VALID), UNITn_LOAD_HI/LO and UNITn_LOAD
/// (loads the count), TARGETn_HI/LO, TARGETn_CONF (the period, PERIOD_MODE, TIMER_UNIT_SEL) and COMPn_LOAD, which arms
/// the comparator: at the target in target mode (at once when it has passed, within 2^51), every period from the count
/// at the load in period mode. An alarm sets INT_RAW's bit (INT_ENA, INT_CLR, INT_ST); INT_ST's bits are the interrupt
/// sources SYSTIMER_TARGET0-2 (61-63). REAL_TARGETn reads the armed target. The CPU's stalls do not stop the units.
/// </summary>
internal sealed class Esp32c5Systimer(SimulatedChip chip)
{
    private const ulong Mask = (1UL << 52) - 1;

    private readonly ulong[] _base = new ulong[2];
    private readonly long[] _since = new long[2];
    private readonly uint[] _loadHigh = new uint[2];
    private readonly uint[] _loadLow = new uint[2];
    private readonly ulong[] _value = new ulong[2];
    private readonly bool[] _valid = new bool[2];
    private readonly uint[] _targetHigh = new uint[3];
    private readonly uint[] _targetLow = new uint[3];
    private readonly uint[] _targetConf = new uint[3];
    private readonly bool[] _armed = new bool[3];
    private readonly ulong[] _real = new ulong[3];
    private uint _conf = 0x4600_0000;

    /// <summary>INT_RAW: the alarms that happened.</summary>
    public uint Raw { get; private set; }

    /// <summary>INT_ENA.</summary>
    public uint Enable { get; private set; }

    /// <summary>The interrupt sources SYSTIMER_TARGET0-2: INT_ST.</summary>
    public uint Status => Raw & Enable & 7;

    private bool Working(int unit) => (_conf & (1u << (30 - unit))) != 0;

    private bool Comparing(int comparator) => (_conf & (1u << (24 - comparator))) != 0;

    private int UnitOf(int comparator) => (int)(_targetConf[comparator] >> 31);

    /// <summary>The count of <paramref name="unit"/> now.</summary>
    public ulong Count(int unit) => (Working(unit) ? _base[unit] + (ulong)(chip.Ticks - _since[unit]) : _base[unit]) & Mask;

    /// <summary>The tick of the next alarm, or long.MaxValue; <paramref name="interruptsOnly"/>: only those whose interrupt is enabled.</summary>
    public long NextEvent(bool interruptsOnly = false)
    {
        var next = long.MaxValue;
        for (var c = 0; c < 3; c++)
        {
            if (!_armed[c] || !Comparing(c) || !Working(UnitOf(c)) || (interruptsOnly && (Enable & (1u << c)) == 0))
            {
                continue;
            }

            var ahead = (_real[c] - Count(UnitOf(c))) & Mask;
            next = Math.Min(next, ahead >= 1UL << 51 ? chip.Ticks : chip.Ticks + (long)ahead);
        }

        return next;
    }

    /// <summary>Raises the alarms due now; true when one was.</summary>
    public bool Advance()
    {
        var any = false;
        for (var c = 0; c < 3; c++)
        {
            if (!_armed[c] || !Comparing(c) || !Working(UnitOf(c)))
            {
                continue;
            }

            var count = Count(UnitOf(c));
            if (((count - _real[c]) & Mask) >= 1UL << 51)
            {
                continue; // not yet
            }

            Raw |= 1u << c;
            any = true;
            if ((_targetConf[c] & (1u << 30)) != 0)
            {
                var period = Math.Max(_targetConf[c] & 0x3FF_FFFF, 1);
                _real[c] = (_real[c] + period) & Mask;
            }
            else
            {
                _armed[c] = false;
            }
        }

        return any;
    }

    public uint Read(uint offset)
    {
        switch (offset)
        {
            case 0x00:
                return _conf;
            case 0x04 or 0x08:
                return _valid[(offset - 4) / 4] ? 1u << 29 : 0;
            case 0x0C or 0x14:
                return _loadHigh[(offset - 0x0C) / 8];
            case 0x10 or 0x18:
                return _loadLow[(offset - 0x10) / 8];
            case 0x1C or 0x24 or 0x2C:
                return _targetHigh[(offset - 0x1C) / 8];
            case 0x20 or 0x28 or 0x30:
                return _targetLow[(offset - 0x20) / 8];
            case 0x34 or 0x38 or 0x3C:
                return _targetConf[(offset - 0x34) / 4];
            case 0x40 or 0x48:
                return (uint)(_value[(offset - 0x40) / 8] >> 32);
            case 0x44 or 0x4C:
                return (uint)_value[(offset - 0x44) / 8];
            case 0x64:
                return Enable;
            case 0x68:
                return Raw;
            case 0x70:
                return Status;
            case 0x74 or 0x7C or 0x84:
                return (uint)_real[(offset - 0x74) / 8];
            case 0x78 or 0x80 or 0x88:
                return (uint)(_real[(offset - 0x78) / 8] >> 32);
            case 0xFC:
                return 0x0220_1073;
            default:
                return 0;
        }
    }

    public void Write(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x00:
                var counts = new[] { Count(0), Count(1) };
                var was = _conf;
                _conf = value;
                for (var unit = 0; unit < 2; unit++)
                {
                    _base[unit] = counts[unit];
                    _since[unit] = chip.Ticks;
                }

                for (var c = 0; c < 3; c++)
                {
                    if (Comparing(c) && (was & (1u << (24 - c))) == 0)
                    {
                        Arm(c); // a comparator that starts to work compares with what was loaded
                    }
                }

                break;
            case 0x04 or 0x08:
                if ((value & (1u << 30)) != 0)
                {
                    var unit = (int)(offset - 4) / 4;
                    _value[unit] = Count(unit);
                    _valid[unit] = true;
                }

                break;
            case 0x0C or 0x14:
                _loadHigh[(offset - 0x0C) / 8] = value & 0xFFFFF;
                break;
            case 0x10 or 0x18:
                _loadLow[(offset - 0x10) / 8] = value;
                break;
            case 0x1C or 0x24 or 0x2C:
                _targetHigh[(offset - 0x1C) / 8] = value & 0xFFFFF;
                break;
            case 0x20 or 0x28 or 0x30:
                _targetLow[(offset - 0x20) / 8] = value;
                break;
            case 0x34 or 0x38 or 0x3C:
                _targetConf[(offset - 0x34) / 4] = value & 0xC3FF_FFFF;
                break;
            case 0x50 or 0x54 or 0x58:
                if ((value & 1) != 0)
                {
                    Arm((int)(offset - 0x50) / 4);
                }

                break;
            case 0x5C or 0x60:
                if ((value & 1) != 0)
                {
                    var unit = (int)(offset - 0x5C) / 4;
                    _base[unit] = (((ulong)_loadHigh[unit] << 32) | _loadLow[unit]) & Mask;
                    _since[unit] = chip.Ticks;
                }

                break;
            case 0x64:
                Enable = value & 7;
                break;
            case 0x68:
                Raw = value & 7; // INT_RAW is writable, as the SVD says
                break;
            case 0x6C:
                Raw &= ~value;
                break;
        }
    }

    /// <summary>COMPn_LOAD: the comparator takes its target, or its first period from now.</summary>
    private void Arm(int c)
    {
        _real[c] = (_targetConf[c] & (1u << 30)) != 0
            ? (Count(UnitOf(c)) + Math.Max(_targetConf[c] & 0x3FF_FFFF, 1)) & Mask
            : (((ulong)_targetHigh[c] << 32) | _targetLow[c]) & Mask;
        _armed[c] = true;
    }
}
