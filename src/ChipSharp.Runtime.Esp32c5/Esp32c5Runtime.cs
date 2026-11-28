using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Riscv32;

namespace ChipSharp.Runtime.Esp32c5;

/// <summary>
/// The runtime of Espressif ESP32-C5 programs (target "esp32c5" and its boards, e.g. "esp32c5-devkitc"): the RISC-V
/// runtime (<see cref="Riscv32Runtime"/>: the reset handler, the trap entry and its dispatch, the helpers, float math,
/// the failure stop, and the heap and garbage collector) plus what this chip needs, which implements the hardware
/// contract (src/ChipSharp.Runtime.Shared/chipsharp_hardware.h):
/// <list type="bullet">
/// <item><see cref="BootFileName"/>: <c>chipsharp_chip_boot</c>, the chip's first steps (before .data and .bss): the
/// watchdogs the ROM leaves running stopped (the RTC watchdog, both timer groups', the super watchdog set to feed
/// itself), and mtvec in CLIC mode.</item>
/// <item><see cref="RegistersFileName"/>, <see cref="InterruptsFileName"/>, <see cref="PinsFileName"/>: the chip's
/// registers, interrupt sources and pins, as ChipSharp.Chips writes them from Espressif's SVD and chips/esp32c5.json
/// (src/ChipSharp.Chips/Generated); <see cref="ChipHeaderFileName"/> has what the runtime's C files share.</item>
/// <item><see cref="SourceFileName"/>: chip start-up (the CPU at 160 MHz, SYSTIMER) and the runtime functions programs
/// call: <c>ChipSharp_Runtime_Pin*</c> (GPIO0-GPIO28), <c>ChipSharp_Runtime_Delay*</c>,
/// <c>ChipSharp_Runtime_Clock*</c> (SYSTIMER's 64-bit count), <c>ChipSharp_Runtime_Register*</c>.</item>
/// <item><see cref="InterruptsSourceFileName"/>: the timer and pin events of the program's <c>[Interrupt]</c> methods
/// (<c>ChipSharp_Runtime_Timer*</c>, <c>ChipSharp_Runtime_Pin*Watch</c>), and the functions that clear them, which the
/// target's "interrupts" table names.</item>
/// <item><see cref="UsbFileName"/>: the console, the devkit's USB Serial/JTAG port (Console.Write, Console.Read, the
/// failure messages); <see cref="ConsoleFileName"/> makes the text, shared with the other runtimes.</item>
/// <item><see cref="UartFileName"/>: UART0 (ChipSharp.Hardware's Uart) with a receive buffer, and the uart event.</item>
/// <item><see cref="FailuresFileName"/>: what the program does where .NET would throw, said on the console.</item>
/// <item><see cref="I2cFileName"/>, <see cref="SpiFileName"/>, <see cref="AdcFileName"/>, <see cref="PwmFileName"/>: the
/// I2C controller, GP-SPI2, the SAR ADC1 (one-shot) and the LEDC, as ChipSharp.Hardware's I2c, Spi, Adc and Pwm, with the
/// pins through the GPIO matrix (<see cref="BusHeaderFileName"/> has what they share).</item>
/// </list>
/// The program runs from SRAM, where the ROM loads it: the image at flash offset 0x2000, every segment in SRAM, its entry
/// <c>chipsharp_reset</c> (targets/chip/esp32c5.json says how and why).
/// </summary>
public sealed class Esp32c5Runtime : IArchRuntime
{
    /// <summary>The C file with the chip's first steps, chipsharp_chip_boot.</summary>
    public const string BootFileName = "esp32c5_boot.c";

    /// <summary>The chip's registers (ChipSharp.Chips, from Espressif's SVD and chips/esp32c5.json).</summary>
    public const string RegistersFileName = "esp32c5_registers.h";

    /// <summary>The chip's interrupt sources, which the interrupt matrix routes to the CPU's interrupt lines.</summary>
    public const string InterruptsFileName = "esp32c5_interrupts.h";

    /// <summary>The chip's pins: GPIO 0-28, their IO MUX functions, the GPIO port's registers.</summary>
    public const string PinsFileName = "esp32c5_pins.h";

    /// <summary>What the runtime's C files share: the interrupt routing and its helpers.</summary>
    public const string ChipHeaderFileName = "esp32c5_chip.h";

    /// <summary>The C file with the chip's start-up (the 160 MHz clock, SYSTIMER) and the pins, delays, clock and registers.</summary>
    public const string SourceFileName = "esp32c5_runtime.c";

    /// <summary>The C file with the events of <c>[Interrupt]</c> methods: the timer (alarm 1) and pin changes.</summary>
    public const string InterruptsSourceFileName = "esp32c5_interrupts.c";

    /// <summary>The C file with the console on the USB Serial/JTAG port, and Console.Read.</summary>
    public const string UsbFileName = "esp32c5_usb.c";

    /// <summary>The C file with UART0 (Uart) and the uart event.</summary>
    public const string UartFileName = "esp32c5_uart.c";

    /// <summary>The C file with what the program does where .NET would throw, said on the console.</summary>
    public const string FailuresFileName = "esp32c5_failures.c";

