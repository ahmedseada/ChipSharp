using System.Buffers.Binary;
using System.Globalization;
using ChipSharp.Simulators.RiscV;

namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// An ESP32-C5 (Espressif; Technical Reference Manual v1.1, datasheet v1.5), as the ESP32-C5-DevKitC-1 has it: the HP
/// CPU (<see cref="RiscVCpu"/>: RV32IMAC, Zicsr, Zifencei, Zcb, Zcmp, Zcmt; the CLIC, <see cref="Esp32c5Clic"/>; the
/// CLINT, <see cref="Esp32c5Clint"/>), its memories and the peripherals a runtime needs, at the addresses of
/// <see cref="Esp32c5Map"/>: 384 KB of HP SRAM at 0x4080_0000, 16 KB of LP SRAM at 0x5000_0000, the flash through the
/// MMU at 0x4200_0000 (what an image puts there, read-only), the ROM's functions at 0x4000_0000 (<see cref="Esp32c5Rom"/>:
/// a few, done in C#; ROM data reads 0). The peripherals: GPIO and IO MUX (<see cref="Esp32c5Gpio"/>), SYSTIMER
/// (<see cref="Esp32c5Systimer"/>), UART0 (<see cref="Esp32c5Uart"/>), USB Serial/JTAG (<see cref="Esp32c5UsbSerialJtag"/>),
/// the watchdogs (<see cref="Esp32c5Mwdt"/>, <see cref="Esp32c5LpWdt"/>), the interrupt matrix (a source's map register
/// at 4 × the source holds its CLIC ID, 16-47, or 0; INT_STATUS_0-2 at 0x150 the sources asserting), INTPRI's
/// CPU_INTR_FROM_CPU_0-3 (software interrupts, sources 23-26), and PCR's clocks: the CPU's (SOC_CLK_SEL: XTAL 48 MHz,
/// RC_FAST, PLL_F160M or PLL_F240M, over CPU_DIV_NUM + 1, applied at BUS_CLOCK_UPDATE, which clears itself; printed as
/// "CPU clock 160 MHz"), UART0's, the watchdogs'; its *_READY bits read 1. The other registers of the peripheral space
/// (0x6000_0000-0x600F_FFFF) only keep what is written. An access of any other address is a bus error: the access-fault
/// exception. A byte or halfword access of a peripheral register reads, or writes, the whole word.
/// <para>
/// Loading (<see cref="Esp32c5Image"/>): an ELF file or an ESP image; the CPU starts at its entry point, as the ROM's
/// loader leaves it: in machine mode, interrupts off, sp at the ROM's stack (0x4085_E5A0), the CPU at XTAL's 48 MHz,
/// MWDT0 and RWDT in flash boot mode and the super watchdog running (the program must feed or turn them off, as ESP-IDF's
/// start-up does), UART0 at 115200 baud.
/// </para>
/// <para>
/// Time: the CPU's cycles at its clock; a peripheral access 3 cycles more (the AHB bus). The run stops as a failure at
/// ebreak (the runtimes' failure stop), at an exception without a trap handler, at a call of a ROM function the
/// simulator does not have, at a watchdog's reset. It has finished when the CPU waits for an interrupt (wfi) with
/// interrupts off (mstatus.MIE 0), once UART0 and the USB port have sent what they hold; or with interrupts on, when no
/// interrupt can come: none is pending, enabled (clicintie) and above the threshold (mintthresh, the level being
/// served), and none will be, from a timer, a byte being sent, a watchdog, or an input to come. With one to come, the
/// interrupt wakes the CPU and its handler runs, as the riscv32 runtime's chipsharp_halt expects (events run on).
/// </para>
/// </summary>
public sealed class Esp32c5Chip : SimulatedChip, IRiscVBus
{
    /// <summary>The ROM's stack, where the ROM leaves sp (soc.h's SOC_ROM_STACK_START).</summary>
    public const uint RomStack = 0x4085_E5A0;

    private const uint SramSize = Esp32c5Map.SramEnd - Esp32c5Map.SramStart;
    private const uint LpSramSize = Esp32c5Map.LpSramEnd - Esp32c5Map.LpSramStart;
    private const int PeripheralCycles = 3;
    private const uint PcrSysclk = 0x110, PcrCpuFreq = 0x118, PcrAhbFreq = 0x11C, PcrBusClockUpdate = 0x144;

