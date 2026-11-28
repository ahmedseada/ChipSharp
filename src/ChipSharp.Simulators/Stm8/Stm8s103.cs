using System.Globalization;

namespace ChipSharp.Simulators.Stm8;

/// <summary>
/// An STM8S103F3 (the 20-pin package): the STM8 core (<see cref="Stm8Cpu"/>), 8 KB of flash at 0x8000, 1 KB of RAM at
/// 0x0000 (SP starts at 0x03FF), 640 bytes of EEPROM at 0x4000 and the option bytes (read only: writing them, or flash,
/// changes nothing), the I/O registers at 0x5000 and the CPU's and the ITC's at 0x7F00, as the datasheet maps them.
/// Writing any other address stops the run as a failure, and so does popping more than was pushed; reading one gives 0
/// (SDCC's library reads such an address to skip two bytes: bcp a, longmem).
/// <para>
/// Peripherals: the ports (PA1-PA3, PB4-PB5 open-drain, PC3-PC7, PD1-PD6; ODR, IDR, DDR, CR1 for push-pull or pull-up,
/// CR2 for the external interrupt) and EXTI (vectors 3-7, by EXTI_CR1/CR2's edges, the level mode included); the ITC
/// (ITC_SPR1-8: the software priorities, then the hardware order); CLK (the HSI at 16 MHz divided by CKDIVR's HSIDIV for
/// fMASTER, 2 MHz after reset, and by CPUDIV for the CPU; switching to the LSI, 128 kHz; there is no HSE crystal, so a
/// switch to it never ends); TIM1, TIM2 (<see cref="Stm8Timer16"/>), TIM4 (<see cref="Tim4"/>); UART1
/// (<see cref="Stm8Uart"/>: TX on PD5 printed as text, RX on PD6 from --send); the independent watchdog (it resets the
/// chip, which stops the run as a failure). SPI, I2C, the ADC, AWU, BEEP and WWDG are registers only.
/// </para>
/// <para>
/// The image is Intel HEX (sdcc -mstm8) for the flash, and the EEPROM if it has any; flash it does not fill reads 0x00
/// (erased), and running into it stops the run as a failure. The CPU starts at 0x8000, the reset vector (an int
/// instruction), with interrupts off (I1:I0 = 11). The run stops as a failure at break, at an interrupt whose vector does
/// not point anywhere (int 0x000000), and when the program halts or waits for an interrupt (halt, wfi) that nothing can
/// raise any more, it has finished.
/// </para>
/// </summary>
public sealed class Stm8s103 : SimulatedChip, IStm8Bus
{
    public const int FlashStart = 0x8000, FlashSize = 8 * 1024, RamSize = 1024, EepromStart = 0x4000, EepromSize = 640;

    /// <summary>The vector of interrupt 0: interrupt n's is at 0x8008 + 4n.</summary>
    public const int FirstVector = 0x8008;

    private const int IoStart = 0x5000, IoEnd = 0x5800, CpuStart = 0x7F00;
    private const int ExtiCr1 = 0x50A0, ExtiCr2 = 0x50A1;
    private const int ClkIckr = 0x50C0, ClkCmsr = 0x50C3, ClkSwr = 0x50C4, ClkSwcr = 0x50C5, ClkCkdivr = 0x50C6;
    private const int IwdgKr = 0x50E0, IwdgPr = 0x50E1, IwdgRlr = 0x50E2;
    private const int Uart1 = 0x5230, Tim1 = 0x5250, Tim2 = 0x5300, Tim4Base = 0x5340;
    private const byte Hsi = 0xE1, Lsi = 0xD2;

    /// <summary>The interrupts the simulator raises, by the names of the chip's target file (targets/chip/stm8s103f3.json).</summary>
    public static IReadOnlyDictionary<string, int> Interrupts { get; } = new Dictionary<string, int>
    {
        ["clk"] = 2, ["extiA"] = 3, ["extiB"] = 4, ["extiC"] = 5, ["extiD"] = 6, ["extiE"] = 7, ["tim1Update"] = 11,
        ["tim1Capture"] = 12, ["tim2Update"] = 13, ["tim2Capture"] = 14, ["uart1Tx"] = 17, ["uart1Rx"] = 18,
        ["tim4Update"] = 23,
    };

