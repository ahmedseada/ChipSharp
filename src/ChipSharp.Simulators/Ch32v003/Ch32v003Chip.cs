using System.Buffers.Binary;
using System.Globalization;
using ChipSharp.Simulators.RiscV;

namespace ChipSharp.Simulators.Ch32v003;

/// <summary>
/// A WCH CH32V003 (the CH32V003F4P6: CH32V003 Reference Manual V1.6, datasheet V1.4, QingKeV2 Processor Manual V1.1): the
/// QingKe V2A core (<see cref="RiscVCpu"/> as <see cref="RiscVCore.QingKeV2A"/>: RV32EC and XW, the PFIC,
/// <see cref="Ch32v003Pfic"/>, and SysTick, <see cref="Ch32v003SysTick"/>), 16 KB of flash at 0x0800_0000 (aliased at 0,
/// where it boots; what the image does not fill reads 0xFF, and running into it stops the run), 2 KB of SRAM at
/// 0x2000_0000, ESIG (16 KB; a unique ID) and the option bytes as WCH ships them (<see cref="Ch32v003Map.FactoryOptionBytes"/>),
/// and the peripherals a runtime needs, at <see cref="Ch32v003Map"/>'s addresses: RCC, FLASH's wait states, GPIOA/C/D with
/// AFIO and EXTI (<see cref="Ch32v003Gpio"/>), USART1 (<see cref="Ch32v003Usart"/>), the watchdogs (<see cref="Ch32v003Iwdg"/>,
/// <see cref="Ch32v003Wwdg"/>). PWR, EXTEND (0x0A00 after reset) and DBG keep what is written; TIM1, TIM2, I2C1, SPI1,
/// ADC1 and DMA1 are not modeled yet, and a program that touches them stops, saying so. An access of anything else is
/// a bus error, the access-fault exception. A byte or halfword access of a register reads, or writes, its bytes of the
/// word (the PFIC's IPRIOR and VTFIDR bytes alone).
/// <para>
/// RCC: CTLR (HSION and HSIRDY 1; HSITRIM 16; PLLON, and PLLRDY at once; HSEON stops the run: the simulator has no
/// crystal), CFGR0 (SW and SWS: HSI, or the PLL once ready (HSI × 2, PLLSRC 0); HPRE, SYSCLK / 3 after reset: the CPU
/// starts at 8 MHz), INTR, the peripheral resets (APB2PRSTR, APB1PRSTR: GPIO, AFIO, USART1, WWDG), the clock enables
/// (AHBPCENR, APB2PCENR, APB1PCENR: a register of GPIOx, AFIO, USART1, WWDG or PWR used with its clock off stops the run,
/// as the chip would ignore it) and RSTSCKR (LSION, LSIRDY at once). The CPU runs at HCLK; its changes are printed
/// ("CPU clock 48 MHz"). FLASH: ACTLR's LATENCY, which must be 1 for SYSCLK above 24 MHz (RM 16.3.1): a run at 48 MHz
/// with 0 stops, as the chip would misread its flash; KEYR's keys unlock CTLR; erasing and programming are not simulated
/// (STRT, PG, FTPG and FTER stop the run); OBR and WPR give the option bytes.
/// </para>
/// <para>
/// Time: the core's cycles at HCLK, and a load or store 1 more (2 for a peripheral's register, LATENCY more for flash);
/// with a flash wait state, an instruction fetched from a 32-bit word of flash other than the last costs LATENCY cycles
/// more (a model: WCH does not publish the V2A's timing). The run starts at 0, as reset leaves the chip (the program's
/// start-up sets sp, mtvec, INTSYSCR). It stops as a failure at ebreak (the runtimes' failure stop), at a trap the core
/// cannot serve (no handler), at a watchdog's reset or a software reset (PFIC's SYSRESET); it has finished when the
/// core waits for an interrupt (wfi) with interrupts off (mstatus.MIE 0), once USART1 has sent what it holds; or with
/// interrupts on, when no interrupt can come: none pending that the PFIC would take, none to come from SysTick, USART1,
/// a watchdog or an input. With one to come, the interrupt wakes the core and its handler runs.
/// </para>
/// </summary>
public sealed class Ch32v003Chip : SimulatedChip, IRiscVBus
{
    private const uint Apb2Afio = 1, Apb2Gpioa = 1 << 2, Apb2Gpioc = 1 << 4, Apb2Gpiod = 1 << 5, Apb2Usart1 = 1 << 14;
    private const uint Apb1Wwdg = 1 << 11, Apb1Pwr = 1 << 28;
    private const uint FlashKey1 = 0x4567_0123, FlashKey2 = 0xCDEF_89AB;

    /// <summary>The unique ID ESIG gives (UNIID1-3): a fixed one.</summary>
    private static readonly uint[] UniqueId = [0x4C43_5743, 0x3330_3056, 0x5349_4D20];

