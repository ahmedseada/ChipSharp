using System.Globalization;

namespace ChipSharp.Simulators;

/// <summary>What a pin does, as the simulators print it: an input (with its pull-up or without), or an output, low or high.</summary>
public enum PinState
{
    Input,
    InputPullUp,
    Low,
    High,
}

/// <summary>
/// A GPIO port of up to 8 pins (PA0-PA7, ...), as the chip's registers set it up: each pin an input, with its pull-up or
/// without, or an output, push-pull or open-drain (released when it outputs 1); a peripheral can drive a pin itself (a
/// timer's PWM output). From outside, an --input drives a pin's level; a pin nothing drives reads high with its pull-up,
/// else low. Every change of what a pin does is printed ("PA3 high", "PA3 input pull-up"), but a pin a timer drives is
/// measured instead: a line for each PWM period that differs from the one before, as tools/avr-sim does.
/// </summary>
public sealed class GpioPort
{
    private readonly SimulatedChip _chip;
    private readonly PinState[] _states = new PinState[8];
    private readonly PwmMeter?[] _meters = new PwmMeter?[8];

    /// <summary>
    /// A port named <paramref name="letter"/>, with the pins of <paramref name="pins"/> (a bit each); on a chip that
    /// numbers its pins instead (GPIO0, GPIO1, ...), <paramref name="firstGpio"/> is the number of the port's bit 0.
    /// </summary>
    public GpioPort(SimulatedChip chip, char letter, byte pins, int? firstGpio = null)
    {
        _chip = chip;
        Letter = letter;
        Pins = pins;
        FirstGpio = firstGpio;
    }

    /// <summary>The port's letter: 'A' for PA0-PA7.</summary>
    public char Letter { get; }

    /// <summary>The pins the chip has (a bit each).</summary>
    public byte Pins { get; }

    /// <summary>What the program outputs on each pin (ODR, PA).</summary>
    public byte Output { get; set; }

    /// <summary>Which pins are outputs (DDR, PAC).</summary>
    public byte Direction { get; set; }

    /// <summary>Which outputs drive high as well as low; the others are open-drain. Every pin unless the chip says.</summary>
    public byte PushPull { get; set; } = 0xFF;

    /// <summary>Which inputs have their pull-up on.</summary>
    public byte PullUp { get; set; }

    /// <summary>Which pins a peripheral drives itself (a timer's output), whatever the registers above say.</summary>
    public byte PeripheralDriven { get; private set; }

    /// <summary>The levels the peripheral drives those pins at.</summary>
    public byte PeripheralLevel { get; private set; }

    /// <summary>Which pins an --input drives from outside.</summary>
    public byte OutsideDriven { get; private set; }

    /// <summary>The levels it drives them at.</summary>
    public byte OutsideLevel { get; private set; }

    /// <summary>Every pin's level, as the program reads it (IDR, PA): 1 high.</summary>
    public byte Levels { get; private set; }

    /// <summary>Called when a pin's level changes (the bit, whether it is high now), after the change is printed.</summary>
    public Action<int, bool>? LevelChanged { get; set; }

    /// <summary>The GPIO number of bit 0, on a chip that numbers its pins; null on one that names them by port (PA3).</summary>
    public int? FirstGpio { get; }

    /// <summary>A pin's name: "PA3", or "GPIO11" on a chip that numbers its pins.</summary>
    public string PinName(int bit) => FirstGpio is { } first
        ? $"GPIO{(first + bit).ToString(CultureInfo.InvariantCulture)}"
        : $"P{Letter}{bit.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>What a pin does now.</summary>
    public PinState State(int bit) => _states[bit];

    /// <summary>Drives a pin from outside, as an --input does.</summary>
    public void DriveFromOutside(int bit, bool high)
    {
        OutsideDriven |= (byte)(1 << bit);
        OutsideLevel = (byte)(high ? OutsideLevel | (1 << bit) : OutsideLevel & ~(1 << bit));
        Update();
    }