    /// <summary>The first registers of the peripherals the simulator models, by the target file's names.</summary>
    public static IReadOnlyDictionary<string, int> Peripherals { get; } = new Dictionary<string, int>
    {
        ["exti"] = ExtiCr1, ["clk"] = ClkIckr, ["iwdg"] = IwdgKr, ["uart1"] = Uart1, ["tim1"] = Tim1, ["tim2"] = Tim2,
        ["tim4"] = Tim4Base, ["itc"] = CpuStart + 0x70,
    };

    /// <summary>The address of a port's first register (ODR): PA's 0x5000, then 5 registers a port.</summary>
    public static int PortAddress(char letter) => IoStart + 5 * (char.ToUpperInvariant(letter) - 'A');

    private static readonly string[] InterruptNames =
    [
        "TLI", "AWU", "CLK", "EXTI0 (port A)", "EXTI1 (port B)", "EXTI2 (port C)", "EXTI3 (port D)", "EXTI4 (port E)",
        "8", "9", "SPI", "TIM1 update", "TIM1 capture/compare", "TIM2 update", "TIM2 capture/compare", "15", "16",
        "UART1 TX", "UART1 RX", "I2C", "20", "21", "ADC1", "TIM4 update", "flash",
    ];

    private readonly Stm8Cpu _cpu;
    private readonly byte[] _ram = new byte[RamSize];
    private readonly byte[] _flash;
    private readonly bool[] _flashLoaded;
    private readonly byte[] _eeprom;
    private readonly byte[] _io = new byte[IoEnd - IoStart];
    private readonly byte[] _cpuRegisters = new byte[0x100];
    private readonly GpioPort[] _ports;
    private readonly bool[] _extiPending = new bool[5];
    private readonly Tim4 _tim4 = new();
    private readonly Stm8Timer16 _tim1;
    private readonly Stm8Timer16 _tim2;
    private readonly Stm8Uart _uart;
    private readonly int _imageBytes;
    private long _ticksPerMaster;
    private long _cpuDivider;
    private long _tickRemainder;
    private bool _iwdgRunning;
    private long _iwdgTicks;

    /// <summary>An STM8S103 with <paramref name="flash"/> (and <paramref name="eeprom"/>, or nothing), just after reset.</summary>
    public Stm8s103(IntelHexImage flash, IntelHexImage? eeprom = null)
    {
        ArgumentNullException.ThrowIfNull(flash);
        _flash = flash.Bytes;
        _flashLoaded = flash.Loaded;
        _eeprom = eeprom?.Bytes ?? new byte[EepromSize];
        _imageBytes = flash.Count + (eeprom?.Count ?? 0);
        _cpu = new Stm8Cpu(this) { SP = RamSize - 1, PC = FlashStart, StackTop = RamSize - 1 };
        _ports =
        [
            new GpioPort(this, 'A', 0x0E), new GpioPort(this, 'B', 0x30), new GpioPort(this, 'C', 0xF8),
            new GpioPort(this, 'D', 0x7E), new GpioPort(this, 'E', 0x00), new GpioPort(this, 'F', 0x00),
        ];
        for (var index = 0; index < _ports.Length; index++)
        {
            var port = index;
            _ports[index].LevelChanged = (bit, high) => PinChanged(port, bit, high);
        }

        foreach (var port in _ports)
        {
            port.PushPull = 0; // CR1 0: open-drain outputs, inputs without pull-ups
        }

        _io[0x500F + 3 - IoStart] = 0x02; // PD_CR1: PD1, SWIM, has its pull-up after reset
        _ports[3].PullUp = 0x02;
        _ports[3].PushPull = 0x02;
        _ports[3].Update();
        _tim1 = Stm8Timer16.Tim1(this, [(_ports[2], 6), (_ports[2], 7), (_ports[2], 3), (_ports[2], 4)], () => _ticksPerMaster);
        _tim2 = Stm8Timer16.Tim2(this, [(_ports[3], 4), (_ports[3], 3), (_ports[0], 3)], () => _ticksPerMaster);
        _uart = new Stm8Uart(this, "UART1", () => _ticksPerMaster);
        Reset();
    }