    private readonly RiscVCpu _cpu;
    private readonly Ch32v003Pfic _pfic;
    private readonly Ch32v003SysTick _sysTick;
    private readonly Ch32v003Gpio _gpio;
    private readonly Ch32v003Usart _usart;
    private readonly Ch32v003Iwdg _iwdg;
    private readonly Ch32v003Wwdg _wwdg;
    private readonly byte[] _flash;
    private readonly bool[] _flashLoaded;
    private readonly byte[] _sram = new byte[Ch32v003Map.SramSize];
    private readonly byte[] _optionBytes = new byte[Ch32v003Map.OptionBytesSize];
    private readonly Dictionary<uint, uint> _registers = new() { [Ch32v003Map.Extend] = 0x0A00 };
    private readonly int _imageBytes;
    private uint _rccCtlr = 0x83 | (0x48u << 8);
    private uint _rccCfgr0 = 0x20;
    private uint _rccIntr;
    private uint _apb2Reset, _apb1Reset;
    private uint _ahbEnable = 4, _apb2Enable, _apb1Enable;
    private uint _rstsckr = 0x0C00_0000;
    private uint _latency;
    private uint _flashCtlr = 0x8080;
    private uint _flashStatr = 0x8000;
    private uint _flashAddress;
    private int _flashKeys;
    private uint _systemClockSource;
    private long _sysclk = Ch32v003Map.HsiHz;
    private long _hclk = Ch32v003Map.HsiHz / 3;
    private long _cycleFraction;
    private long _nextPeripheralEvent = long.MaxValue;
    private bool _replan = true;
    private bool _checkInterrupts = true;
    private uint _prefetchedWord = uint.MaxValue;
    private uint _fetched;

    /// <summary>A CH32V003 with <paramref name="image"/> in its flash, as reset leaves it.</summary>
    public Ch32v003Chip(Ch32v003Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Image = image;
        _flash = image.Flash;
        _flashLoaded = image.Loaded;
        _imageBytes = image.Count;
        Array.Fill(_optionBytes, (byte)0xFF);
        Ch32v003Map.FactoryOptionBytes.ToArray().CopyTo(_optionBytes, 0);

        _pfic = new Ch32v003Pfic(() => (_cpu!.InterruptSystemControl & RiscVCpu.IntsyscrNesting) != 0)
        {
            Changed = () => _checkInterrupts = true,
            Reset = Fail,
        };
        _pfic.Returned = () =>
        {
            if (_pfic.Depth == 0 && _pfic.SleepOnExit)
            {
                _cpu!.Waiting = true; // SLEEPONEXIT: the last mret sleeps
            }
        };
        _cpu = new RiscVCpu(this, RiscVCore.QingKeV2A)
        {
            Pc = Ch32v003Map.FlashAlias,
            Pfic = _pfic,
            InterruptStateChanged = () => _checkInterrupts = true,
        };
        _sysTick = new Ch32v003SysTick(this, () => _hclk) { Changed = () => _replan = true };
        _usart = new Ch32v003Usart(this, () => _hclk) { Changed = () => _replan = true };
        _gpio = new Ch32v003Gpio(this, () => ((_optionBytes[2] >> 3) & 3) != 3, () => _gpio is null ? (-1, 0, false) : UsartTx())
        {
            Changed = () => _replan = true,
            Fault = Fail,
        };
        _usart.Changed = () =>
        {
            _replan = true;
            _gpio.ApplyAll();
        };
        _usart.TxConnected = () => UsartTx() is var (port, bit, _) && _gpio.IsAlternateOutput(port, bit);
        _usart.RxConnected = () => UsartRx() is var (port, bit) && _gpio.IsDigitalInput(port, bit);
        _usart.PinNames = () => (Ch32v003Gpio.PinName(UsartTx().Port, UsartTx().Bit), Ch32v003Gpio.PinName(UsartRx().Port, UsartRx().Bit));
        _iwdg = new Ch32v003Iwdg(this, Fail);
        _wwdg = new Ch32v003Wwdg(this, () => _hclk, () => (_apb1Enable & Apb1Wwdg) != 0, Fail) { Changed = () => _replan = true };
        _gpio.ApplyAll();
    }

    /// <summary>Loads an Intel HEX, .bin or ELF file (<see cref="Ch32v003Image.Load"/>) into a new CH32V003.</summary>
    public static Ch32v003Chip Load(string path) => new(Ch32v003Image.Load(path));

    public override string Name => "CH32V003";

    /// <summary>The image loaded: the flash and the ELF's symbols.</summary>
    public Ch32v003Image Image { get; }

    /// <summary>The core: its registers and CSRs.</summary>
    public RiscVCpu Cpu => _cpu;

    /// <summary>HCLK, the core's clock, in hertz: 8 MHz after reset (HSI / 3), up to 48 MHz.</summary>
    public long CpuHertz => _hclk;

    public override IReadOnlyList<GpioPort> Ports => _gpio.Ports;

    public override ISerialInput? SerialInput => _usart;

    public override SerialMonitor? SerialOutput => _usart.Output;

