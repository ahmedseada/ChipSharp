using System.Globalization;

namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// The watchdog of a timer group, MWDT0 or MWDT1 (TRM v1.1, chapter 16 Watchdog Timers; TIMG's registers by the SVD):
/// four stages, each with its timeout (WDTCONFIG2-5, in cycles of the WDT clock: PCR_TGn_WDT_CLK_SEL's XTAL, RC_FAST or
/// PLL_F80M over WDTCONFIG1's prescaler) and its action (WDTCONFIG0's STG0-3: none, an interrupt, a CPU reset, a core
/// reset); a write of WDTFEED starts stage 0 again. The registers change only while WDTWPROTECT holds 0x50D83AA1, and
/// the stages, their timeouts, the prescaler and the flash boot protection take effect at WDT_CONF_UPDATE_EN, as on the
/// chip (WDT_EN at once). The ROM leaves MWDT0 running in flash boot mode (WDT_FLASHBOOT_MOD_EN): its stage 0 is a core
/// reset, 26 000 000 XTAL cycles (about 0.54 s) after the start, unless the program clears the mode. A reset stops the
/// run as a failure; the interrupt is INT_RAW_TIMERS's WDT bit (INT_ENA_TIMERS, INT_ST_TIMERS, INT_CLR_TIMERS), the
/// source TGn_WDT_LEVEL. The group's other registers (its timers) only keep what is written.
/// </summary>
internal sealed class Esp32c5Mwdt
{
    private readonly SimulatedChip _chip;
    private readonly int _group;
    private readonly Func<uint> _clockConfiguration;
    private readonly Action<string> _reset;
    private readonly uint[] _config = new uint[6];
    private readonly uint[] _active = new uint[6];
    private readonly Dictionary<uint, uint> _other = [];
    private uint _key = Esp32c5Map.WatchdogKey;
    private int _stage;
    private long _stageStart;

    /// <summary>The watchdog of timer group <paramref name="group"/>, as the ROM leaves it (MWDT0 in flash boot mode).</summary>
    public Esp32c5Mwdt(SimulatedChip chip, int group, Func<uint> clockConfiguration, Action<string> reset)
    {
        _chip = chip;
        _group = group;
        _clockConfiguration = clockConfiguration;
        _reset = reset;
        uint[] resets = [group == 0 ? 0x4C000u : 0x0C000u, 0x10000, 0x18CBA80, 0x7FFFFFF, 0xFFFFF, 0xFFFFF];
        resets.CopyTo(_config, 0);
        resets.CopyTo(_active, 0);
    }

    /// <summary>INT_ENA_TIMERS and INT_RAW_TIMERS: T0, T1, WDT.</summary>
    public uint Enable { get; private set; }

    public uint Raw { get; private set; }

    /// <summary>The interrupt source TGn_WDT_LEVEL.</summary>
    public bool InterruptPending => (Raw & Enable & 4) != 0;

    private bool Flashboot => (_active[0] & (1u << 14)) != 0;

    private bool Running => ((_config[0] & (1u << 31)) != 0 || Flashboot) && (_clockConfiguration() & (1u << 22)) != 0;

    /// <summary>The action of a stage: 0 none, 1 interrupt, 2 CPU reset, 3 core reset; stage 0 in flash boot mode a core reset.</summary>
    private int Action(int stage) => stage == 0 && Flashboot && (_config[0] & (1u << 31)) == 0
        ? 3
        : (int)(_active[0] >> (29 - 2 * stage)) & 3;

    private double TicksPerCycle
    {
        get
        {
            double clock = ((_clockConfiguration() >> 20) & 3) switch
            {
                1 => Esp32c5Map.RcFastHz,
                2 => Esp32c5Map.Pll80Hz,
                _ => Esp32c5Map.XtalHz,
            };
            var prescale = _active[1] >> 16;
            return SimulatedChip.TicksPerSecond * (double)(prescale == 0 ? 0x10000 : prescale) / clock;
        }
    }

    /// <summary>The tick the current stage times out, or long.MaxValue while the watchdog is off.</summary>
    public long NextEvent => Running ? _stageStart + Math.Max(1, (long)Math.Ceiling(Math.Max(_active[2 + _stage], 1u) * TicksPerCycle)) : long.MaxValue;

