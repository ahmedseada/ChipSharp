using System.Globalization;

namespace ChipSharp.Simulators.Ch32v003;

/// <summary>
/// The independent watchdog (CH32V003 RM V1.6, chapter 4): IWDG_CTLR's keys (0x5555 unlocks PSCR and RLDR, 0xCCCC starts
/// it, for good, 0xAAAA feeds it: the counter reloads), IWDG_PSCR (LSI / 4 to / 256), IWDG_RLDR (12 bits, 0xFFF after
/// reset), IWDG_STATR (PVU and RVU: the updates are done at once). Started, its counter goes down from RLDR at LSI's
/// 128 kHz over the prescaler; at 0 it resets the chip, which stops the run, as a failure. With the option bytes'
/// IWDG_SW 0 it would start at reset; WCH ships them with 1.
/// </summary>
internal sealed class Ch32v003Iwdg(SimulatedChip chip, Action<string> reset)
{
    private uint _prescaler;
    private uint _reload = 0xFFF;
    private bool _unlocked;
    private bool _running;
    private long _fedAt;

    /// <summary>The tick it resets the chip, or long.MaxValue while it is not running.</summary>
    public long NextEvent => _running ? _fedAt + TimeoutTicks : long.MaxValue;

    /// <summary>Its timeout, in ticks: (RLDR + 1) counts of LSI over the prescaler.</summary>
    private long TimeoutTicks => (long)(_reload + 1) * Divider * SimulatedChip.TicksPerSecond / Ch32v003Map.LsiHz;

    private uint Divider => _prescaler >= 6 ? 256u : 4u << (int)_prescaler;

    public void Advance()
    {
        if (chip.Ticks >= NextEvent)
        {
            _running = false;
            reset(string.Create(CultureInfo.InvariantCulture,
                $"the independent watchdog (IWDG) reset the chip: the program did not feed it (IWDG_CTLR 0xAAAA) within its " +
                $"{TimeoutTicks * 1000.0 / SimulatedChip.TicksPerSecond:0.###} ms"));
        }
    }

    public uint Read(uint offset) => offset switch
    {
        0x04 => _prescaler,
        0x08 => _reload,
        _ => 0, // CTLR is write-only; STATR: PVU and RVU 0, the updates are done
    };

    public void Write(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x00:
                switch (value & 0xFFFF)
                {
                    case 0x5555:
                        _unlocked = true;
                        break;
                    case 0xAAAA:
                        _fedAt = chip.Ticks;
                        _unlocked = false;
                        break;
                    case 0xCCCC:
                        if (!_running)
                        {
                            _running = true;
                            _fedAt = chip.Ticks;
                        }

                        break;
                }

                break;
            case 0x04 when _unlocked:
                _prescaler = value & 7;
                break;
            case 0x08 when _unlocked:
                _reload = value & 0xFFF;
                break;
        }
    }
}

/// <summary>
/// The window watchdog (CH32V003 RM V1.6, chapter 5): WWDG_CTLR (WDGA, set for good; T[6:0], 0x7F after reset),
/// WWDG_CFGR (EWI, set for good; WDGTB; W[6:0], 0x7F), WWDG_STATR (EWIF, cleared by writing 0). Its 7-bit counter goes
/// down every 4096 × 2^WDGTB HCLK cycles while its clock (RCC_APB1PCENR's WWDGEN) is on, whether WDGA is set or not.
/// When it goes from 0x40 to 0x3F (T6 cleared), or a write of T comes while the counter is above W (outside the window),
/// or T6 is written 0, with WDGA set it resets the chip, which stops the run, as a failure. At 0x40 EWIF is set: with EWI,
/// the interrupt WWDG (16).
/// </summary>
internal sealed class Ch32v003Wwdg(SimulatedChip chip, Func<long> hclk, Func<bool> clocked, Action<string> reset)
{
    private uint _counter = 0x7F;
    private uint _cfgr = 0x7F;
    private bool _enabled;
    private bool _flag;
    private long _baseTick;
    private long _remainder;
    private long _rate;

    /// <summary>Called when EWIF may have changed.</summary>
    public Action? Changed { get; set; }

    /// <summary>The interrupt WWDG: EWIF with EWI.</summary>
    public bool InterruptPending => _flag && (_cfgr & 0x200) != 0;

