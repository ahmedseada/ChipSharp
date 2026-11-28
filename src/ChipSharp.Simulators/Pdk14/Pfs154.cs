using System.Globalization;

namespace ChipSharp.Simulators.Pdk14;

/// <summary>
/// A Padauk PFS154 (the 16-pin package): the PDK14 core (<see cref="Pdk14Cpu"/>) with 2K words of program memory and 128
/// bytes of RAM; ports A (PA0, PA3-PA7; PA5 open-drain) and B (PB0-PB7) with their pull-ups; Timer16, Timer2 and Timer3
/// with their interrupts and outputs; the interrupts of PA0 and PB0; the clock and the watchdog. The I/O registers are
/// at the addresses of Free-PDK's pfs154.h.
/// <para>
/// The image is SDCC's Intel HEX (sdcc -mpdk14): byte address 2w holds the low byte of the word at word address w, 2w + 1
/// its high byte (6 bits). What it does not fill reads 0x3FFF, as blank program memory, and running into it stops the
/// run as a failure, but the factory's trim values: words 0x7ED (IHRCR) and 0x7EE (BGTR) are <c>ret k</c> instructions
/// (of <see cref="FactoryTrim"/>), which a program calls to read them. The CPU starts at word 0 with interrupts off.
/// </para>
/// <para>
/// Clocks: the IHRC is exactly 16 MHz (IHRCR, its calibration, changes nothing) and the ILRC 16 MHz / 291 (54.98 kHz;
/// the datasheet's is about 55 kHz). The system clock is what CLKMD selects (IHRC/1 to /64, ILRC/1 to /16), the ILRC
/// after reset (CLKMD 0xF6), and an instruction takes 1 or 2 of its cycles (<see cref="Pdk14Cpu"/>). The external
/// oscillator is not there: selecting it stops the run as a failure. The oscillators' enable bits are not modeled
/// (only the ILRC's, for the watchdog).
/// </para>
/// <para>
/// Timer16 counts the clock T16M selects (system clock, IHRC, ILRC, or falling edges of PA0 or PA4), divided by 1, 4, 16
/// or 64, and requests its interrupt (INTRQ bit 2) when bit 8-15 (T16M bits 0-2) of the count rises, or falls (INTEGS
/// bit 4). Timer2 and Timer3 (TM2C/TM3C, TM2S/TM3S, TM2CT/TM3CT, TM2B/TM3B) count their clock (system clock, IHRC,
/// ILRC, or edges of PA0, PB0 or PA4), divided by the prescaler (1, 4, 16, 64) and the scaler (1-32): in period mode
/// from 0 to the bound, where they start again at 0 and toggle their output; in PWM mode from 0 to 255 (or 63), their
/// output high while below the bound. Each requests its interrupt (INTRQ bits 6, 7) when it reaches the bound. Their
/// output (TM2: PB2, PA3, PB4; TM3: PB5, PB6, PB7) drives the pin, measured as PWM. PA0 and PB0 request theirs (INTRQ
/// bits 0, 1) on the edges INTEGS selects. A request is taken when its INTEN bit and the global enable are on.
/// </para>
/// <para>
/// The watchdog (CLKMD bit 1, on after reset) times out after 8K, 16K, 64K or 256K ILRC clocks (MISC bits 0-1) without a
/// wdreset: the chip resets, and the run stops there as a failure. So does the instruction reset, which a runtime runs on
/// a fatal error (the PDK14 has no breakpoint). stopsys stops the clocks until a pin changes, stopexe the CPU until an
/// interrupt request (whose INTEN bit is on) or a pin change; when nothing is left that could wake it (no input to come,
/// no timer whose interrupt is enabled or that drives a pin), the program has finished. PADIER and PBDIER are kept but
/// change nothing: every pin reads its level, and every input that changes from outside wakes the chip (a pin a timer
/// drives does not). The comparator, PWMG, RFC and LCD driver are registers only.
/// </para>
/// </summary>
public sealed class Pfs154 : SimulatedChip, IPdk14Io
{
    /// <summary>Program memory, in words.</summary>
    public const int RomWords = 2048;