    private static readonly Dictionary<uint, uint> PcrResets = new()
    {
        [0x000] = 0x5, [0x004] = 0x40_0000, [0x054] = 0x1D, [0x05C] = 0x40_0000, [0x060] = 0x1D, [0x068] = 0x40_0000,
        [0x06C] = 0x5, [0x070] = 0x40_0000, [0x094] = 0x5, [0x098] = 0x5, [PcrSysclk] = 0xB000_0200, [0x114] = 0xD,
        [0x124] = 0x6008, [0x128] = 0x1FF,
    };

    /// <summary>The PCR registers with READY bits (bit 2; MSPI's 3; the timer groups' 2 to 4), which read 1.</summary>
    private static readonly Dictionary<uint, uint> PcrReady = new()
    {
        [0x000] = 4, [0x00C] = 4, [0x018] = 8, [0x028] = 4, [0x030] = 4, [0x038] = 4, [0x048] = 4, [0x054] = 0x1C,
        [0x060] = 0x1C, [0x06C] = 4, [0x074] = 0xC, [0x094] = 4, [0x098] = 4, [0x09C] = 4, [0x0A0] = 4, [0x0A4] = 4,
        [0x0AC] = 4, [0x0C4] = 4, [0x0CC] = 4, [0x0D0] = 4, [0x0D4] = 4, [0x0DC] = 4, [0x0E4] = 4, [0x0E8] = 4,
        [0x0EC] = 4, [0x164] = 4, [0x16C] = 4, [0x170] = 4,
    };

    private readonly RiscVCpu _cpu;
    private readonly Esp32c5Rom _rom;
    private readonly byte[] _sram = new byte[SramSize];
    private readonly byte[] _lpSram = new byte[LpSramSize];
    private readonly byte[] _flash;
    private readonly Esp32c5Clic _clic = new();
    private readonly Esp32c5Clint _clint;
    private readonly Esp32c5Systimer _systimer;
    private readonly Esp32c5Uart _uart;
    private readonly Esp32c5UsbSerialJtag _usb;
    private readonly Esp32c5Gpio _gpio;
    private readonly Esp32c5Mwdt _mwdt0;
    private readonly Esp32c5Mwdt _mwdt1;
    private readonly Esp32c5LpWdt _lpWdt;
    private readonly uint[] _map = new uint[Esp32c5Map.SourceCount];
    private readonly uint[] _fromCpu = new uint[4];
    private readonly Dictionary<uint, uint> _pcr = new(PcrResets);
    private readonly Dictionary<uint, uint> _registers = [];
    private readonly int _imageBytes;
    private long _cpuHertz = Esp32c5Map.XtalHz;
    private long _cycleFraction;
    private long _nextPeripheralEvent = long.MaxValue;
    private long _deadline = long.MaxValue;
    private bool _replan = true;
    private bool _checkInterrupts = true;

    /// <summary>An ESP32-C5 with <paramref name="image"/> loaded, as the ROM leaves it.</summary>
    public Esp32c5Chip(Esp32c5Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Image = image;
        var flashEnd = image.Segments.Where(s => s.Address is >= Esp32c5Map.FlashMapStart and < Esp32c5Map.FlashMapEnd)
            .Select(s => (long)s.Address + s.Bytes.Length - Esp32c5Map.FlashMapStart).DefaultIfEmpty(0).Max();
        _flash = new byte[Math.Min(flashEnd, Esp32c5Map.FlashMapEnd - Esp32c5Map.FlashMapStart)];
        foreach (var segment in image.Segments)
        {
            Load(segment);
            _imageBytes += segment.Bytes.Length;
        }

        _cpu = new RiscVCpu(this)
        {
            Pc = image.Entry,
            Interrupts = _clic,
            InterruptStateChanged = () => _checkInterrupts = true,
        };
        _cpu.X[2] = RomStack;
        _rom = new Esp32c5Rom(this);
        _clint = new Esp32c5Clint(this);
        _cpu.TimeSource = () => _clint.Time;
        _clic.Changed = () => _checkInterrupts = true;
        _systimer = new Esp32c5Systimer(this);
        _uart = new Esp32c5Uart(this, "UART0", () => _pcr[0x004]) { Changed = () => _replan = true };
        _usb = new Esp32c5UsbSerialJtag(this) { Changed = () => _replan = true };
        _gpio = new Esp32c5Gpio(this) { Changed = () => _replan = true };
        _mwdt0 = new Esp32c5Mwdt(this, 0, () => _pcr[0x05C], Fail);
        _mwdt1 = new Esp32c5Mwdt(this, 1, () => _pcr[0x068], Fail);
        _lpWdt = new Esp32c5LpWdt(this, Fail);
    }