    /// <summary>Loads an Intel HEX file (as <c>sdcc -mstm8</c> writes it) into a new STM8S103: its flash, and its EEPROM.</summary>
    public static Stm8s103 Load(string path)
    {
        // the EEPROM's bytes, if the file has any, then the rest, which must all be flash
        var whole = IntelHexImage.Load(path, 0, FlashStart + FlashSize, 0x00, "the STM8S103's memory");
        var flash = new byte[FlashSize];
        var loaded = new bool[FlashSize];
        Array.Copy(whole.Bytes, FlashStart, flash, 0, FlashSize);
        Array.Copy(whole.Loaded, FlashStart, loaded, 0, FlashSize);
        for (var address = 0; address < FlashStart; address++)
        {
            if (whole.Loaded[address] && address is < EepromStart or >= EepromStart + EepromSize)
            {
                throw new ImageException(string.Create(CultureInfo.InvariantCulture,
                    $"'{path}' has bytes at 0x{address:x4}, outside the STM8S103's flash (0x8000-0x9fff) and EEPROM " +
                    $"(0x4000-0x427f)."));
            }
        }

        var flashCount = loaded.Count(b => b);
        if (flashCount == 0)
        {
            throw new ImageException($"'{path}' has nothing for the flash (0x8000-0x9fff), where the STM8S103 starts.");
        }

        return new Stm8s103(Image(flash, loaded, flashCount, FlashStart), Image(whole.Bytes[EepromStart..(EepromStart + EepromSize)],
            whole.Loaded[EepromStart..(EepromStart + EepromSize)], whole.Count - flashCount, EepromStart));

        static IntelHexImage Image(byte[] bytes, bool[] loaded, int count, int start) =>
            IntelHexImage.FromBytes(start, bytes, loaded, count);
    }

    public override string Name => "STM8S103";

    public override IReadOnlyList<GpioPort> Ports => _ports;

    public override ISerialInput? SerialInput => _uart;

    public override SerialMonitor? SerialOutput => _uart.Output;

    public override int ImageBytes => _imageBytes;

    public override int ProgramCounter => _cpu.PC;

    /// <summary>The CPU: its registers.</summary>
    public Stm8Cpu Cpu => _cpu;

    /// <summary>The RAM (0x0000-0x03FF).</summary>
    public override byte[] Ram => _ram;

    /// <summary>fMASTER, in hertz.</summary>
    public double MasterClockHertz => (double)TicksPerSecond / _ticksPerMaster;

    /// <summary>The CPU's clock, in hertz.</summary>
    public double CpuClockHertz => MasterClockHertz / _cpuDivider;

    public byte Read(int address)
    {
        if (address < RamSize)
        {
            return _ram[address];
        }

        if (address is >= FlashStart and < FlashStart + FlashSize)
        {
            return _flash[address - FlashStart];
        }

        if (address is >= IoStart and < IoEnd)
        {
            return ReadIo(address);
        }

        if (address is >= CpuStart and <= 0x7FFF)
        {
            return ReadCpuRegister(address);
        }

        if (address is >= EepromStart and < EepromStart + EepromSize)
        {
            return _eeprom[address - EepromStart];
        }

        if (address is >= 0x4800 and <= 0x480A)
        {
            return (byte)(address is 0x4800 || (address & 1) == 1 ? 0x00 : 0xFF); // the option bytes, as from the factory
        }

        if (address is >= 0x4865 and <= 0x4870)
        {
            return (byte)(address - 0x4865 + 0x30); // the unique ID
        }

        // nothing there: SDCC's library reads such addresses on purpose (bcp a, 0xa601 skips the ld a, #1 it hides)
        return 0;
    }