    public void Advance()
    {
        while (NextEvent <= _chip.Ticks)
        {
            var at = NextEvent;
            var action = Action(_stage);
            if (action == 1)
            {
                Raw |= 4;
            }
            else if (action >= 2)
            {
                var hint = _stage == 0 && Flashboot && (_config[0] & (1u << 31)) == 0
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"; the ROM leaves its flash boot protection on: clear WDT_FLASHBOOT_MOD_EN (TIMG{_group}_WDTCONFIG0 bit 14)")
                    : "";
                _reset(string.Create(CultureInfo.InvariantCulture,
                    $"the timer group {_group} watchdog (MWDT{_group}) reset the chip (stage {_stage}: " +
                    $"{(action == 2 ? "CPU reset" : "core reset")}): the program did not feed it or turn it off{hint}"));
                return;
            }

            _stage = (_stage + 1) & 3;
            _stageStart = at;
        }
    }

    public uint Read(uint offset) => offset switch
    {
        >= 0x48 and <= 0x5C => _config[(offset - 0x48) / 4] & ~(1u << 22),
        0x64 => _key,
        0x70 => Enable,
        0x74 => Raw,
        0x78 => Raw & Enable,
        0xF8 => 0x0220_9142,
        0xFC => _other.GetValueOrDefault(offset, 0x7000_0000u),
        0x00 or 0x24 => _other.GetValueOrDefault(offset, 0x6000_2000u),
        0x68 => _other.GetValueOrDefault(offset, 0x11000u),
        0x80 => _other.GetValueOrDefault(offset, 0xFFFF_FF98u),
        _ => _other.GetValueOrDefault(offset),
    };

    public void Write(uint offset, uint value)
    {
        var unlocked = _key == Esp32c5Map.WatchdogKey;
        switch (offset)
        {
            case >= 0x48 and <= 0x5C:
                if (!unlocked)
                {
                    return;
                }

                var index = (int)(offset - 0x48) / 4;
                var wasRunning = Running;
                _config[index] = value;
                if (index == 0 && (value & (1u << 22)) != 0)
                {
                    _config.CopyTo(_active, 0); // WDT_CONF_UPDATE_EN
                }

                if (!wasRunning && Running)
                {
                    _stage = 0;
                    _stageStart = _chip.Ticks;
                }

                if (index == 1 && (value & 1) != 0)
                {
                    _stageStart = _chip.Ticks; // WDT_DIVCNT_RST
                }

                break;
            case 0x60:
                if (unlocked)
                {
                    _stage = 0;
                    _stageStart = _chip.Ticks;
                }

                break;
            case 0x64:
                _key = value;
                break;
            case 0x70:
                Enable = value & 7;
                break;
            case 0x7C:
                Raw &= ~value;
                break;
            default:
                _other[offset] = value;
                break;
        }
    }
}

/// <summary>
/// LP_WDT (TRM v1.1, chapter 16; registers by the SVD): the RTC watchdog (RWDT) and the super watchdog (SWD). RWDT has
/// four stages (CONFIG1-4, in RTC_SLOW_CLK cycles, 136 kHz; stage 0's timeout shifted left by EFUSE_WDT_DELAY_SEL + 1,
/// the eFuse 0 here) with their actions (CONFIG0's STG0-3: none, an interrupt, a CPU, core or system reset); FEED's
/// RTC_WDT_FEED starts stage 0 again; the registers change only while WPROTECT holds 0x50D83AA1. The ROM leaves it in
/// flash boot mode (WDT_FLASHBOOT_MOD_EN): a system reset 400 000 cycles (about 2.9 s) after the start. The SWD, in the
/// analog domain, resets the chip slightly less than a second (here 900 ms) after its last feed, and asks to be fed 100
/// ms before (SUPER_WDT_INT_RAW): SWD_CONFIG's SWD_FEED feeds it, SWD_AUTO_FEED_EN has the hardware feed it, SWD_DISABLE
/// stops it, while SWD_WPROTECT holds the key. A reset stops the run as a failure; the interrupts are INT_RAW's bits 30
/// (SWD) and 31 (RWDT), with INT_ENA, INT_ST and INT_CLR: the source LP_WDT (18).
/// </summary>
internal sealed class Esp32c5LpWdt(SimulatedChip chip, Action<string> reset)
{
    private const long SuperTimeout = SimulatedChip.TicksPerSecond * 9 / 10, SuperWarning = SimulatedChip.TicksPerSecond / 10;
    private const double TicksPerSlowCycle = (double)SimulatedChip.TicksPerSecond / Esp32c5Map.RcSlowHz;

    private readonly uint[] _config = [0x13214, 0x30D40, 0x13880, 0xFFF, 0xFFF];
    private uint _key;
    private uint _swdConfig = 0x12C0_0000;
    private uint _swdKey;
    private int _stage;
    private long _stageStart;
    private long _swdFed;
    private bool _swdWarned;
    private uint _enable;