    /// <summary>RAM, in bytes.</summary>
    public const int RamBytes = 128;

    /// <summary>Ticks of an ILRC clock: 16 MHz / 291 = 54.98 kHz.</summary>
    public const int TicksPerIlrc = 291;

    /// <summary>CLKMD after reset: the ILRC is the system clock, IHRC and ILRC on, the watchdog on.</summary>
    public const byte ClkmdAtReset = 0xF6;

    /// <summary>The word addresses of the factory's trim values (IHRCR's, BGTR's), each a <c>ret k</c> of this value.</summary>
    public const int IhrcTrimWord = 0x7ED, BandgapTrimWord = 0x7EE;

    /// <summary>The value the factory's trim words return: any will do, the simulated IHRC being exactly 16 MHz.</summary>
    public const byte FactoryTrim = 0x80;

    private const int Clkmd = 0x03, Inten = 0x04, Intrq = 0x05, T16m = 0x06, Misc = 0x08, Tm2b = 0x09, Integs = 0x0C,
        Pa = 0x10, Pac = 0x11, Paph = 0x12, Pb = 0x14, Pbc = 0x15, Pbph = 0x16, Tm2s = 0x17, Tm2c = 0x1C, Tm2ct = 0x1D,
        Tm3c = 0x32, Tm3ct = 0x33, Tm3s = 0x34, Tm3b = 0x35;

    private const byte Pa0Request = 0x01, Pb0Request = 0x02, T16Request = 0x04, Tm2Request = 0x40, Tm3Request = 0x80;

    /// <summary>The I/O registers the simulator models, by the names of the chip's target file (targets/chip/pfs154.json).</summary>
    public static IReadOnlyDictionary<string, int> IoRegisters { get; } = new Dictionary<string, int>
    {
        ["flag"] = Pdk14Cpu.FlagAddress, ["sp"] = Pdk14Cpu.SpAddress, ["clkmd"] = Clkmd, ["inten"] = Inten,
        ["intrq"] = Intrq, ["t16m"] = T16m, ["misc"] = Misc, ["tm2b"] = Tm2b, ["integs"] = Integs, ["padier"] = 0x0D,
        ["pbdier"] = 0x0E, ["pa"] = Pa, ["pac"] = Pac, ["paph"] = Paph, ["pb"] = Pb, ["pbc"] = Pbc, ["pbph"] = Pbph,
        ["tm2s"] = Tm2s, ["tm2c"] = Tm2c, ["tm2ct"] = Tm2ct, ["tm3c"] = Tm3c, ["tm3ct"] = Tm3ct, ["tm3s"] = Tm3s,
        ["tm3b"] = Tm3b,
    };

    /// <summary>The interrupt requests the simulator raises, as INTRQ / INTEN bit numbers, by the target file's names.</summary>
    public static IReadOnlyDictionary<string, int> InterruptBits { get; } = new Dictionary<string, int>
    {
        ["pa0"] = 0, ["pb0"] = 1, ["t16"] = 2, ["tm2"] = 6, ["tm3"] = 7,
    };

    private readonly Pdk14Cpu _cpu;
    private readonly bool[] _loaded;
    private readonly byte[] _io = new byte[64];
    private readonly GpioPort _pa;
    private readonly GpioPort _pb;
    private readonly Timer8 _tm2;
    private readonly Timer8 _tm3;
    private readonly int _imageBytes;
    private long _ticksPerCycle;
    private ushort _t16;
    private long _t16Ticks;
    private int _t16Edges;
    private long _watchdogTicks;