    public override int ImageBytes => _imageBytes;

    /// <summary>The SRAM, from 0x2000_0000.</summary>
    public override byte[] Ram => _sram;

    public override int ProgramCounter => (int)_cpu.Pc;

    /// <summary>USART1's TX pin (port 0 A, 1 C, 2 D; bit) by AFIO_PCFR1's USART1_RM, and whether the USART drives it.</summary>
    private (int Port, int Bit, bool Drives) UsartTx()
    {
        var (port, bit) = _gpio.UsartRemap switch { 0 => (2, 5), 1 => (2, 0), 2 => (2, 6), _ => (1, 0) };
        return (port, bit, _usart.Transmitting);
    }

    private (int Port, int Bit) UsartRx() => _gpio.UsartRemap switch { 0 => (2, 6), 1 => (2, 1), 2 => (2, 5), _ => (1, 1) };

    /// <summary>Reads <paramref name="size"/> bytes at <paramref name="address"/> as the core would (e.g. a test's variable).</summary>
    public uint Read(uint address, int size)
    {
        var offset = address - Ch32v003Map.SramStart;
        if (offset <= Ch32v003Map.SramSize - 4)
        {
            return size switch
            {
                4 => BinaryPrimitives.ReadUInt32LittleEndian(_sram.AsSpan((int)offset)),
                2 => BinaryPrimitives.ReadUInt16LittleEndian(_sram.AsSpan((int)offset)),
                _ => _sram[offset],
            };
        }

        return ReadOther(address, size);
    }

    public void Write(uint address, int size, uint value)
    {
        var offset = address - Ch32v003Map.SramStart;
        if (offset <= Ch32v003Map.SramSize - 4)
        {
            switch (size)
            {
                case 4:
                    BinaryPrimitives.WriteUInt32LittleEndian(_sram.AsSpan((int)offset), value);
                    return;
                case 2:
                    BinaryPrimitives.WriteUInt16LittleEndian(_sram.AsSpan((int)offset), (ushort)value);
                    return;
                default:
                    _sram[offset] = (byte)value;
                    return;
            }
        }

        WriteOther(address, size, value);
    }

    public uint Fetch(uint address)
    {
        var flash = FlashOffset(address);
        if (flash < Ch32v003Map.FlashSize)
        {
            if (!_flashLoaded[flash])
            {
                throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                    $"the program ran into flash the image does not fill, at pc 0x{address:x8}"));
            }