    /// <summary>Loads an ELF file or an ESP image (<see cref="Esp32c5Image.Load"/>) into a new ESP32-C5.</summary>
    public static Esp32c5Chip Load(string path) => new(Esp32c5Image.Load(path));

    public override string Name => "ESP32-C5";

    /// <summary>The image loaded: its segments, entry point and symbols.</summary>
    public Esp32c5Image Image { get; }

    /// <summary>The CPU: its registers and CSRs.</summary>
    public RiscVCpu Cpu => _cpu;

    public override IReadOnlyList<GpioPort> Ports => _gpio.Ports;

    public override ISerialInput? SerialInput => _uart;

    public override SerialMonitor? SerialOutput => _uart.Output;

    public override IReadOnlyList<ISerialInput> SerialInputs => [_uart, _usb];

    public override IReadOnlyList<SerialMonitor> SerialOutputs => [_uart.Output, _usb.Output];

    public override int ImageBytes => _imageBytes;

    /// <summary>The HP SRAM, from 0x4080_0000.</summary>
    public override byte[] Ram => _sram;

    public override int ProgramCounter => (int)_cpu.Pc;

    /// <summary>
    /// Whether the SPLL (the 480 MHz PLL behind PLL_F160M and PLL_F240M) is powered and locked. Not as the ROM leaves the
    /// chip (a boot of an image straight from the ROM: the CPU runs from the crystal, and ESP-IDF's bootloader is what
    /// powers and calibrates the SPLL, through the PMU and the analog I2C: rtc_clk_bbpll_enable and _configure); true
    /// for a program a bootloader started that did (chipsim's --pll-running). A program that selects a PLL as the CPU's
    /// clock source while it is not running has stopped its own clock: the run fails.
    /// </summary>
    public bool PllRunning { get; set; }

    /// <summary>The CPU's clock, in hertz.</summary>
    public long CpuHertz => _cpuHertz;

    /// <summary>The pin GPIOn.</summary>
    public override (GpioPort Port, int Bit)? FindPin(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.StartsWith("GPIO", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < Esp32c5Map.GpioCount)
        {
            return (_gpio.Ports[n / 8], n % 8);
        }

        return null;
    }

    /// <summary>Reads <paramref name="size"/> bytes at <paramref name="address"/> as the CPU would (e.g. a test's variable).</summary>
    public uint Read(uint address, int size)
    {
        var offset = address - Esp32c5Map.SramStart;
        if (offset < SramSize - 3)
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
        var offset = address - Esp32c5Map.SramStart;
        if (offset < SramSize - 3)
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
        var offset = address - Esp32c5Map.SramStart;
        if (offset < SramSize - 3)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(_sram.AsSpan((int)offset));
        }