    /// <summary>
    /// A peripheral drives a pin (<paramref name="drives"/>), at <paramref name="high"/>; its edges are measured as PWM,
    /// with periods of about <paramref name="periodTicks"/> until one is measured. Not driving it gives it back to the port.
    /// </summary>
    public void DriveFromPeripheral(int bit, bool drives, bool high, long periodTicks)
    {
        var mask = (byte)(1 << bit);
        if (!drives)
        {
            if ((PeripheralDriven & mask) != 0)
            {
                PeripheralDriven &= (byte)~mask;
                _meters[bit]?.Stop();
                _meters[bit] = null;
                _states[bit] = (PinState)(-1); // printed again, whatever it is
            }

            Update();
            return;
        }

        PeripheralDriven |= mask;
        PeripheralLevel = (byte)(high ? PeripheralLevel | mask : PeripheralLevel & ~mask);
        (_meters[bit] ??= new PwmMeter(_chip, PinName(bit))).ExpectedPeriod = Math.Max(periodTicks, 1);
        Update();
    }

    /// <summary>Works out every pin's level and state from the registers and what drives them; prints what changed.</summary>
    public void Update()
    {
        byte levels = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            var mask = 1 << bit;
            if ((Pins & mask) == 0)
            {
                continue;
            }

            PinState state;
            bool high;
            if ((PeripheralDriven & mask) != 0)
            {
                high = (PeripheralLevel & mask) != 0;
                state = high ? PinState.High : PinState.Low;
            }
            else if ((Direction & mask) != 0 && ((PushPull & mask) != 0 || (Output & mask) == 0))
            {
                high = (Output & mask) != 0;
                state = high ? PinState.High : PinState.Low;
            }
            else
            {
                var pullUp = (Direction & mask) == 0 && (PullUp & mask) != 0;
                state = pullUp ? PinState.InputPullUp : PinState.Input;
                high = (OutsideDriven & mask) != 0 ? (OutsideLevel & mask) != 0 : pullUp;
            }

            levels |= (byte)(high ? mask : 0);
            if (state != _states[bit])
            {
                _states[bit] = state;
                if (_meters[bit] is { } meter)
                {
                    meter.Edge(high);
                }
                else
                {
                    _chip.Print($"{PinName(bit)} {Text(state)}");
                }
            }
        }

        var changed = (byte)(levels ^ Levels);
        Levels = levels;
        for (var bit = 0; changed != 0 && bit < 8; bit++)
        {
            if ((changed & (1 << bit)) != 0)
            {
                LevelChanged?.Invoke(bit, (levels & (1 << bit)) != 0);
                _chip.OnPinLevelChanged(this, bit, (levels & (1 << bit)) != 0);
            }
        }
    }

    /// <summary>A pin state as lines print it.</summary>
    public static string Text(PinState state) => state switch
    {
        PinState.Low => "low",
        PinState.High => "high",
        PinState.InputPullUp => "input pull-up",
        _ => "input",
    };

    /// <summary>
    /// A PWM output's edges, measured: a whole period (rising edge to rising edge, high until the falling edge between)
    /// is printed when it differs from the one before ("PB4 PWM 1000.00 Hz, duty 25.00% (250.00 us high)"); no edge for
    /// 2.5 periods is printed as "PB4 PWM steady low" (or high).
    /// </summary>
    private sealed class PwmMeter(SimulatedChip chip, string name)
    {
        private long? _rise;
        private long? _fall;
        private long _period;
        private bool _high;
        private string? _printed;
        private int _version;

        /// <summary>The period the timer's registers give now: 2.5 of it (or of the one measured, the longer) without an edge is steady.</summary>
        public long ExpectedPeriod { get; set; }

        public void Edge(bool high)
        {
            _high = high;
            var now = chip.Ticks;
            if (high && _rise is { } rise && _fall is { } fall && fall > rise)
            {
                _period = now - rise;
                var hertz = (double)SimulatedChip.TicksPerSecond / _period;
                var duty = 100.0 * (fall - rise) / _period;
                var microseconds = (fall - rise) * 1e6 / SimulatedChip.TicksPerSecond;
                Say(string.Create(CultureInfo.InvariantCulture,
                    $"{hertz:F2} Hz, duty {duty:F2}% ({microseconds:F2} us high)"));
            }

            if (high)
            {
                _rise = now;
            }
            else
            {
                _fall = now;
            }

            var version = ++_version;
            chip.At(now + (long)(2.5 * Math.Max(_period, ExpectedPeriod)), () =>
            {
                if (version == _version)
                {
                    Say($"steady {(_high ? "high" : "low")}");
                    _rise = null; // the next period starts with the next rising edge
                    _fall = null;
                }
            });
        }

        public void Stop() => _version++;

        private void Say(string text)
        {
            if (text != _printed)
            {
                chip.Print($"{name} PWM {text}");
                _printed = text;
            }
        }
    }
}