    /// <summary>The tick of its next event (EWIF at 0x40, the reset at 0x3F), or long.MaxValue.</summary>
    public long NextEvent { get; private set; } = long.MaxValue;

    /// <summary>Counter decrements a second (0 with its clock off).</summary>
    private long Rate => clocked() ? hclk() / (4096L << (int)((_cfgr >> 7) & 3)) : 0;

    private long Elapsed => _rate == 0 ? 0 : ((chip.Ticks - _baseTick) * _rate + _remainder) / SimulatedChip.TicksPerSecond;

    /// <summary>The counter now (7 bits; it goes on from 0 to 0x7F with WDGA off).</summary>
    private uint Counter => (uint)((_counter - Elapsed) & 0x7F);

    public void Advance()
    {
        if (chip.Ticks < NextEvent)
        {
            return;
        }

        Rebase();
        if (_counter == 0x3F && _enabled)
        {
            reset(string.Create(CultureInfo.InvariantCulture,
                $"the window watchdog (WWDG) reset the chip: its counter went from 0x40 to 0x3F before the program fed it (WWDG_CTLR)"));
            NextEvent = long.MaxValue;
            return;
        }

        if (_counter == 0x40)
        {
            _flag = true;
            Changed?.Invoke();
        }

        Schedule();
    }

    /// <summary>HCLK or the watchdog's clock changed: the counter goes on at the new rate.</summary>
    public void ClockChanged()
    {
        Rebase();
        Schedule();
    }

    /// <summary>Its registers to their reset values (RCC_APB1PRSTR's WWDGRST): off again.</summary>
    public void ResetRegisters()
    {
        (_counter, _cfgr, _enabled, _flag) = (0x7F, 0x7F, false, false);
        _baseTick = chip.Ticks;
        _remainder = 0;
        Schedule();
    }

    public uint Read(uint offset) => offset switch
    {
        0x00 => Counter | (_enabled ? 0x80u : 0),
        0x04 => _cfgr,
        0x08 => _flag ? 1u : 0,
        _ => 0,
    };

    public void Write(uint offset, uint value)
    {
        Rebase();
        switch (offset)
        {
            case 0x00:
                var counter = value & 0x7F;
                if (_enabled && _counter > (_cfgr & 0x7F))
                {
                    reset(string.Create(CultureInfo.InvariantCulture,
                        $"the window watchdog (WWDG) reset the chip: the program fed it with its counter at 0x{_counter:x2}, above the window (W 0x{_cfgr & 0x7F:x2})"));
                    return;
                }

                _enabled |= (value & 0x80) != 0;
                _counter = counter;
                if (_enabled && (counter & 0x40) == 0)
                {
                    reset("the window watchdog (WWDG) reset the chip: the program wrote T6 0 (WWDG_CTLR), a software reset");
                    return;
                }

                break;
            case 0x04:
                _cfgr = (value & 0x1FF) | (_cfgr & 0x200) | (value & 0x200);
                break;
            case 0x08:
                if ((value & 1) == 0)
                {
                    _flag = false;
                    Changed?.Invoke();
                }

                break;
        }

        Schedule();
    }

    private void Rebase()
    {
        if (_rate != 0)
        {
            var total = (chip.Ticks - _baseTick) * _rate + _remainder;
            var counted = total / SimulatedChip.TicksPerSecond;
            _counter = (uint)((_counter - counted) & 0x7F);
            _remainder = total - counted * SimulatedChip.TicksPerSecond;
        }
        else
        {
            _remainder = 0;
        }

        _baseTick = chip.Ticks;
    }

    /// <summary>The next tick the counter reaches 0x40 (EWIF) or, with WDGA, 0x3F (the reset).</summary>
    private void Schedule()
    {
        _rate = Rate;
        if (_rate == 0)
        {
            NextEvent = long.MaxValue;
            return;
        }

        long steps = _counter > 0x40 ? _counter - 0x40 : _counter == 0x40 ? 1 : _counter + 0x80 - 0x40;
        if (_counter == 0x40 && !_enabled)
        {
            steps = 0x80; // with WDGA off it wraps round to 0x40 again
        }

        var needed = steps * SimulatedChip.TicksPerSecond - _remainder;
        NextEvent = _baseTick + (needed + _rate - 1) / _rate;
    }
}