    /// <summary>A PFS154 with <paramref name="image"/> in its program memory (see <see cref="Load"/>), just after reset.</summary>
    public Pfs154(IntelHexImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rom = new ushort[RomWords];
        _loaded = new bool[RomWords];
        for (var word = 0; word < RomWords; word++)
        {
            rom[word] = (ushort)((image.Bytes[2 * word] | (image.Bytes[2 * word + 1] << 8)) & 0x3FFF);
            _loaded[word] = image.Loaded[2 * word] || image.Loaded[2 * word + 1];
        }

        foreach (var word in new[] { IhrcTrimWord, BandgapTrimWord })
        {
            if (!_loaded[word])
            {
                rom[word] = 0x0200 | FactoryTrim; // ret #k
                _loaded[word] = true;
            }
        }

        _imageBytes = image.Count;
        _cpu = new Pdk14Cpu(rom, new byte[RamBytes], this);
        _pa = new GpioPort(this, 'A', 0xF9) { PushPull = 0xDF }; // PA5 is open-drain
        _pb = new GpioPort(this, 'B', 0xFF);
        _pa.LevelChanged = (bit, high) => PinChanged(_pa, bit, high);
        _pb.LevelChanged = (bit, high) => PinChanged(_pb, bit, high);
        Ports = [_pa, _pb];
        _tm2 = new Timer8(this, Tm2Request, [null, (_pb, 2), (_pa, 3), (_pb, 4)]);
        _tm3 = new Timer8(this, Tm3Request, [null, (_pb, 5), (_pb, 6), (_pb, 7)]);
        _io[Clkmd] = ClkmdAtReset;
        _io[0x0D] = 0xFF; // PADIER, PBDIER
        _io[0x0E] = 0xFF;
        UpdateClock();
    }

    /// <summary>Loads an Intel HEX file (as <c>sdcc -mpdk14</c> writes it) into a new PFS154.</summary>
    public static Pfs154 Load(string path) =>
        new(IntelHexImage.Load(path, 0, RomWords * 2, 0xFF, "the PFS154's 2K words of program memory (byte addresses)"));

    public override string Name => "PFS154";

    public override IReadOnlyList<GpioPort> Ports { get; }

    public override int ImageBytes => _imageBytes;

    /// <summary>The PC as a byte address, as SDCC's listings and .map give code addresses.</summary>
    public override int ProgramCounter => _cpu.Pc * 2;

    /// <summary>The CPU: its registers and RAM.</summary>
    public Pdk14Cpu Cpu => _cpu;

    public override byte[] Ram => _cpu.Ram;

    /// <summary>The system clock's frequency, in hertz.</summary>
    public double SystemClockHertz => (double)TicksPerSecond / _ticksPerCycle;

    ushort IPdk14Io.Timer16
    {
        get => _t16;
        set => _t16 = value;
    }

    byte IPdk14Io.Read(int address) => address switch
    {
        Pa => _pa.Levels,
        Pac => _pa.Direction,
        Paph => _pa.PullUp,
        Pb => _pb.Levels,
        Pbc => _pb.Direction,
        Pbph => _pb.PullUp,
        Tm2ct => _tm2.Counter,
        Tm3ct => _tm3.Counter,
        _ => _io[address],
    };

    void IPdk14Io.Write(int address, byte value)
    {
        _io[address] = value;
        switch (address)
        {
            case Clkmd:
                UpdateClock();
                break;
            case Pa:
                _pa.Output = value;
                _pa.Update();
                break;
            case Pac:
                _pa.Direction = value;
                _pa.Update();
                break;
            case Paph:
                _pa.PullUp = value;
                _pa.Update();
                break;
            case Pb:
                _pb.Output = value;
                _pb.Update();
                break;
            case Pbc:
                _pb.Direction = value;
                _pb.Update();
                break;
            case Pbph:
                _pb.PullUp = value;
                _pb.Update();
                break;
            case T16m:
                _t16Ticks = 0;
                _t16Edges = 0;
                break;
            case Tm2c or Tm2s or Tm2b:
                _tm2.Configure(_io[Tm2c], _io[Tm2s], _io[Tm2b]);
                break;
            case Tm2ct:
                _tm2.Counter = value;
                break;
            case Tm3c or Tm3s or Tm3b:
                _tm3.Configure(_io[Tm3c], _io[Tm3s], _io[Tm3b]);
                break;
            case Tm3ct:
                _tm3.Counter = value;
                break;
        }
    }

    void IPdk14Io.ResetWatchdog() => _watchdogTicks = 0;