    /// <summary>What the files of the buses and the analog parts share: the crystal's speed, pins through the GPIO matrix.</summary>
    public const string BusHeaderFileName = "esp32c5_bus.h";

    /// <summary>The C file with the I2C controller (I2c).</summary>
    public const string I2cFileName = "esp32c5_i2c.c";

    /// <summary>The C file with GP-SPI2 (Spi).</summary>
    public const string SpiFileName = "esp32c5_spi.c";

    /// <summary>The C file with the SAR ADC1 and the temperature sensor (Adc).</summary>
    public const string AdcFileName = "esp32c5_adc.c";

    /// <summary>The C file with the LEDC (Pwm).</summary>
    public const string PwmFileName = "esp32c5_pwm.c";

    /// <summary>The C file with Console.Write and Console.WriteLine, shared with the other runtimes (src/ChipSharp.Runtime.Shared).</summary>
    public const string ConsoleFileName = "chipsharp_console.c";

    /// <summary>The C file that writes floats and doubles as .NET does, shared with the other runtimes.</summary>
    public const string FloatTextFileName = "chipsharp_float_text.c";

    private readonly Riscv32Runtime _riscv32 = new();

    public string Name => "esp32c5";

    /// <summary>
    /// The RISC-V start-up and helpers (with the heap, for a target with one), the chip's first steps and its registers, its
    /// own start-up and runtime functions, the console, the text of floats, the failures, UART0 and the events' functions
    /// (the linker leaves out of a program what it does not use; the events' handlers, only the program's own entries).
    /// </summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        // with a heap, the RISC-V runtime has the text of floats already (strings need it)
        var riscv32 = _riscv32.Startup(target).Files;
        return new RuntimeSources(
        [
            .. riscv32,
            Source(RegistersFileName), Source(InterruptsFileName), Source(PinsFileName), Source(ChipHeaderFileName),
            Source(BootFileName), Source(SourceFileName), Source(InterruptsSourceFileName), Source(UsbFileName),
            Source(UartFileName), Source(FailuresFileName), Source(ConsoleFileName), ClockHeader(target),
            Source(BusHeaderFileName), Source(I2cFileName), Source(SpiFileName), Source(AdcFileName), Source(PwmFileName),
            .. riscv32.Any(file => file.FileName == FloatTextFileName) ? [] : new[] { Source(FloatTextFileName) },
        ]);
    }

    /// <summary>The RISC-V trap entry and its dispatch (the CLIC's 48 interrupt ids, the target's "vectors").</summary>
    public RuntimeSources InterruptEntry(TargetDescription target) => _riscv32.InterruptEntry(target);

    /// <summary>The RISC-V runtime's: nothing.</summary>
    public RuntimeSources Atomics(TargetDescription target) => _riscv32.Atomics(target);

    /// <summary>The file with what the runtime builds the CPU's clock from: <see cref="ClockHeaderFileName"/>.</summary>
    public const string ClockHeaderFileName = "esp32c5_clock.h";

    /// <summary>The crystal's frequency, which a board's "clockHz" says the CPU runs at when the PLL is not used.</summary>
    public const long CrystalHertz = 48_000_000;

    /// <summary>The CPU frequency of the PLL's PLL_F160M output (a board's opt-in "clockHz").</summary>
    public const long PllHertz = 160_000_000;

    /// <summary>
    /// <see cref="ClockHeaderFileName"/>, written for <paramref name="target"/>: <c>C5_CPU_ON_PLL</c> is 1 when its "clockHz"
    /// is <see cref="PllHertz"/> (the runtime moves the CPU to the PLL, which must be running already: see
    /// esp32c5_runtime.c) and 0 for <see cref="CrystalHertz"/> (the CPU stays as the ROM left it).
    /// </summary>
    public static GeneratedFile ClockHeader(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var text = target.Setting("clockHz") ?? throw new ChipSharpException(
            $"Target '{target.Name}': 'clockHz' is missing: the CPU's clock the board runs at, {CrystalHertz} (the crystal) or " +
            $"{PllHertz} (PLL_F160M, for a program started with the PLL running).");
        var onPll = text == PllHertz.ToString(System.Globalization.CultureInfo.InvariantCulture) ? 1
            : text == CrystalHertz.ToString(System.Globalization.CultureInfo.InvariantCulture) ? 0
            : throw new ChipSharpException($"Target '{target.Name}': 'clockHz' is \"{text}\"; the esp32c5 runtime runs the CPU at " +
                $"{CrystalHertz} (the crystal, the ROM's) or {PllHertz} (PLL_F160M, needs the PLL running already).");
        return new GeneratedFile(ClockHeaderFileName,
            $"// The CPU's clock, from the target's \"clockHz\" {text}, written by the esp32c5 runtime.\n#define C5_CPU_ON_PLL {onPll}\n");
    }

    /// <summary>One of this runtime's source files by name, e.g. <see cref="BootFileName"/>.</summary>
    public static GeneratedFile Source(string fileName) => new(fileName, EmbeddedText.Read(typeof(Esp32c5Runtime), fileName));
}