            return _fetched = flash <= Ch32v003Map.FlashSize - 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(_flash.AsSpan((int)flash))
                : BinaryPrimitives.ReadUInt16LittleEndian(_flash.AsSpan((int)flash));
        }

        var sram = address - Ch32v003Map.SramStart;
        if (sram < Ch32v003Map.SramSize)
        {
            return _fetched = sram <= Ch32v003Map.SramSize - 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(_sram.AsSpan((int)sram))
                : BinaryPrimitives.ReadUInt16LittleEndian(_sram.AsSpan((int)sram));
        }

        throw new RiscVAccessFault(address);
    }

    public int AccessCycles(uint address)
    {
        if (address - Ch32v003Map.SramStart < Ch32v003Map.SramSize)
        {
            return 1;
        }

        return FlashOffset(address) < Ch32v003Map.FlashSize ? 1 + (int)_latency : 2;
    }

    /// <summary>The offset in the flash of <paramref name="address"/> (the flash, or its alias at 0), or above its size.</summary>
    private static uint FlashOffset(uint address) =>
        address < Ch32v003Map.FlashSize ? address : address - Ch32v003Map.FlashStart;

    protected override void Step(long limit)
    {
        if (_replan)
        {
            Replan();
        }

        if (_cpu.Waiting)
        {
            Sleep(limit);
            return;
        }

        var deadline = Math.Min(limit, _nextPeripheralEvent);
        var hooks = Hooks is not null;
        while (true)
        {
            if (_checkInterrupts)
            {
                _checkInterrupts = false;
                TakeInterrupt();
            }

            var pc = _cpu.Pc;
            if (hooks)
            {
                RunHook((int)pc);
            }

            var cycles = _cpu.Step();
            if (_latency != 0 && FlashOffset(pc) < Ch32v003Map.FlashSize)
            {
                cycles += FetchWaits(pc);
            }

            AddCycles(cycles);
            if (_cpu.BreakHit)
            {
                Fail(string.Create(CultureInfo.InvariantCulture,
                    $"the program hit a breakpoint (ebreak) at pc 0x{pc:x8}, as a runtime does on a fatal error"));
                return;
            }

            if (StopReason is not null)
            {
                return;
            }

            if (_replan)
            {
                Replan();
                deadline = Math.Min(limit, _nextPeripheralEvent);
            }

            if (_cpu.Waiting || Ticks >= deadline)
            {
                break;
            }
        }

        if (Ticks >= _nextPeripheralEvent)
        {
            AdvancePeripherals();
        }
    }

    /// <summary>The flash wait states of the instruction at <paramref name="pc"/>: LATENCY for each word other than the last fetched.</summary>
    private int FetchWaits(uint pc)
    {
        var first = FlashOffset(pc) >> 2;
        var last = (FlashOffset(pc) + ((_fetched & 3) == 3 ? 3u : 1u)) >> 2;
        var waits = 0;
        for (var word = first; word <= last; word++)
        {
            if (word != _prefetchedWord)
            {
                waits += (int)_latency;
                _prefetchedWord = word;
            }
        }

        return waits;
    }

    private void AddCycles(int cycles)
    {
        _cycleFraction += cycles * TicksPerSecond;
        if (_cycleFraction >= _hclk)
        {
            var ticks = _cycleFraction / _hclk;
            Ticks += ticks;
            _cycleFraction -= ticks * _hclk;
        }
    }

    /// <summary>Takes the interrupt the PFIC lets in, when interrupts are on (NMI whatever MIE says).</summary>
    private void TakeInterrupt()
    {
        if (_pfic.Best() is { } number && (_cpu.InterruptsEnabled || number == Ch32v003Map.NmiNumber))
        {
            AddCycles(_cpu.TakeQingKeInterrupt(number));
        }
    }

    /// <summary>
    /// wfi. With interrupts off (mstatus.MIE 0), ChipSharp's halt: once USART1 has sent what it holds, the program has
    /// finished. With interrupts on, the core wakes when the PFIC would take an interrupt, else time passes to the next
    /// event; with none to come, the program has finished too.
    /// </summary>
    private void Sleep(long limit)
    {
        long next;
        if (!_cpu.InterruptsEnabled)
        {
            next = _usart.NextEvent;
            if (_usart.IdleTick == long.MaxValue)
            {
                Finish("the program has finished: the CPU waits for an interrupt (wfi) with interrupts off (mstatus.MIE 0)");
                return;
            }
        }
        else if (_pfic.Best() is not null)
        {
            _cpu.Waiting = false;
            _checkInterrupts = true;
            return;
        }
        else
        {
            next = NextWakeEvent();
            if (next == long.MaxValue && !(InputsAhead && InputsCouldWake()))
            {
                Finish("the program has finished: the CPU waits for an interrupt (wfi) that nothing can raise any more");
                return;
            }
        }

        var to = Math.Min(limit, next);
        if (to > Ticks)
        {
            Ticks = to;
        }

        if (Ticks >= _nextPeripheralEvent)
        {
            AdvancePeripherals();
        }
    }

    /// <summary>What a waiting core must not skip: what could wake it, a watchdog, USART1 sending.</summary>
    private long NextWakeEvent()
    {
        var next = Math.Min(_usart.NextEvent, Math.Min(_iwdg.NextEvent, _wwdg.NextEvent));
        if (_sysTick.InterruptsOn && _pfic.CouldWake(Ch32v003Map.SysTickNumber))
        {
            next = Math.Min(next, _sysTick.NextEvent);
        }

        return next;
    }

    private bool InputsCouldWake() =>
        (_gpio.InterruptsOn && _pfic.CouldWake(Ch32v003Map.Exti7To0Number)) ||
        (_usart.ReceiveInterrupts && _pfic.CouldWake(Ch32v003Map.Usart1Number));

    private void AdvancePeripherals()
    {
        _usart.Advance();
        _sysTick.Advance();
        _iwdg.Advance();
        _wwdg.Advance();
        Replan();
    }

    /// <summary>The interrupt lines from the peripherals, and the tick of the next peripheral event.</summary>
    private void Replan()
    {
        _replan = false;
        _pfic.SetLine(Ch32v003Map.SysTickNumber, _sysTick.InterruptPending);
        _pfic.SetLine(Ch32v003Map.SoftwareNumber, _sysTick.SoftwarePending);
        _pfic.SetLine(Ch32v003Map.WwdgNumber, _wwdg.InterruptPending);
        _pfic.SetLine(Ch32v003Map.Exti7To0Number, _gpio.InterruptPending);
        _pfic.SetLine(Ch32v003Map.Usart1Number, _usart.InterruptPending);
        _nextPeripheralEvent = Math.Min(Math.Min(_usart.NextEvent, _sysTick.NextEvent), Math.Min(_iwdg.NextEvent, _wwdg.NextEvent));
        _checkInterrupts = true;
    }

    private uint ReadOther(uint address, int size)
    {
        var shift = (int)(address & 3) * 8;
        var flash = FlashOffset(address);
        if (flash <= Ch32v003Map.FlashSize - (uint)size)
        {
            return ReadBytes(_flash, flash, size);
        }

        if (address - Ch32v003Map.PeripheralStart < Ch32v003Map.PeripheralEnd - Ch32v003Map.PeripheralStart)
        {
            return Narrow(ReadPeripheral(address & ~3u), shift, size);
        }

        if (address - Ch32v003Map.Pfic < 0x1000)
        {
            return _pfic.Read(address - Ch32v003Map.Pfic, size);
        }

        if (address - Ch32v003Map.SysTick < 0x20)
        {
            if (Ticks >= _nextPeripheralEvent)
            {
                AdvancePeripherals();
            }

            return Narrow(_sysTick.Read((address - Ch32v003Map.SysTick) & ~3u), shift, size);
        }

        if (address - Ch32v003Map.Dbg < 0x400)
        {
            return Narrow(_registers.GetValueOrDefault(address & ~3u), shift, size);
        }

        var sram = address - Ch32v003Map.SramStart;
        if (sram <= Ch32v003Map.SramSize - (uint)size)
        {
            return ReadBytes(_sram, sram, size);
        }

        var options = address - Ch32v003Map.OptionBytes;
        if (options <= Ch32v003Map.OptionBytesSize - (uint)size)
        {
            return ReadBytes(_optionBytes, options, size);
        }

        if (address - Ch32v003Map.EsigFlashCapacity < 0x20)
        {
            return Narrow(Esig((address - Ch32v003Map.EsigFlashCapacity) & ~3u), shift, size);
        }

        throw new RiscVAccessFault(address);
    }

    private void WriteOther(uint address, int size, uint value)
    {
        var shift = (int)(address & 3) * 8;
        var word = size == 4 ? value : (value & Mask(size)) << shift;
        if (address - Ch32v003Map.PeripheralStart < Ch32v003Map.PeripheralEnd - Ch32v003Map.PeripheralStart)
        {
            WritePeripheral(address & ~3u, word, size == 4 ? uint.MaxValue : Mask(size) << shift);
            return;
        }

        if (address - Ch32v003Map.Pfic < 0x1000)
        {
            _pfic.Write(address - Ch32v003Map.Pfic, size, value);
            return;
        }

        if (address - Ch32v003Map.SysTick < 0x20)
        {
            if (Ticks >= _nextPeripheralEvent)
            {
                AdvancePeripherals();
            }

            var register = (address - Ch32v003Map.SysTick) & ~3u;
            _sysTick.Write(register, Merge(_sysTick.Read(register), word, size == 4 ? uint.MaxValue : Mask(size) << shift));
            return;
        }

        if (address - Ch32v003Map.Dbg < 0x400)
        {
            _registers[address & ~3u] = Merge(_registers.GetValueOrDefault(address & ~3u), word, size, shift);
            return;
        }

        var sram = address - Ch32v003Map.SramStart;
        if (sram <= Ch32v003Map.SramSize - (uint)size)
        {
            WriteBytes(_sram, sram, size, value);
            return;
        }

        throw new RiscVAccessFault(address); // the flash, the option bytes (FPEC programs them), what is not there
    }

    private static uint Esig(uint offset) => offset switch
    {
        0x00 => Ch32v003Map.FlashSize / 1024, // ESIG_FLACAP: 16 (KB)
        0x08 => UniqueId[0],
        0x0C => UniqueId[1],
        0x10 => UniqueId[2],
        _ => 0xFFFF_FFFF,
    };

    private static uint Mask(int size) => size == 4 ? uint.MaxValue : (1u << (8 * size)) - 1;

    private static uint Narrow(uint word, int shift, int size) => (word >> shift) & Mask(size);

    private static uint Merge(uint old, uint word, int size, int shift) =>
        size == 4 ? word : (old & ~(Mask(size) << shift)) | word;

    private static uint ReadBytes(byte[] memory, uint offset, int size)
    {
        uint value = 0;
        for (var b = 0; b < size; b++)
        {
            value |= (uint)memory[offset + b] << (8 * b);
        }

        return value;
    }

    private static void WriteBytes(byte[] memory, uint offset, int size, uint value)
    {
        for (var b = 0; b < size; b++)
        {
            memory[offset + b] = (byte)(value >> (8 * b));
        }
    }

    /// <summary>Whether a peripheral's clock is on; when not, the run stops, as the chip would ignore the access.</summary>
    private bool Clocked(string name, uint address, uint enables, uint bit, string enable)
    {
        if ((enables & bit) != 0)
        {
            return true;
        }

        Fail(string.Create(CultureInfo.InvariantCulture,
            $"the program used {name}'s register at 0x{address:x8} with its clock off ({enable} 0): the chip ignores it"));
        return false;
    }

    /// <summary>A peripheral the simulator does not model yet: the run stops, saying so.</summary>
    private void Unmodeled(string name, uint address) => Fail(string.Create(CultureInfo.InvariantCulture,
        $"the program used {name} (its register at 0x{address:x8}), which the CH32V003 simulator does not model yet (milestone M5, wave 2)"));

    private uint ReadPeripheral(uint address)
    {
        if (Ticks >= _nextPeripheralEvent)
        {
            AdvancePeripherals();
        }

        switch (address)
        {
            case >= Ch32v003Map.GpioA and < Ch32v003Map.GpioA + 0x400:
            case >= Ch32v003Map.GpioC and < Ch32v003Map.GpioC + 0x400:
            case >= Ch32v003Map.GpioD and < Ch32v003Map.GpioD + 0x400:
                var port = Ch32v003Gpio.PortIndex(address & ~0x3FFu);
                return GpioClocked(port, address) ? _gpio.ReadPort(port, address & 0x3FF) : 0;
            case >= Ch32v003Map.Usart1 and < Ch32v003Map.Usart1 + 0x400:
                return Clocked("USART1", address, _apb2Enable, Apb2Usart1, "RCC_APB2PCENR's USART1EN")
                    ? _usart.Read(address - Ch32v003Map.Usart1)
                    : 0;
            case >= Ch32v003Map.Exti and < Ch32v003Map.Exti + 0x400:
                return _gpio.ReadExti(address - Ch32v003Map.Exti);
            case >= Ch32v003Map.Afio and < Ch32v003Map.Afio + 0x400:
                return Clocked("AFIO", address, _apb2Enable, Apb2Afio, "RCC_APB2PCENR's AFIOEN")
                    ? _gpio.ReadAfio(address - Ch32v003Map.Afio)
                    : 0;
            case >= Ch32v003Map.Rcc and < Ch32v003Map.Rcc + 0x400:
                return ReadRcc(address - Ch32v003Map.Rcc);
            case >= Ch32v003Map.Flash and < Ch32v003Map.Flash + 0x400:
                return ReadFlash(address - Ch32v003Map.Flash);
            case >= Ch32v003Map.Iwdg and < Ch32v003Map.Iwdg + 0x400:
                return _iwdg.Read(address - Ch32v003Map.Iwdg);
            case >= Ch32v003Map.Wwdg and < Ch32v003Map.Wwdg + 0x400:
                return Clocked("WWDG", address, _apb1Enable, Apb1Wwdg, "RCC_APB1PCENR's WWDGEN")
                    ? _wwdg.Read(address - Ch32v003Map.Wwdg)
                    : 0;
            case >= Ch32v003Map.Pwr and < Ch32v003Map.Pwr + 0x400:
                return Clocked("PWR", address, _apb1Enable, Apb1Pwr, "RCC_APB1PCENR's PWREN")
                    ? _registers.GetValueOrDefault(address)
                    : 0;
            case >= Ch32v003Map.Extend and < Ch32v003Map.Extend + 0x400:
                return _registers.GetValueOrDefault(address);
            default:
                if (UnmodeledName(address) is { } name)
                {
                    Unmodeled(name, address);
                    return 0;
                }

                throw new RiscVAccessFault(address); // reserved
        }
    }

    private void WritePeripheral(uint address, uint value, uint mask)
    {
        if (Ticks >= _nextPeripheralEvent)
        {
            AdvancePeripherals();
        }

        _replan = true;
        switch (address)
        {
            case >= Ch32v003Map.GpioA and < Ch32v003Map.GpioA + 0x400:
            case >= Ch32v003Map.GpioC and < Ch32v003Map.GpioC + 0x400:
            case >= Ch32v003Map.GpioD and < Ch32v003Map.GpioD + 0x400:
                var port = Ch32v003Gpio.PortIndex(address & ~0x3FFu);
                if (GpioClocked(port, address))
                {
                    var offset = address & 0x3FF;
                    _gpio.WritePort(port, offset, offset is 0x10 or 0x14 ? value : Merge(_gpio.ReadPort(port, offset), value, mask));
                }

                return;
            case >= Ch32v003Map.Usart1 and < Ch32v003Map.Usart1 + 0x400:
                if (Clocked("USART1", address, _apb2Enable, Apb2Usart1, "RCC_APB2PCENR's USART1EN"))
                {
                    _usart.Write(address - Ch32v003Map.Usart1, value);
                }

                return;
            case >= Ch32v003Map.Exti and < Ch32v003Map.Exti + 0x400:
                _gpio.WriteExti(address - Ch32v003Map.Exti, value);
                return;
            case >= Ch32v003Map.Afio and < Ch32v003Map.Afio + 0x400:
                if (Clocked("AFIO", address, _apb2Enable, Apb2Afio, "RCC_APB2PCENR's AFIOEN"))
                {
                    var offset = address - Ch32v003Map.Afio;
                    _gpio.WriteAfio(offset, Merge(_gpio.ReadAfio(offset), value, mask));
                }

                return;
            case >= Ch32v003Map.Rcc and < Ch32v003Map.Rcc + 0x400:
                WriteRcc(address - Ch32v003Map.Rcc, value, mask);
                return;
            case >= Ch32v003Map.Flash and < Ch32v003Map.Flash + 0x400:
                WriteFlash(address - Ch32v003Map.Flash, value);
                return;
            case >= Ch32v003Map.Iwdg and < Ch32v003Map.Iwdg + 0x400:
                _iwdg.Write(address - Ch32v003Map.Iwdg, value);
                return;
            case >= Ch32v003Map.Wwdg and < Ch32v003Map.Wwdg + 0x400:
                if (Clocked("WWDG", address, _apb1Enable, Apb1Wwdg, "RCC_APB1PCENR's WWDGEN"))
                {
                    _wwdg.Write(address - Ch32v003Map.Wwdg, value);
                }

                return;
            case >= Ch32v003Map.Pwr and < Ch32v003Map.Pwr + 0x400:
                if (Clocked("PWR", address, _apb1Enable, Apb1Pwr, "RCC_APB1PCENR's PWREN"))
                {
                    _registers[address] = Merge(_registers.GetValueOrDefault(address), value, mask);
                }

                return;
            case >= Ch32v003Map.Extend and < Ch32v003Map.Extend + 0x400:
                _registers[address] = Merge(_registers.GetValueOrDefault(address), value, mask);
                return;
            default:
                if (UnmodeledName(address) is { } name)
                {
                    Unmodeled(name, address);
                    return;
                }

                throw new RiscVAccessFault(address);
        }
    }

    /// <summary>The old word with the written bytes (<paramref name="mask"/>) replaced.</summary>
    private static uint Merge(uint old, uint value, uint mask) => (old & ~mask) | (value & mask);

    private static string? UnmodeledName(uint address) => address switch
    {
        >= Ch32v003Map.Tim2 and < Ch32v003Map.Tim2 + 0x400 => "TIM2",
        >= Ch32v003Map.I2c1 and < Ch32v003Map.I2c1 + 0x400 => "I2C1",
        >= Ch32v003Map.Adc1 and < Ch32v003Map.Adc1 + 0x400 => "ADC1",
        >= Ch32v003Map.Tim1 and < Ch32v003Map.Tim1 + 0x400 => "TIM1",
        >= Ch32v003Map.Spi1 and < Ch32v003Map.Spi1 + 0x400 => "SPI1",
        >= Ch32v003Map.Dma1 and < Ch32v003Map.Dma1 + 0x400 => "DMA1",
        _ => null,
    };

    private bool GpioClocked(int port, uint address) => port switch
    {
        0 => Clocked("GPIOA", address, _apb2Enable, Apb2Gpioa, "RCC_APB2PCENR's IOPAEN"),
        1 => Clocked("GPIOC", address, _apb2Enable, Apb2Gpioc, "RCC_APB2PCENR's IOPCEN"),
        _ => Clocked("GPIOD", address, _apb2Enable, Apb2Gpiod, "RCC_APB2PCENR's IOPDEN"),
    };

    private uint ReadRcc(uint offset) => offset switch
    {
        0x00 => _rccCtlr | ((_rccCtlr & 1) << 1) | ((_rccCtlr & (1u << 24)) << 1), // HSIRDY with HSION, PLLRDY with PLLON
        0x04 => (_rccCfgr0 & ~0xCu) | (_systemClockSource << 2),
        0x08 => _rccIntr,
        0x0C => _apb2Reset,
        0x10 => _apb1Reset,
        0x14 => _ahbEnable,
        0x18 => _apb2Enable,
        0x1C => _apb1Enable,
        0x24 => _rstsckr | ((_rstsckr & 1) << 1), // LSIRDY with LSION
        _ => 0,
    };

    private void WriteRcc(uint offset, uint value, uint mask)
    {
        value = Merge(ReadRcc(offset), value, mask);
        switch (offset)
        {
            case 0x00:
                if ((value & (1u << 16)) != 0)
                {
                    Fail("the program turned HSE on (RCC_CTLR's HSEON): the simulator has no external crystal (OSC_IN, " +
                        "OSC_OUT: PA1, PA2); the CH32V003 runs from its HSI");
                    return;
                }

                // HSITRIM, PLLON, CSSON, HSEBYP; HSION and PLLON stay on while SYSCLK needs them
                var ctlr = (value & 0x010D_00F9) | (_rccCtlr & 0xFF00);
                if (_systemClockSource == 2)
                {
                    ctlr |= (1u << 24) | 1;
                }
                else if (_systemClockSource == 0)
                {
                    ctlr |= 1;
                }

                _rccCtlr = ctlr;
                UpdateClock();
                return;
            case 0x04:
                if ((value & (1u << 16)) != 0)
                {
                    Fail("the program selected HSE as the PLL's source (RCC_CFGR0's PLLSRC): the simulator has no external crystal");
                    return;
                }

                _rccCfgr0 = value & 0x0701_FFF3;
                UpdateClock();
                return;
            case 0x08:
                _rccIntr = (_rccIntr & ~((value >> 16) & 0x9F)) | (value & 0x1F00); // the C bits clear the flags
                return;
            case 0x0C:
                _apb2Reset = value & 0x5A35;
                ResetPeripherals(_apb2Reset, 0);
                return;
            case 0x10:
                _apb1Reset = value & 0x1020_0801;
                ResetPeripherals(0, _apb1Reset);
                return;
            case 0x14:
                _ahbEnable = value & 5;
                return;
            case 0x18:
                _apb2Enable = value & 0x5A35;
                return;
            case 0x1C:
                _apb1Enable = value & 0x1020_0801;
                _wwdg.ClockChanged();
                return;
            case 0x24:
                _rstsckr = (value & 0x0100_0000) != 0 ? value & 1 : (_rstsckr & 0xFC00_0000) | (value & 1);
                return;
        }
    }

    /// <summary>The peripherals a reset bit of APB2PRSTR or APB1PRSTR holds in reset go back to their reset values.</summary>
    private void ResetPeripherals(uint apb2, uint apb1)
    {
        if ((apb2 & Apb2Afio) != 0)
        {
            _gpio.ResetAfio();
        }

        foreach (var (bit, port) in new[] { (Apb2Gpioa, 0), (Apb2Gpioc, 1), (Apb2Gpiod, 2) })
        {
            if ((apb2 & bit) != 0)
            {
                _gpio.Reset(port);
            }
        }

        if ((apb2 & Apb2Usart1) != 0)
        {
            _usart.Reset();
        }

        if ((apb1 & Apb1Wwdg) != 0)
        {
            _wwdg.ResetRegisters();
        }
    }

    /// <summary>SYSCLK from SW (the PLL once it is ready), HCLK from HPRE; the core's clock follows, and is printed.</summary>
    private void UpdateClock()
    {
        var sw = _rccCfgr0 & 3;
        if (sw == 1)
        {
            Fail("the program selected HSE as the system clock (RCC_CFGR0's SW): the simulator has no external crystal");
            return;
        }

        if (sw == 0 || (sw == 2 && (_rccCtlr & (1u << 24)) != 0))
        {
            _systemClockSource = sw;
        }

        _sysclk = _systemClockSource == 2 ? Ch32v003Map.PllHz : Ch32v003Map.HsiHz;
        var hpre = (_rccCfgr0 >> 4) & 0xF;
        var divider = hpre < 8 ? hpre + 1 : 2L << (int)(hpre - 8);
        var hclk = _sysclk / divider;
        CheckLatency();
        if (hclk != _hclk)
        {
            _cycleFraction = _cycleFraction * hclk / _hclk;
            _hclk = hclk;
            _sysTick.ClockChanged();
            _wwdg.ClockChanged();
            Print(string.Create(CultureInfo.InvariantCulture, $"CPU clock {hclk / 1e6:0.###} MHz"));
        }
    }

    /// <summary>A SYSCLK above 24 MHz needs a flash wait state (FLASH_ACTLR's LATENCY 1).</summary>
    private void CheckLatency()
    {
        if (_sysclk > 24_000_000 && _latency == 0)
        {
            Fail(string.Create(CultureInfo.InvariantCulture,
                $"SYSCLK is {_sysclk / 1e6:0.###} MHz with no flash wait state (FLASH_ACTLR's LATENCY 0): ") +
                "the CH32V003 needs 1 above 24 MHz (RM 16.3.1), set before the switch, or it misreads its flash");
        }
    }

    private uint ReadFlash(uint offset) => offset switch
    {
        0x00 => _latency,
        0x0C => _flashStatr,
        0x10 => _flashCtlr,
        0x14 => _flashAddress,
        0x1C => OptionByteRegister(),
        0x20 => 0xFFFF_0000 | ((uint)_optionBytes[10] << 8) | _optionBytes[8],
        _ => 0,
    };

    /// <summary>FLASH_OBR from the option bytes: DATA1, DATA0, 2'b11, USER's START_MODE, RST_MODE, STANDBY_RST and IWDG_SW, RDPRT.</summary>
    private uint OptionByteRegister()
    {
        var user = (uint)_optionBytes[2];
        return ((uint)_optionBytes[6] << 18) | ((uint)_optionBytes[4] << 10) | (3u << 8) | (((user >> 5) & 1) << 7) |
            (((user >> 3) & 3) << 5) | (((user >> 2) & 1) << 4) | ((user & 1) << 2) | (_optionBytes[0] == 0xA5 ? 0 : 2u);
    }

    private void WriteFlash(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x00:
                _latency = value & 3;
                CheckLatency();
                return;
            case 0x04:
                _flashKeys = value == FlashKey1 ? 1 : value == FlashKey2 && _flashKeys == 1 ? 2 : 0;
                if (_flashKeys == 2)
                {
                    _flashCtlr &= ~0x80u;
                }

                return;
            case 0x0C:
                _flashStatr &= ~(value & 0x30); // EOP and WRPRTERR: write 1 to clear
                return;
            case 0x10:
                if ((value & 0x0003_0041) != 0) // PG, STRT, FTPG, FTER
                {
                    Fail("the program started erasing or programming the flash (FLASH_CTLR), which the simulator does not simulate");
                    return;
                }

                _flashCtlr = (_flashCtlr & 0x8080) | (value & 0x0000_1E36) | (value & 0x8080);
                return;
            case 0x14:
                _flashAddress = value;
                return;
        }
    }
}