    public void Write(int address, byte value)
    {
        if (address < RamSize)
        {
            _ram[address] = value;
        }
        else if (address is >= IoStart and < IoEnd)
        {
            WriteIo(address, value);
        }
        else if (address is >= CpuStart and <= 0x7FFF)
        {
            WriteCpuRegister(address, value);
        }
        else if (address is not (>= FlashStart and < FlashStart + FlashSize) and not (>= EepromStart and < EepromStart + EepromSize)
                 and not (>= 0x4800 and <= 0x480A))
        {
            throw Outside("wrote", address); // writing flash, EEPROM or an option byte without unlocking it does nothing
        }
    }

    protected override void Step(long limit)
    {
        switch (_cpu.Sleep)
        {
            case Stm8Sleep.Halt:
                // the clocks have stopped: only an external interrupt (a pin that changes, an input to come) wakes the CPU
                if (TakeInterrupt())
                {
                    return;
                }

                if (!InputsAhead || !AnyExternalInterrupt())
                {
                    Finish("the program has finished: the CPU halted (halt), and nothing is left to wake it " +
                        "(no external interrupt to come)");
                    return;
                }

                Ticks = Math.Max(Ticks, limit);
                return;
            case Stm8Sleep.Wait:
                if (TakeInterrupt())
                {
                    return;
                }

                var next = TicksToEvent();
                if (next == long.MaxValue && !(InputsAhead && (AnyExternalInterrupt() || _uart.CanInterrupt)))
                {
                    Finish("the program has finished: the CPU waits for an interrupt (wfi) that nothing can raise any more");
                    return;
                }

                var ticks = Math.Clamp(Math.Min(next, limit - Ticks), 1, long.MaxValue);
                Ticks += ticks;
                AdvanceTicks(ticks);
                TakeInterrupt();
                return;
        }

        var pc = _cpu.PC;
        if (pc is >= FlashStart and < FlashStart + FlashSize ? !_flashLoaded[pc - FlashStart] : pc >= RamSize)
        {
            var where = pc.ToString("x4", CultureInfo.InvariantCulture);
            throw new ProgramFault(pc is >= FlashStart and < FlashStart + FlashSize
                ? $"the program ran into flash the image does not fill, at pc 0x{where}"
                : $"the program jumped to 0x{where}, where there is no flash or RAM to run");
        }

        RunHook(pc);
        var cycles = _cpu.Step();
        if (_cpu.BreakHit)
        {
            Fail(string.Create(CultureInfo.InvariantCulture,
                $"the program hit a breakpoint (break) at pc 0x{_cpu.PC:x4}, as a runtime does on a fatal error"));
            return;
        }

        var elapsed = cycles * _ticksPerMaster * _cpuDivider;
        Ticks += elapsed;
        AdvanceMaster(cycles * _cpuDivider, elapsed);
        TakeInterrupt();
    }

    /// <summary>The interrupts there are, by vector number: whether each is requested.</summary>
    private bool Pending(int irq) => irq switch
    {
        2 => (_io[ClkSwcr - IoStart] & 0x0C) == 0x0C, // SWIF with SWIEN
        >= 3 and <= 7 => ExternalPending(irq - 3),
        11 => _tim1.UpdatePending,
        12 => _tim1.ComparePending,
        13 => _tim2.UpdatePending,
        14 => _tim2.ComparePending,
        17 => _uart.TransmitPending,
        18 => _uart.ReceivePending,
        23 => _tim4.InterruptPending,
        _ => false,
    };