    protected override void Step(long limit)
    {
        switch (_cpu.Sleep)
        {
            case Pdk14Sleep.StopSys:
                // the clocks have stopped: only a pin that changes (an input to come) wakes the chip
                if (!InputsAhead)
                {
                    Finish("the program has finished: stopsys stopped the clocks, and no input is left to wake the chip");
                    return;
                }

                Ticks = Math.Max(Ticks, limit);
                return;
            case Pdk14Sleep.StopExe:
                var next = TicksToNextEvent();
                if (next == long.MaxValue && !InputsAhead)
                {
                    Finish("the program has finished: stopexe stopped the CPU, and nothing is left to wake it " +
                        "(no input to come, no timer whose interrupt is enabled)");
                    return;
                }

                var ticks = Math.Clamp(Math.Min(next, limit - Ticks), 1, long.MaxValue);
                Ticks += ticks;
                Advance(ticks);
                if ((_io[Inten] & _io[Intrq]) != 0)
                {
                    _cpu.Sleep = Pdk14Sleep.None;
                }

                TakeInterrupt();
                return;
        }

        if (!_loaded[_cpu.Pc])
        {
            throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                $"the program ran into program memory the image does not fill, at pc 0x{_cpu.Pc * 2:x4} (word 0x{_cpu.Pc:x3})"));
        }

        RunHook(_cpu.Pc * 2);
        var at = _cpu.Pc;
        var cycles = _cpu.Step();
        if (_cpu.ResetRequested)
        {
            Fail(string.Create(CultureInfo.InvariantCulture,
                $"the program reset the chip (reset) at pc 0x{at * 2:x4}, as a runtime does on a fatal error"));
            return;
        }

        var elapsed = cycles * _ticksPerCycle;
        Ticks += elapsed;
        Advance(elapsed);
        TakeInterrupt();
    }

    private void TakeInterrupt()
    {
        if (_cpu.InterruptsEnabled && (_io[Inten] & _io[Intrq]) != 0)
        {
            var elapsed = _cpu.Interrupt() * _ticksPerCycle;
            Ticks += elapsed;
            Advance(elapsed);
        }
    }

    /// <summary>Lets <paramref name="ticks"/> pass for the timers and the watchdog.</summary>
    private void Advance(long ticks)
    {
        var t16Clock = T16Clock();
        if (t16Clock > 0)
        {
            _t16Ticks += ticks;
            if (_t16Ticks >= t16Clock)
            {
                var counts = _t16Ticks / t16Clock;
                _t16Ticks -= counts * t16Clock;
                CountT16((int)Math.Min(counts, 0x10000));
            }
        }

        _tm2.Advance(ticks);
        _tm3.Advance(ticks);
        if ((_io[Clkmd] & 0x06) == 0x06) // the watchdog on, with the ILRC
        {
            _watchdogTicks += ticks;
            if (_watchdogTicks >= WatchdogTimeout)
            {
                Fail(string.Create(CultureInfo.InvariantCulture,
                    $"the watchdog reset the chip: the program ran no wdreset for {WatchdogTimeout / TicksPerIlrc} ILRC " +
                    $"clocks (CLKMD bit 1 turns the watchdog off; it is on after reset)"));
            }
        }
    }

    /// <summary>The watchdog's time-out, in ticks: 8K, 16K, 64K or 256K ILRC clocks.</summary>
    private long WatchdogTimeout => ((_io[Misc] & 3) switch { 0 => 8192L, 1 => 16384L, 2 => 65536L, _ => 262144L }) * TicksPerIlrc;

    /// <summary>Ticks of a Timer16 count (its clock, divided), or 0 when it counts pin edges or nothing.</summary>
    private long T16Clock()
    {
        var mode = _io[T16m];
        long source = (mode >> 5) switch
        {
            1 => _ticksPerCycle,
            4 => 1,
            6 => TicksPerIlrc,
            _ => 0, // off, the external oscillator, or pin edges
        };
        return source * T16Divider(mode);
    }

    private static int T16Divider(byte mode) => 1 << (2 * ((mode >> 3) & 3));

    /// <summary>Timer16 counts <paramref name="counts"/> times; its interrupt is requested when the bit T16M selects rises (or falls).</summary>
    private void CountT16(int counts)
    {
        var bit = 8 + (_io[T16m] & 7);
        var period = 1 << (bit + 1);
        var before = _t16;
        var after = before + counts;
        // the selected edges between: the counts where bit goes 0 -> 1 (or 1 -> 0) on the way from before to after
        var target = (_io[Integs] & 0x10) == 0 ? 1 << bit : 0;
        var edges = Floor(after - target, period) - Floor(before - target, period);
        _t16 = (ushort)after;
        if (edges > 0)
        {
            _io[Intrq] |= T16Request;
        }

        static long Floor(long value, int period) => value >= 0 ? value / period : -((-value + period - 1) / period);
    }

    /// <summary>
    /// The ticks until a timer can next request an interrupt or change its pin (or the watchdog time out): what a CPU in
    /// stopexe must not skip; long.MaxValue when nothing will.
    /// </summary>
    private long TicksToNextEvent()
    {
        var next = long.MaxValue;
        var t16Clock = T16Clock();
        if (t16Clock > 0 && (_io[Inten] & T16Request) != 0)
        {
            var bit = 8 + (_io[T16m] & 7);
            var period = 1 << (bit + 1);
            var target = (_io[Integs] & 0x10) == 0 ? 1 << bit : 0;
            var counts = (target - (_t16 % period) + period) % period;
            counts = counts == 0 ? period : counts;
            next = Math.Min(next, counts * t16Clock - _t16Ticks);
        }

        next = Math.Min(next, _tm2.TicksToEvent((_io[Inten] & Tm2Request) != 0));
        next = Math.Min(next, _tm3.TicksToEvent((_io[Inten] & Tm3Request) != 0));

        if ((_io[Clkmd] & 0x06) == 0x06)
        {
            next = Math.Min(next, WatchdogTimeout - _watchdogTicks);
        }

        return next;
    }

    private void UpdateClock()
    {
        var clkmd = _io[Clkmd];
        var select = clkmd >> 5;
        long ticks = (clkmd & 0x08) == 0
            ? select switch { 0 => 4, 1 => 2, 2 => 1, 6 => 4 * TicksPerIlrc, 7 => TicksPerIlrc, _ => 0 }
            : select switch { 0 => 16, 1 => 8, 2 => 16 * TicksPerIlrc, 3 => 32, 4 => 64, 6 => 2 * TicksPerIlrc, _ => 0 };
        if (ticks == 0)
        {
            throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                $"the program set CLKMD to 0x{clkmd:x2}: a system clock the simulator does not have (the external " +
                $"oscillator, or a reserved setting)"));
        }

        _ticksPerCycle = ticks;
    }

    /// <summary>A pin's level changed: PA0's and PB0's interrupts, the timers' pin clocks, waking up.</summary>
    private void PinChanged(GpioPort port, int bit, bool high)
    {
        if (bit == 0)
        {
            var edges = port == _pa ? _io[Integs] & 3 : (_io[Integs] >> 2) & 3;
            if (edges == 0 || (edges == 1 && high) || (edges == 2 && !high))
            {
                _io[Intrq] |= port == _pa ? Pa0Request : Pb0Request;
            }
        }

        // Timer16 counts falling edges of PA0 (source 7) or PA4 (source 3)
        var source = _io[T16m] >> 5;
        if (!high && port == _pa && ((source == 7 && bit == 0) || (source == 3 && bit == 4)) &&
            ++_t16Edges >= T16Divider(_io[T16m]))
        {
            _t16Edges = 0;
            CountT16(1);
        }

        _tm2.PinEdge(port == _pa, bit, high);
        _tm3.PinEdge(port == _pa, bit, high);
        var mask = 1 << bit;
        if ((port.Direction & mask) == 0 && (port.OutsideDriven & mask) != 0 && (port.PeripheralDriven & mask) == 0)
        {
            _cpu.Sleep = Pdk14Sleep.None; // an input changed from outside: that wakes the chip from stopsys and stopexe
        }
    }

    /// <summary>Timer2 or Timer3: an 8-bit counter with a period mode and a PWM mode, its interrupt, its output pin.</summary>
    private sealed class Timer8(Pfs154 chip, byte request, (GpioPort Port, int Bit)?[] outputs)
    {
        private byte _control;
        private byte _scale;
        private byte _bound;
        private long _ticks;
        private int _edges;
        private bool _toggle;
        private bool? _level;
        private (GpioPort Port, int Bit)? _output;

        public byte Counter { get; set; }

        private bool PwmMode => (_control & 0x02) != 0;

        private int Top => (_scale & 0x80) != 0 ? 63 : 255;

        private int Divider => (1 << (2 * ((_scale >> 5) & 3))) * ((_scale & 0x1F) + 1);

        /// <summary>Ticks of a count, or 0 when the timer counts pin edges or nothing.</summary>
        private long CountTicks => (_control >> 4) switch
        {
            1 => chip._ticksPerCycle * Divider,
            2 => Divider,
            4 => (long)TicksPerIlrc * Divider,
            _ => 0,
        };

        public void Configure(byte control, byte scale, byte bound)
        {
            _control = control;
            _scale = scale;
            _bound = bound;
            var output = outputs[(control >> 2) & 3];
            if (output != _output && _output is var (oldPort, oldBit))
            {
                oldPort.DriveFromPeripheral(oldBit, false, false, 0);
            }

            _output = output;
            _level = null;
            Drive();
        }

        public void Advance(long ticks)
        {
            var countTicks = CountTicks;
            if (countTicks == 0)
            {
                return;
            }

            _ticks += ticks;
            var end = chip.Ticks;
            while (_ticks >= countTicks)
            {
                _ticks -= countTicks;
                chip.SetTime(end - _ticks); // the count's own tick, for the pin's edge
                Count();
            }

            chip.SetTime(end);
        }

        /// <summary>A pin's edge, which the timer counts when its clock is that pin's (TM2C bits 4-7: 8-13).</summary>
        public void PinEdge(bool portA, int bit, bool high)
        {
            var source = _control >> 4;
            var counted = source switch
            {
                8 or 9 => portA && bit == 0 && high == (source == 8),
                10 or 11 => !portA && bit == 0 && high == (source == 10),
                12 or 13 => portA && bit == 4 && high == (source == 12),
                _ => false,
            };
            if (counted && ++_edges >= Divider)
            {
                _edges = 0;
                Count();
            }
        }

        /// <summary>
        /// The ticks until the counter next reaches its bound (when <paramref name="interrupt"/> is enabled) or changes its
        /// output pin, or long.MaxValue when neither happens by itself.
        /// </summary>
        public long TicksToEvent(bool interrupt)
        {
            var countTicks = CountTicks;
            if (countTicks == 0 || (!interrupt && _output is null))
            {
                return long.MaxValue;
            }

            // the bound, and in PWM mode the wrap to 0 too (the pin rises there)
            var counts = PwmMode ? ((_bound - Counter - 1) & Top) + 1 : ((_bound - Counter) & 0xFF) + 1;
            if (PwmMode && _output is not null)
            {
                counts = Math.Min(counts, Top - Counter + 1);
            }

            return counts * countTicks - _ticks;
        }

        private void Count()
        {
            if (PwmMode)
            {
                Counter = (byte)((Counter + 1) & Top);
                if (Counter == (_bound & Top))
                {
                    chip._io[Intrq] |= request;
                }
            }
            else if (Counter == _bound)
            {
                Counter = 0;
                _toggle = !_toggle;
                chip._io[Intrq] |= request;
            }
            else
            {
                Counter++;
            }

            Drive();
        }

        /// <summary>Drives the output pin, when one is selected: the toggle in period mode, below the bound in PWM mode.</summary>
        private void Drive()
        {
            if (_output is not var (port, bit))
            {
                return;
            }

            var high = (PwmMode ? Counter < _bound : _toggle) ^ ((_control & 1) != 0);
            if (high == _level)
            {
                return;
            }

            _level = high;
            var period = PwmMode ? (Top + 1) * CountTicks : 2 * (_bound + 1) * CountTicks;
            port.DriveFromPeripheral(bit, true, high, period > 0 ? period : TicksPerSecond);
        }
    }
}
