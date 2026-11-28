namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// The ESP32-C5's LED PWM controller (LEDC at 0x6000_7000; TRM chapter 26, behaviour as ESP-IDF's ledc_ll.h and ledc.c
/// drive it): 4 timers that count the crystal (PCR_LEDC_SCLK_CONF: XTAL) divided by CLK_DIV (Q10.8) from 0 to 2^DUTY_RES
/// and wrap, and 6 channels that make a signal from a timer's count: high from the count 0 (HPOINT) for DUTY counts
/// (a channel's DUTY register has 4 fractional bits more), low for the rest of the period, high all the time at
/// 2^DUTY_RES, low at 0 or while the channel's signal is off (SIG_OUT_EN 0: its idle level). A new duty (PARA_UP) takes
/// effect at the start of the next period, and a timer's DUTY_RES and CLK_DIV (its PARA_UP) from its next period on; a
/// timer counts while it is not paused and the LEDC's clocks are on. A channel's signal goes to a pin through the GPIO
/// matrix as the output signal with the channel's number (0 to 5): <see cref="Esp32c5Gpio"/> asks <see cref="Level"/> and
/// is told by <see cref="Changed"/> when it changes, and measures the pin's edges as PWM.
/// </summary>
internal sealed class Esp32c5Ledc(SimulatedChip chip)
{
    private const int Channels = 6, Timers = 4;
    private const uint ChannelStride = 0x14, TimerBase = 0xA0;

    private readonly Dictionary<uint, uint> _registers = [];
    private readonly Timer[] _timers = [.. Enumerable.Range(0, Timers).Select(_ => new Timer())];
    private readonly Channel[] _channels = [.. Enumerable.Range(0, Channels).Select(_ => new Channel())];

    /// <summary>Called when the level of a channel's signal has changed, with the channel's number.</summary>
    public Action<int>? Changed { get; set; }

    /// <summary>Whether the LEDC's register and function clocks are on (PCR_LEDC_CONF and PCR_LEDC_SCLK_CONF).</summary>
    public Func<bool> ClocksOn { get; set; } = () => true;

    /// <summary>The ticks of one period of the timer a channel is on, for the meter that measures its pin.</summary>
    public long PeriodTicks(int channel) => Math.Max(1, _timers[_channels[channel].Timer].PeriodTicks);

    /// <summary>The level of a channel's signal now.</summary>
    public bool Level(int channel) => _channels[channel].High;

    public uint Read(uint offset)
    {
        var value = _registers.GetValueOrDefault(offset);
        if (offset < ChannelStride * Channels)
        {
            return (offset % ChannelStride) switch
            {
                0x00 => value & ~(1u << 4), // PARA_UP clears when taken
                0x0C => value & ~(1u << 31), // DUTY_START too
                _ => value,
            };
        }

        return value;
    }

    public void Write(uint offset, uint value)
    {
        _registers[offset] = value;
        if (offset < ChannelStride * Channels)
        {
            var channel = (int)(offset / ChannelStride);
            if (offset % ChannelStride == 0x00)
            {
                var state = _channels[channel];
                state.Timer = (int)(value & 3);
                state.SignalOn = (value & 4) != 0;
                state.Idle = (value & 8) != 0;
                if ((value & (1u << 4)) != 0)
                {
                    state.PendingDuty = (_registers.GetValueOrDefault((uint)channel * ChannelStride + 0x08) & 0x1FFFFFF) >> 4;
                    Refresh(channel);
                }
            }

            return;
        }

        if (offset is >= TimerBase and < TimerBase + 8 * Timers && (offset - TimerBase) % 8 == 0)
        {
            var timer = (int)((offset - TimerBase) / 8);
            var state = _timers[timer];
            state.Bits = (int)(value & 0x1F);
            state.Divider = (value >> 5) & 0x3FFFF;
            state.Paused = (value & (1u << 23)) != 0;
            if ((value & ((1u << 24) | (1u << 26))) != 0 || state.Paused)
            {
                for (var channel = 0; channel < Channels; channel++)
                {
                    if (_channels[channel].Timer == timer)
                    {
                        Refresh(channel, restart: (value & (1u << 24)) != 0);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Starts a channel's waveform when its signal is on and its timer counts (from the next period boundary: now, for the
    /// first), or stops it (the signal at its idle level) when not; <paramref name="restart"/>: the timer was reset, so the
    /// periods start again now.
    /// </summary>
    private void Refresh(int channel, bool restart = false)
    {
        var state = _channels[channel];
        var timer = _timers[state.Timer];
        var counting = state.SignalOn && !timer.Paused && ClocksOn() && timer.Divider != 0;
        if (!counting)
        {
            state.Version++;
            state.Running = false;
            Set(channel, !state.SignalOn && state.Idle);
            return;
        }

        if (state.Running && !restart)
        {
            return;
        }

        var version = ++state.Version;
        state.Running = true;
        Period(channel, version, chip.Ticks);
    }

    /// <summary>One period of a channel, from <paramref name="start"/>: the pending duty taken, the signal high until the duty's counts have passed.</summary>
    private void Period(int channel, int version, long start)
    {
        var state = _channels[channel];
        if (version != state.Version)
        {
            return;
        }

        var timer = _timers[state.Timer];
        var period = Math.Max(1, timer.PeriodTicks);
        var counts = 1L << timer.Bits;
        var duty = Math.Min(state.PendingDuty, counts);
        Set(channel, duty > 0);
        if (duty > 0 && duty < counts)
        {
            chip.At(start + (long)Math.Round((double)period * duty / counts), () =>
            {
                if (version == state.Version)
                {
                    Set(channel, false);
                }
            });
        }

        chip.At(start + period, () => Period(channel, version, start + period));
    }

    private void Set(int channel, bool high)
    {
        if (_channels[channel].High != high)
        {
            _channels[channel].High = high;
            Changed?.Invoke(channel);
        }
    }

    private sealed class Timer
    {
        public int Bits { get; set; }

        public uint Divider { get; set; }

        public bool Paused { get; set; } = true;

        /// <summary>The period in ticks of 16 MHz: 2^bits counts of divider/256 crystal cycles.</summary>
        public long PeriodTicks => (long)Math.Round((double)(1L << Bits) * Divider / 256 * SimulatedChip.TicksPerSecond / Esp32c5Map.XtalHz);
    }

    private sealed class Channel
    {
        public int Timer { get; set; }

        public bool SignalOn { get; set; }

        public bool Idle { get; set; }

        public long PendingDuty { get; set; }

        public bool High { get; set; }

        public bool Running { get; set; }

        public int Version { get; set; }
    }
}