    /// <summary>Takes the interrupt to take, if there is one: the highest software priority, then the lowest vector.</summary>
    private bool TakeInterrupt()
    {
        var level = _cpu.Level;
        if (level == 3)
        {
            return false;
        }

        var best = -1;
        var bestLevel = level;
        for (var irq = 0; irq < InterruptNames.Length; irq++)
        {
            if (!Pending(irq))
            {
                continue;
            }

            var irqLevel = Stm8Cpu.LevelOf(PriorityCode(irq));
            if (irqLevel > bestLevel)
            {
                best = irq;
                bestLevel = irqLevel;
            }
        }

        if (best < 0)
        {
            return false;
        }

        var vector = FirstVector + 4 * best;
        var handler = (Read(vector + 1) << 16) | (Read(vector + 2) << 8) | Read(vector + 3);
        if (Read(vector) != 0x82 || handler == 0)
        {
            throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                $"interrupt {best} ({InterruptNames[best]}) was requested, but its vector at 0x{vector:x4} has no handler"));
        }

        if (best is >= 3 and <= 7)
        {
            _extiPending[best - 3] = false; // cleared as the vector is fetched
        }

        var elapsed = _cpu.Interrupt(handler, PriorityCode(best)) * _ticksPerMaster * _cpuDivider;
        Ticks += elapsed;
        AdvanceMaster(_cpuDivider * 9, elapsed);
        return true;
    }

    /// <summary>The I1:I0 code of an interrupt's software priority (ITC_SPR1-8); 10, which is not allowed, counts as 11.</summary>
    private int PriorityCode(int irq)
    {
        var code = (_cpuRegisters[0x70 + irq / 4] >> (2 * (irq % 4))) & 3;
        return code == 2 ? 3 : code;
    }

    private bool ExternalPending(int port)
    {
        if (_extiPending[port])
        {
            return true;
        }

        // falling edge and low level (sensitivity 00): requested while an enabled pin is low
        return Sensitivity(port) == 0 && (InterruptPins(port) & ~_ports[port].Levels) != 0;
    }

    /// <summary>The pins of a port whose external interrupt is on: inputs with CR2 set.</summary>
    private byte InterruptPins(int port)
    {
        var p = _ports[port];
        return (byte)(_io[0x04 + 5 * port] & ~p.Direction & p.Pins);
    }

    private int Sensitivity(int port) => port < 4 ? (_io[ExtiCr1 - IoStart] >> (2 * port)) & 3 : _io[ExtiCr2 - IoStart] & 3;

    private bool AnyExternalInterrupt()
    {
        for (var port = 0; port < 5; port++)
        {
            if (InterruptPins(port) != 0 && PriorityCode(3 + port) != 2)
            {
                return true;
            }
        }

        return false;
    }

    private void PinChanged(int port, int bit, bool high)
    {
        if (port < 5 && (InterruptPins(port) & (1 << bit)) != 0)
        {
            var sensitivity = Sensitivity(port);
            if (sensitivity == 3 || (sensitivity == 1 && high) || (sensitivity is 0 or 2 && !high))
            {
                _extiPending[port] = true;
            }
        }
    }

    /// <summary>Lets <paramref name="ticks"/> pass for the peripherals (while the CPU waits).</summary>
    private void AdvanceTicks(long ticks)
    {
        _tickRemainder += ticks;
        var clocks = _tickRemainder / _ticksPerMaster;
        _tickRemainder -= clocks * _ticksPerMaster;
        AdvanceMaster(clocks, ticks);
    }

    private void AdvanceMaster(long masterClocks, long ticks)
    {
        _tim4.Advance(masterClocks);
        _tim1.Advance(masterClocks);
        _tim2.Advance(masterClocks);
        _uart.Advance(ticks);
        if (_iwdgRunning)
        {
            _iwdgTicks -= ticks;
            if (_iwdgTicks <= 0)
            {
                Fail("the independent watchdog reset the chip: the program did not refresh it (IWDG_KR 0xAA) in time");
            }
        }
    }

    /// <summary>
    /// The ticks until a peripheral next does something a sleeping CPU must not skip (an interrupt it can raise, an edge
    /// of a timer's pin, the end of a byte going out), or long.MaxValue when none will.
    /// </summary>
    private long TicksToEvent()
    {
        var next = long.MaxValue;
        if (_tim4.CanInterrupt)
        {
            next = Math.Min(next, _tim4.ClocksToUpdate() * _ticksPerMaster - _tickRemainder);
        }

        if (_tim1.CanInterrupt || _tim1.DrivesPins)
        {
            next = Math.Min(next, _tim1.ClocksToEvent() * _ticksPerMaster - _tickRemainder);
        }

        if (_tim2.CanInterrupt || _tim2.DrivesPins)
        {
            next = Math.Min(next, _tim2.ClocksToEvent() * _ticksPerMaster - _tickRemainder);
        }

        next = Math.Min(next, _uart.TicksToEvent()); // a byte going out

        if (_iwdgRunning)
        {
            next = Math.Min(next, _iwdgTicks);
        }

        return next;
    }

    private byte ReadIo(int address)
    {
        var offset = address - IoStart;
        if (address is >= 0x5000 and < 0x501E)
        {
            var port = _ports[offset / 5];
            return (offset % 5) switch
            {
                0 => port.Output,
                1 => port.Levels,
                2 => port.Direction,
                _ => _io[offset],
            };
        }

        return address switch
        {
            ClkIckr => (byte)((_io[offset] & 0x2D) | ((_io[offset] & 1) << 1) | ((_io[offset] & 0x08) << 1)),
            >= Uart1 and < Uart1 + 11 => _uart.Read(address - Uart1),
            >= Tim1 and < Tim1 + 0x20 => _tim1.Read(address - Tim1),
            >= Tim2 and < Tim2 + 0x17 => _tim2.Read(address - Tim2),
            >= Tim4Base and < Tim4Base + 9 => _tim4.Read(address - Tim4Base),
            _ => _io[offset],
        };
    }

    private void WriteIo(int address, byte value)
    {
        var offset = address - IoStart;
        if (address is >= 0x5000 and < 0x501E)
        {
            var port = _ports[offset / 5];
            switch (offset % 5)
            {
                case 0:
                    port.Output = value;
                    break;
                case 1:
                    return; // IDR is read-only
                case 2:
                    port.Direction = value;
                    break;
                case 3: // CR1: the pull-ups of inputs, push-pull for outputs (PB4 and PB5 are true open-drain)
                    _io[offset] = value;
                    port.PullUp = value;
                    port.PushPull = (byte)(value & (offset / 5 == 1 ? 0xCF : 0xFF));
                    break;
                default:
                    _io[offset] = value;
                    break;
            }

            port.Update();
            return;
        }

        switch (address)
        {
            case >= Uart1 and < Uart1 + 11:
                _uart.Write(address - Uart1, value);
                return;
            case >= Tim1 and < Tim1 + 0x20:
                _tim1.Write(address - Tim1, value);
                return;
            case >= Tim2 and < Tim2 + 0x17:
                _tim2.Write(address - Tim2, value);
                return;
            case >= Tim4Base and < Tim4Base + 9:
                _tim4.Write(address - Tim4Base, value);
                return;
            case ClkCmsr:
                return; // read-only
            case ClkCkdivr:
                _io[offset] = (byte)(value & 0x1F);
                UpdateClock();
                return;
            case ClkSwr:
                _io[offset] = value;
                if (value != _io[ClkCmsr - IoStart])
                {
                    _io[ClkSwcr - IoStart] |= 0x01; // SWBSY
                    SwitchClock();
                }

                return;
            case ClkSwcr:
                _io[offset] = (byte)((_io[offset] & 0x09 & value) | (value & 0x06)); // SWIF is cleared by writing 0
                SwitchClock();
                return;
            case IwdgKr:
                Watchdog(value);
                return;
        }

        _io[offset] = value;
    }

    /// <summary>A clock switch (CLK_SWR, SWEN): to the HSI or the LSI it is done at once; to the HSE, which is not there, never.</summary>
    private void SwitchClock()
    {
        var target = _io[ClkSwr - IoStart];
        var swcr = _io[ClkSwcr - IoStart];
        if ((swcr & 0x01) == 0 || (swcr & 0x02) == 0 || target is not (Hsi or Lsi))
        {
            return;
        }

        _io[ClkCmsr - IoStart] = target;
        _io[ClkSwcr - IoStart] = (byte)((swcr & ~0x03) | 0x08); // done: SWBSY and SWEN off, SWIF on
        UpdateClock();
    }

    private void UpdateClock()
    {
        var ckdivr = _io[ClkCkdivr - IoStart];
        _ticksPerMaster = _io[ClkCmsr - IoStart] == Lsi ? 125 : 1L << ((ckdivr >> 3) & 3);
        _cpuDivider = 1L << (ckdivr & 7);
        _uart.ClockChanged();
    }

    /// <summary>IWDG_KR: 0xCC starts the watchdog, 0xAA refreshes it, 0x55 lets PR and RLR be written.</summary>
    private void Watchdog(byte key)
    {
        if (key is 0xCC)
        {
            _iwdgRunning = true;
        }

        if (key is 0xCC or 0xAA)
        {
            // (RLR + 1) counts of the LSI (128 kHz, 125 ticks) divided by 4 << PR
            var prescaler = 4L << Math.Min((int)_io[IwdgPr - IoStart], 6);
            _iwdgTicks = (_io[IwdgRlr - IoStart] + 1L) * prescaler * 125;
        }
    }

    private byte ReadCpuRegister(int address) => (address - CpuStart) switch
    {
        0x00 => _cpu.A,
        0x01 => (byte)(_cpu.PC >> 16),
        0x02 => (byte)(_cpu.PC >> 8),
        0x03 => (byte)_cpu.PC,
        0x04 => (byte)(_cpu.X >> 8),
        0x05 => (byte)_cpu.X,
        0x06 => (byte)(_cpu.Y >> 8),
        0x07 => (byte)_cpu.Y,
        0x08 => (byte)(_cpu.SP >> 8),
        0x09 => (byte)_cpu.SP,
        0x0A => _cpu.CC,
        var other => _cpuRegisters[other],
    };

    private void WriteCpuRegister(int address, byte value)
    {
        switch (address - CpuStart)
        {
            case 0x00:
                _cpu.A = value;
                break;
            case 0x04:
                _cpu.X = (ushort)((_cpu.X & 0xFF) | (value << 8));
                break;
            case 0x05:
                _cpu.X = (ushort)((_cpu.X & 0xFF00) | value);
                break;
            case 0x06:
                _cpu.Y = (ushort)((_cpu.Y & 0xFF) | (value << 8));
                break;
            case 0x07:
                _cpu.Y = (ushort)((_cpu.Y & 0xFF00) | value);
                break;
            case 0x08:
                _cpu.SP = (ushort)((_cpu.SP & 0xFF) | (value << 8));
                break;
            case 0x09:
                _cpu.SP = (ushort)((_cpu.SP & 0xFF00) | value);
                break;
            case 0x0A:
                _cpu.CC = value;
                break;
            case var other:
                _cpuRegisters[other] = value;
                break;
        }
    }

    private void Reset()
    {
        _io[ClkIckr - IoStart] = 0x01;
        _io[ClkCmsr - IoStart] = Hsi;
        _io[ClkSwr - IoStart] = Hsi;
        _io[ClkCkdivr - IoStart] = 0x18;
        _io[0x50C7 - IoStart] = 0xFF; // CLK_PCKENR1, PCKENR2
        _io[0x50CA - IoStart] = 0xFF;
        _io[IwdgRlr - IoStart] = 0xFF;
        _io[0x50D1 - IoStart] = 0x7F; // WWDG_CR, WR
        _io[0x50D2 - IoStart] = 0x7F;
        _io[0x5203 - IoStart] = 0x02; // SPI_SR: TXE
        _io[0x50F1 - IoStart] = 0x3F; // AWU_APR
        _io[0x50F3 - IoStart] = 0x1F; // BEEP_CSR
        for (var spr = 0x70; spr < 0x78; spr++)
        {
            _cpuRegisters[spr] = 0xFF; // ITC_SPR1-8: every interrupt at level 3
        }

        UpdateClock();
    }

    private ProgramFault Outside(string what, int address) => new(string.Create(CultureInfo.InvariantCulture,
        $"the program {what} 0x{address:x4}, where the STM8S103 has no memory, at pc 0x{_cpu.PC:x4} (SP 0x{_cpu.SP:x4}" +
        $"{(address >= 0xFF00 && _cpu.SP >= 0xFF00 ? ": the stack has grown past the bottom of RAM" : "")})"));
}
