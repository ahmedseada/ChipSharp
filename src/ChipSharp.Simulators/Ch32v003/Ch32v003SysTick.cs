namespace ChipSharp.Simulators.Ch32v003;

/// <summary>
/// The QingKe V2's SysTick (STK, at 0xE000_F000; QingKeV2 Processor Manual V1.1, chapter 4; CH32V003 RM V1.6, 6.5.4): a
/// 32-bit counter that counts up, from HCLK (STK_CTLR.STCLK 1) or HCLK / 8 (0), while STE is set. When it reaches
/// STK_CMPR, STK_SR.CNTIF is set (a write of 0 clears it) and, with STRE, it counts from 0 again (a period of CMPR + 1
/// counts); without, on (it wraps at 2^32). CNTIF with STIE is the SysTick interrupt (12); STK_CTLR.SWIE (bit 31) the
/// software interrupt (14), until the program clears it. STK_CNTR can be written. The counter is worked out from the time
/// when read, not counted tick by tick.
/// </summary>
internal sealed class Ch32v003SysTick(SimulatedChip chip, Func<long> hclk)
{
    private uint _ctlr;
    private bool _flag;
    private uint _compare;
    private uint _baseCount;
    private long _baseTick;
    private long _remainder;
    private long _rate;
    private long _matchTick = long.MaxValue;

    /// <summary>Called when the interrupt lines may have changed.</summary>
    public Action? Changed { get; set; }

    /// <summary>The SysTick interrupt's line: CNTIF with STIE.</summary>
    public bool InterruptPending => _flag && (_ctlr & 2) != 0;

    /// <summary>The software interrupt's line: SWIE.</summary>
    public bool SoftwarePending => (_ctlr & 0x8000_0000) != 0;

    /// <summary>The tick CNTIF is set next, or long.MaxValue.</summary>
    public long NextEvent => _matchTick;

    /// <summary>Whether its interrupt is on (STE and STIE), so that its next match could wake the core.</summary>
    public bool InterruptsOn => (_ctlr & 3) == 3;

    /// <summary>The counts a second now: HCLK or HCLK / 8; 0 when stopped.</summary>
    private long Rate => (_ctlr & 1) == 0 ? 0 : (_ctlr & 4) != 0 ? hclk() : hclk() / 8;

    /// <summary>The counter now.</summary>
    public uint Count
    {
        get
        {
            var elapsed = Elapsed(chip.Ticks);
            if ((_ctlr & 8) == 0)
            {
                return (uint)(_baseCount + elapsed);
            }

            var first = FirstMatch();
            return elapsed <= first ? (uint)(_baseCount + elapsed) : (uint)((elapsed - first - 1) % ((long)_compare + 1));
        }
    }

    /// <summary>Lets the match come, when it is due: CNTIF.</summary>
    public void Advance()
    {
        if (chip.Ticks >= _matchTick)
        {
            _flag = true;
            Rebase();
            Changed?.Invoke();
        }
    }

    /// <summary>HCLK changed: the counter goes on at the new rate from what it counted so far.</summary>
    public void ClockChanged() => Rebase();

    public uint Read(uint offset) => offset switch
    {
        0x00 => _ctlr,
        0x04 => _flag ? 1u : 0,
        0x08 => Count,
        0x10 => _compare,
        _ => 0,
    };

    public void Write(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x00:
                Rebase();
                _ctlr = value & 0x8000_003F;
                break;
            case 0x04:
                if ((value & 1) == 0)
                {
                    _flag = false;
                }

                break;
            case 0x08:
                Rebase();
                _baseCount = value;
                break;
            case 0x10:
                Rebase();
                _compare = value;
                break;
            default:
                return;
        }

        Schedule();
        Changed?.Invoke();
    }

    /// <summary>The counts from the base to <paramref name="tick"/>.</summary>
    private long Elapsed(long tick) => _rate == 0 ? 0 : ((tick - _baseTick) * _rate + _remainder) / SimulatedChip.TicksPerSecond;

    /// <summary>The counts from the base to the first match (at least 1: a counter already at CMPR matches 2^32 counts on).</summary>
    private long FirstMatch()
    {
        var steps = (long)(uint)(_compare - _baseCount);
        return steps == 0 ? 1L << 32 : steps;
    }

    /// <summary>The counter now becomes the base, at the rate HCLK gives now.</summary>
    private void Rebase()
    {
        var now = chip.Ticks;
        if (_rate != 0)
        {
            var total = (now - _baseTick) * _rate + _remainder;
            var counted = total / SimulatedChip.TicksPerSecond;
            _baseCount = Count;
            _remainder = total - counted * SimulatedChip.TicksPerSecond;
        }
        else
        {
            _remainder = 0;
        }

        _baseTick = now;
        _rate = Rate;
        Schedule();
    }

    /// <summary>The tick of the next match, from the base.</summary>
    private void Schedule()
    {
        _rate = Rate;
        if (_rate == 0)
        {
            _matchTick = long.MaxValue;
            return;
        }

        var counts = FirstMatch();
        // the first tick at which (tick - base) * rate + remainder reaches counts * TicksPerSecond
        var needed = counts * SimulatedChip.TicksPerSecond - _remainder;
        _matchTick = _baseTick + (needed + _rate - 1) / _rate;
    }
}