    public uint Raw { get; private set; }

    /// <summary>The interrupt source LP_WDT.</summary>
    public bool InterruptPending => (Raw & _enable) != 0;

    private bool Flashboot => (_config[0] & (1u << 12)) != 0;

    private bool Running => (_config[0] & (1u << 31)) != 0 || Flashboot;

    private bool SuperRunning => (_swdConfig & ((1u << 30) | (1u << 18))) == 0;

    private int Action(int stage) => stage == 0 && Flashboot && (_config[0] & (1u << 31)) == 0
        ? 4
        : (int)(_config[0] >> (28 - 3 * stage)) & 7;

    private long StageEnd
    {
        get
        {
            var hold = (double)_config[1 + _stage] * (_stage == 0 ? 2 : 1);
            return _stageStart + Math.Max(1, (long)Math.Ceiling(Math.Max(hold, 1) * TicksPerSlowCycle));
        }
    }

    public long NextEvent => Math.Min(Running ? StageEnd : long.MaxValue,
        SuperRunning ? _swdFed + (_swdWarned ? SuperTimeout : SuperTimeout - SuperWarning) : long.MaxValue);

    public void Advance()
    {
        while (Running && StageEnd <= chip.Ticks)
        {
            var at = StageEnd;
            var action = Action(_stage);
            if (action == 1)
            {
                Raw |= 1u << 31;
            }
            else if (action >= 2)
            {
                var hint = _stage == 0 && Flashboot && (_config[0] & (1u << 31)) == 0
                    ? "; the ROM leaves its flash boot protection on: clear WDT_FLASHBOOT_MOD_EN (LP_WDT_CONFIG0 bit 12)"
                    : "";
                var what = action switch { 2 => "CPU reset", 3 => "core reset", _ => "system reset" };
                reset(string.Create(CultureInfo.InvariantCulture,
                    $"the RTC watchdog (RWDT) reset the chip (stage {_stage}: {what}): the program did not feed it or turn it off{hint}"));
                return;
            }

            _stage = (_stage + 1) & 3;
            _stageStart = at;
        }

        if (!SuperRunning)
        {
            return;
        }

        if (!_swdWarned && _swdFed + SuperTimeout - SuperWarning <= chip.Ticks)
        {
            _swdWarned = true;
            Raw |= 1u << 30;
        }

        if (_swdFed + SuperTimeout <= chip.Ticks)
        {
            reset("the super watchdog (SWD) reset the chip: the program did not feed it (LP_WDT_SWD_FEED), let the " +
                "hardware feed it (LP_WDT_SWD_AUTO_FEED_EN) or turn it off (LP_WDT_SWD_DISABLE) within 900 ms");
        }
    }

    public uint Read(uint offset) => offset switch
    {
        <= 0x10 => _config[offset / 4],
        0x18 => _key,
        0x1C => _swdConfig & ~((1u << 31) | (1u << 19) | 1u),
        0x20 => _swdKey,
        0x24 => Raw,
        0x28 => Raw & _enable,
        0x2C => _enable,
        0x3FC => 0x0211_2080,
        _ => 0,
    };

    public void Write(uint offset, uint value)
    {
        switch (offset)
        {
            case <= 0x10:
                if (_key == Esp32c5Map.WatchdogKey)
                {
                    var wasRunning = Running;
                    _config[offset / 4] = value;
                    if (!wasRunning && Running)
                    {
                        _stage = 0;
                        _stageStart = chip.Ticks;
                    }
                }

                break;
            case 0x14:
                if (_key == Esp32c5Map.WatchdogKey && (value & (1u << 31)) != 0)
                {
                    _stage = 0;
                    _stageStart = chip.Ticks;
                }

                break;
            case 0x18:
                _key = value;
                break;
            case 0x1C:
                if (_swdKey == Esp32c5Map.WatchdogKey)
                {
                    var wasRunning = SuperRunning;
                    _swdConfig = value & ~((1u << 31) | (1u << 19));
                    if ((value & (1u << 31)) != 0 || (!wasRunning && SuperRunning))
                    {
                        _swdFed = chip.Ticks;
                        _swdWarned = false;
                    }
                }

                break;
            case 0x20:
                _swdKey = value;
                break;
            case 0x24:
                Raw = value & 0xC000_0000;
                break;
            case 0x2C:
                _enable = value & 0xC000_0000;
                break;
            case 0x30:
                Raw &= ~value;
                break;
        }
    }
}