        if (offset < SramSize)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(_sram.AsSpan((int)offset));
        }

        var flash = address - Esp32c5Map.FlashMapStart;
        if (flash + 2 <= _flash.Length)
        {
            return flash + 4 <= _flash.Length
                ? BinaryPrimitives.ReadUInt32LittleEndian(_flash.AsSpan((int)flash))
                : BinaryPrimitives.ReadUInt16LittleEndian(_flash.AsSpan((int)flash));
        }

        var lp = address - Esp32c5Map.LpSramStart;
        if (lp + 2 <= LpSramSize)
        {
            return lp + 4 <= LpSramSize
                ? BinaryPrimitives.ReadUInt32LittleEndian(_lpSram.AsSpan((int)lp))
                : BinaryPrimitives.ReadUInt16LittleEndian(_lpSram.AsSpan((int)lp));
        }

        throw new RiscVAccessFault(address);
    }

    public int AccessCycles(uint address) => address - Esp32c5Map.PeripheralStart < Esp32c5Map.PeripheralEnd - Esp32c5Map.PeripheralStart
        ? PeripheralCycles
        : 0;

    /// <summary>A byte the ROM's console functions write: to UART0's TX FIFO.</summary>
    internal void RomPutChar(byte value)
    {
        _uart.Write(0, value);
        _replan = true;
    }

    /// <summary>The tick UART0 will have sent what it holds, or long.MaxValue when it sends nothing.</summary>
    internal long UartIdleTick => _uart.IdleTick;

    /// <summary>The ticks to the next event (of a peripheral, from outside, the run's end), at least 1: how long a wait in the ROM may last.</summary>
    internal long TicksToDeadline => Math.Max(1, _deadline - Ticks);

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
        _deadline = deadline;
        var hooks = Hooks is not null;
        while (true)
        {
            if (_checkInterrupts)
            {
                _checkInterrupts = false;
                TakeInterrupt();
            }

            var pc = _cpu.Pc;
            int cycles;
            if (pc - Esp32c5Map.RomStart < Esp32c5Map.RomEnd - Esp32c5Map.RomStart)
            {
                cycles = _rom.Call(pc, _cpu);
            }
            else
            {
                if (hooks)
                {
                    RunHook((int)pc);
                }

                cycles = _cpu.Step();
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
                _deadline = deadline;
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

    private void AddCycles(int cycles)
    {
        _cycleFraction += cycles * TicksPerSecond;
        if (_cycleFraction >= _cpuHertz)
        {
            var ticks = _cycleFraction / _cpuHertz;
            Ticks += ticks;
            _cycleFraction -= ticks * _cpuHertz;
        }
    }

    /// <summary>Takes the interrupt the CLIC lets in, when interrupts are on.</summary>
    private void TakeInterrupt()
    {
        if (!_cpu.InterruptsEnabled || _clic.Best(_cpu.Threshold) is not { } best)
        {
            return;
        }

        var vectored = _clic.Vectored(best.Id);
        if (vectored)
        {
            _clic.Taken(best.Id); // hardware vectoring clears an edge interrupt's pending bit
        }

        AddCycles(_cpu.TakeInterrupt(best.Id, best.Level, vectored));
    }

    /// <summary>
    /// wfi. With interrupts off (mstatus.MIE 0, machine mode), ChipSharp's halt: once UART0 and the USB port have sent
    /// what they hold, the program has finished. With interrupts on, the CPU wakes when an interrupt could be taken, else
    /// time passes to the next event; with none to come, the program has finished too.
    /// </summary>
    private void Sleep(long limit)
    {
        long next;
        if (!_cpu.InterruptsEnabled)
        {
            next = Math.Min(_uart.NextEvent, _usb.NextEvent);
            if (next == long.MaxValue)
            {
                Finish("the program has finished: the CPU waits for an interrupt (wfi) with interrupts off (mstatus.MIE 0)");
                return;
            }
        }
        else if (_clic.Best(_cpu.Threshold) is not null)
        {
            _cpu.Waiting = false;
            _checkInterrupts = true;
            return;
        }
        else
        {
            next = NextWakeEvent();
        }
        if (next == long.MaxValue && !(InputsAhead && InputsCouldWake()))
        {
            Finish("the program has finished: the CPU waits for an interrupt (wfi) that nothing can raise any more");
            return;
        }

        var to = Math.Min(limit, next);
        if (to > Ticks)
        {
            Ticks = to;
        }

        if (Ticks >= Math.Min(next, _nextPeripheralEvent))
        {
            AdvancePeripherals();
        }
    }

    /// <summary>What a waiting CPU must not skip: what could wake it, a watchdog, the serial ports sending.</summary>
    private long NextWakeEvent()
    {
        var threshold = _cpu.Threshold;
        var next = Math.Min(Math.Min(_uart.NextEvent, _usb.NextEvent),
            Math.Min(_mwdt0.NextEvent, Math.Min(_mwdt1.NextEvent, _lpWdt.NextEvent)));
        if (_clic.CouldWake(7, threshold))
        {
            next = Math.Min(next, _clint.NextEvent);
        }

        for (var c = 0; c < 3; c++)
        {
            if (SourceCouldWake(Esp32c5Map.SystimerTarget0Source + c, threshold))
            {
                next = Math.Min(next, _systimer.NextEvent(interruptsOnly: true));
            }
        }

        return next;
    }

    private bool InputsCouldWake()
    {
        var threshold = _cpu.Threshold;
        return (_gpio.InterruptsOn && SourceCouldWake(Esp32c5Map.GpioSource, threshold)) ||
            (_uart.ReceiveInterrupts && SourceCouldWake(Esp32c5Map.Uart0Source, threshold)) ||
            (_usb.ReceiveInterrupts && SourceCouldWake(Esp32c5Map.UsbSerialJtagSource, threshold));
    }

    private bool SourceCouldWake(int source, int threshold) => _map[source] is var id and >= 16 and < Esp32c5Clic.Count &&
        _clic.CouldWake((int)id, threshold);

    private void AdvancePeripherals()
    {
        _uart.Advance();
        _usb.Advance();
        _systimer.Advance();
        _mwdt0.Advance();
        _mwdt1.Advance();
        _lpWdt.Advance();
        Replan();
    }

    /// <summary>The interrupt lines from the sources and the map, and the tick of the next peripheral event.</summary>
    private void Replan()
    {
        _replan = false;
        Span<bool> lines = stackalloc bool[Esp32c5Clic.Count];
        var (low, high) = Sources();
        for (var source = 0; source < Esp32c5Map.SourceCount; source++)
        {
            var on = source < 64 ? (low >> source & 1) != 0 : (high >> (source - 64) & 1) != 0;
            if (on && _map[source] is var id and >= 16 and < Esp32c5Clic.Count)
            {
                lines[(int)id] = true;
            }
        }

        _clic.SetLine(3, _clint.SoftwarePending);
        _clic.SetLine(7, _clint.TimerPending);
        for (var id = 16; id < Esp32c5Clic.Count; id++)
        {
            _clic.SetLine(id, lines[id]);
        }

        _nextPeripheralEvent = Math.Min(Math.Min(Math.Min(_uart.NextEvent, _usb.NextEvent), Math.Min(_systimer.NextEvent(),
            _clint.NextEvent)), Math.Min(_mwdt0.NextEvent, Math.Min(_mwdt1.NextEvent, _lpWdt.NextEvent)));
        _checkInterrupts = true;
    }

    /// <summary>The interrupt sources asserting now: sources 0-63, then 64-83.</summary>
    private (ulong Low, uint High) Sources()
    {
        ulong low = 0;
        void Set(int source, bool on) => low |= on ? 1UL << source : 0;
        Set(Esp32c5Map.LpWdtSource, _lpWdt.InterruptPending);
        for (var n = 0; n < 4; n++)
        {
            Set(Esp32c5Map.FromCpu0Source + n, (_fromCpu[n] & 1) != 0);
        }

        Set(Esp32c5Map.GpioSource, _gpio.InterruptPending);
        Set(Esp32c5Map.Uart0Source, _uart.InterruptPending);
        Set(Esp32c5Map.UsbSerialJtagSource, _usb.InterruptPending);
        Set(Esp32c5Map.Tg0WdtSource, _mwdt0.InterruptPending);
        Set(Esp32c5Map.Tg1WdtSource, _mwdt1.InterruptPending);
        var systimer = _systimer.Status;
        for (var c = 0; c < 3; c++)
        {
            Set(Esp32c5Map.SystimerTarget0Source + c, (systimer & (1u << c)) != 0);
        }

        return (low, 0);
    }

    private void Load(Esp32c5Segment segment)
    {
        var (address, bytes) = (segment.Address, segment.Bytes);
        var end = (long)address + bytes.Length;
        if (address >= Esp32c5Map.SramStart && end <= Esp32c5Map.SramEnd)
        {
            bytes.CopyTo(_sram, address - Esp32c5Map.SramStart);
        }
        else if (address >= Esp32c5Map.LpSramStart && end <= Esp32c5Map.LpSramEnd)
        {
            bytes.CopyTo(_lpSram, address - Esp32c5Map.LpSramStart);
        }
        else if (address >= Esp32c5Map.FlashMapStart && end <= Esp32c5Map.FlashMapEnd)
        {
            bytes.CopyTo(_flash, address - Esp32c5Map.FlashMapStart);
        }
        else
        {
            throw new ImageException(string.Create(CultureInfo.InvariantCulture,
                $"the image has {bytes.Length} bytes at 0x{address:x8}, which is not memory the ESP32-C5 loads: HP SRAM " +
                $"0x40800000-0x4085ffff, LP SRAM 0x50000000-0x50003fff, flash through the MMU 0x42000000-0x43ffffff"));
        }
    }

    private uint ReadOther(uint address, int size)
    {
        var shift = (int)(address & 3) * 8;
        if (address - Esp32c5Map.PeripheralStart < Esp32c5Map.PeripheralEnd - Esp32c5Map.PeripheralStart)
        {
            return Narrow(ReadPeripheral(address & ~3u), shift, size);
        }

        var lp = address - Esp32c5Map.LpSramStart;
        if (lp + (uint)size <= LpSramSize)
        {
            return Narrow(ReadBytes(_lpSram, lp, size), 0, size);
        }

        var flash = address - Esp32c5Map.FlashMapStart;
        if (flash + (uint)size <= _flash.Length)
        {
            return Narrow(ReadBytes(_flash, flash, size), 0, size);
        }

        var sram = address - Esp32c5Map.SramStart;
        if (sram + (uint)size <= SramSize)
        {
            return Narrow(ReadBytes(_sram, sram, size), 0, size);
        }

        if (address - Esp32c5Map.Clic < 0x1_0000)
        {
            return _clic.Read(address - Esp32c5Map.Clic, size);
        }

        if (address - Esp32c5Map.Clint < 0x1_0000)
        {
            return Narrow(_clint.Read((address - Esp32c5Map.Clint) & ~3u), shift, size);
        }

        if (address - Esp32c5Map.RomStart < Esp32c5Map.RomEnd - Esp32c5Map.RomStart)
        {
            return 0; // the ROM's data: the simulator has no copy of it
        }

        throw new RiscVAccessFault(address);
    }

    private void WriteOther(uint address, int size, uint value)
    {
        var shift = (int)(address & 3) * 8;
        if (address - Esp32c5Map.PeripheralStart < Esp32c5Map.PeripheralEnd - Esp32c5Map.PeripheralStart)
        {
            WritePeripheral(address & ~3u, size == 4 ? value : (value & (size == 1 ? 0xFFu : 0xFFFFu)) << shift);
            return;
        }

        var lp = address - Esp32c5Map.LpSramStart;
        if (lp + (uint)size <= LpSramSize)
        {
            WriteBytes(_lpSram, lp, size, value);
            return;
        }

        var sram = address - Esp32c5Map.SramStart;
        if (sram + (uint)size <= SramSize)
        {
            WriteBytes(_sram, sram, size, value);
            return;
        }

        if (address - Esp32c5Map.Clic < 0x1_0000)
        {
            _clic.Write(address - Esp32c5Map.Clic, size, value);
            return;
        }

        if (address - Esp32c5Map.Clint < 0x1_0000)
        {
            var register = (address - Esp32c5Map.Clint) & ~3u;
            _clint.Write(register, size == 4 ? value : (_clint.Read(register) & ~(Mask(size) << shift)) | ((value & Mask(size)) << shift));
            _replan = true;
            return;
        }

        throw new RiscVAccessFault(address); // the ROM, the flash's mapping (read-only) and what is not there
    }

    private static uint Mask(int size) => size == 4 ? uint.MaxValue : (1u << (8 * size)) - 1;

    private static uint Narrow(uint word, int shift, int size) => (word >> shift) & Mask(size);

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

    private uint ReadPeripheral(uint address)
    {
        if (Ticks >= _nextPeripheralEvent)
        {
            AdvancePeripherals();
        }

        switch (address)
        {
            case >= Esp32c5Map.Uart0 and < Esp32c5Map.Uart0 + 0x1000:
                if (address == Esp32c5Map.Uart0)
                {
                    _replan = true; // a byte taken from the RX FIFO
                }

                return _uart.Read(address - Esp32c5Map.Uart0);
            case >= Esp32c5Map.Timg0 and < Esp32c5Map.Timg0 + 0x1000:
                return _mwdt0.Read(address - Esp32c5Map.Timg0);
            case >= Esp32c5Map.Timg1 and < Esp32c5Map.Timg1 + 0x1000:
                return _mwdt1.Read(address - Esp32c5Map.Timg1);
            case >= Esp32c5Map.Systimer and < Esp32c5Map.Systimer + 0x1000:
                return _systimer.Read(address - Esp32c5Map.Systimer);
            case >= Esp32c5Map.UsbSerialJtag and < Esp32c5Map.UsbSerialJtag + 0x1000:
                if (address == Esp32c5Map.UsbSerialJtag)
                {
                    _replan = true;
                }

                return _usb.Read(address - Esp32c5Map.UsbSerialJtag);
            case >= Esp32c5Map.InterruptMatrix and < Esp32c5Map.InterruptMatrix + 0x800:
                return ReadInterruptMatrix(address - Esp32c5Map.InterruptMatrix);
            case >= Esp32c5Map.IoMux and < Esp32c5Map.IoMux + 0x200:
                return _gpio.ReadIoMux(address - Esp32c5Map.IoMux);
            case >= Esp32c5Map.Gpio and < Esp32c5Map.Gpio + 0xE00:
                return _gpio.ReadGpio(address - Esp32c5Map.Gpio);
            case >= Esp32c5Map.Pcr and < Esp32c5Map.Pcr + 0x1000:
                var offset = address - Esp32c5Map.Pcr;
                return offset == PcrBusClockUpdate ? 0 : _pcr.GetValueOrDefault(offset) | PcrReady.GetValueOrDefault(offset);
            case >= Esp32c5Map.LpWdt and < Esp32c5Map.LpWdt + 0x400:
                return _lpWdt.Read(address - Esp32c5Map.LpWdt);
            case >= Esp32c5Map.Intpri + 0x90 and < Esp32c5Map.Intpri + 0xA0:
                return _fromCpu[(address - Esp32c5Map.Intpri - 0x90) / 4];
            default:
                return _registers.GetValueOrDefault(address);
        }
    }

    private void WritePeripheral(uint address, uint value)
    {
        if (Ticks >= _nextPeripheralEvent)
        {
            AdvancePeripherals();
        }

        _replan = true;
        switch (address)
        {
            case >= Esp32c5Map.Uart0 and < Esp32c5Map.Uart0 + 0x1000:
                _uart.Write(address - Esp32c5Map.Uart0, value);
                return;
            case >= Esp32c5Map.Timg0 and < Esp32c5Map.Timg0 + 0x1000:
                _mwdt0.Write(address - Esp32c5Map.Timg0, value);
                return;
            case >= Esp32c5Map.Timg1 and < Esp32c5Map.Timg1 + 0x1000:
                _mwdt1.Write(address - Esp32c5Map.Timg1, value);
                return;
            case >= Esp32c5Map.Systimer and < Esp32c5Map.Systimer + 0x1000:
                _systimer.Write(address - Esp32c5Map.Systimer, value);
                return;
            case >= Esp32c5Map.UsbSerialJtag and < Esp32c5Map.UsbSerialJtag + 0x1000:
                _usb.Write(address - Esp32c5Map.UsbSerialJtag, value);
                return;
            case >= Esp32c5Map.InterruptMatrix and < Esp32c5Map.InterruptMatrix + 4 * Esp32c5Map.SourceCount:
                _map[(address - Esp32c5Map.InterruptMatrix) / 4] = value & 0x3F;
                return;
            case >= Esp32c5Map.IoMux and < Esp32c5Map.IoMux + 0x200:
                _gpio.WriteIoMux(address - Esp32c5Map.IoMux, value);
                return;
            case >= Esp32c5Map.Gpio and < Esp32c5Map.Gpio + 0xE00:
                _gpio.WriteGpio(address - Esp32c5Map.Gpio, value);
                return;
            case >= Esp32c5Map.Pcr and < Esp32c5Map.Pcr + 0x1000:
                WritePcr(address - Esp32c5Map.Pcr, value);
                return;
            case >= Esp32c5Map.LpWdt and < Esp32c5Map.LpWdt + 0x400:
                _lpWdt.Write(address - Esp32c5Map.LpWdt, value);
                return;
            case >= Esp32c5Map.Intpri + 0x90 and < Esp32c5Map.Intpri + 0xA0:
                _fromCpu[(address - Esp32c5Map.Intpri - 0x90) / 4] = value & 1;
                return;
            default:
                _registers[address] = value;
                return;
        }
    }

    private uint ReadInterruptMatrix(uint offset)
    {
        if (offset < 4 * Esp32c5Map.SourceCount)
        {
            return _map[offset / 4];
        }

        var (low, high) = Sources();
        return offset switch
        {
            0x150 => (uint)low,
            0x154 => (uint)(low >> 32),
            0x158 => high,
            0x170 => _registers.GetValueOrDefault(Esp32c5Map.InterruptMatrix + offset, 1u),
            0x7FC => 0x0231_2061,
            _ => _registers.GetValueOrDefault(Esp32c5Map.InterruptMatrix + offset),
        };
    }

    private void WritePcr(uint offset, uint value)
    {
        switch (offset)
        {
            case PcrSysclk: // SOC_CLK_SEL and CPU_DBGMD_CLK_EN; the dividers of the PLL and CLK_XTAL_FREQ read only
                _pcr[offset] = (PcrResets[PcrSysclk] & 0x7FFF_FFFF & ~0x3_0000u) | (value & 0x8003_0000);
                return;
            case PcrBusClockUpdate:
                if ((value & 1) != 0)
                {
                    UpdateCpuClock();
                }

                return;
            default:
                _pcr[offset] = value;
                return;
        }
    }

    /// <summary>BUS_CLOCK_UPDATE: the CPU's clock from SOC_CLK_SEL and CPU_DIV_NUM.</summary>
    private void UpdateCpuClock()
    {
        long root = ((_pcr[PcrSysclk] >> 16) & 3) switch
        {
            0 => Esp32c5Map.XtalHz,
            1 => Esp32c5Map.RcFastHz,
            2 => Esp32c5Map.Pll160Hz,
            _ => Esp32c5Map.Pll240Hz,
        };
        var onPll = ((_pcr[PcrSysclk] >> 16) & 3) >= 2;
        if (onPll && !PllRunning)
        {
            Fail("the program selected PLL_F160M or PLL_F240M as the CPU's clock source (PCR_SYSCLK_CONF.SOC_CLK_SEL), but the " +
                "SPLL is not running: the ROM leaves the CPU on the crystal, and the PLL must be powered (PMU) and calibrated " +
                "(analog I2C) first, as ESP-IDF's rtc_clk_bbpll_enable and rtc_clk_bbpll_configure do (chipsim --pll-running " +
                "starts the program as a bootloader that did leaves it)");
            return;
        }

        var hertz = root / ((_pcr.GetValueOrDefault(PcrCpuFreq) & 0xFF) + 1);
        if (onPll)
        {
            // clk_ll_ahb_set_divider: f_ahb <= 48 MHz (the AHB divider of PCR_AHB_FREQ_CONF is for the crystal and RC_FAST;
            // on a PLL the bus has its own divider, which ESP-IDF sets to 4 at 160 MHz and 6 at 240)
            var ahb = root / ((_pcr.GetValueOrDefault(PcrAhbFreq) & 0xFF) + 1);
            if (ahb > 48_000_000)
            {
                Fail(string.Create(CultureInfo.InvariantCulture,
                    $"the CPU is on a PLL with the AHB clock at {ahb / 1e6:0.###} MHz, above its 48 MHz limit: ") +
                    "PCR_AHB_FREQ_CONF's AHB_DIV_NUM must be set first (ESP-IDF's clk_ll_ahb_set_divider: 4 at 160 MHz, 6 at 240)");
                return;
            }
        }
        if (hertz != _cpuHertz)
        {
            _cycleFraction = _cycleFraction * hertz / _cpuHertz;
            _cpuHertz = hertz;
            Print(string.Create(CultureInfo.InvariantCulture, $"CPU clock {hertz / 1e6:0.###} MHz"));
        }
    }
}
