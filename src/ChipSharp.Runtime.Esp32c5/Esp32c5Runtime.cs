using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Riscv32;

namespace ChipSharp.Runtime.Esp32c5;

/// <summary>
/// The runtime of Espressif ESP32-C5 programs (target "esp32c5" and its boards, e.g. "esp32c5-devkitc"): the RISC-V
/// runtime (<see cref="Riscv32Runtime"/>: the reset handler, the trap entry and its dispatch, the helpers, float math,
/// the failure stop) plus what this chip needs so far, M4's wave 0:
/// <list type="bullet">
/// <item><see cref="BootFileName"/>: <c>chipsharp_chip_boot</c>, the chip's first steps (before .data and .bss): the
/// watchdogs the ROM leaves running stopped (the RTC watchdog, both timer groups', the super watchdog set to feed
/// itself), and mtvec in CLIC mode.</item>
/// <item><see cref="RegistersFileName"/>: the chip's registers, as ChipSharp.Chips writes them from Espressif's SVD and
/// chips/esp32c5.json (src/ChipSharp.Chips/Generated).</item>
/// </list>
/// The program runs from SRAM, where the ROM loads it: the image at flash offset 0x2000, every segment in SRAM, its entry
/// <c>chipsharp_reset</c> (targets/chip/esp32c5.json says how and why). Pins, the clock and the delays, the console on
/// UART0 and the events come with the runtime's next part, which implements the hardware contract.
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

    private readonly Riscv32Runtime _riscv32 = new();

    public string Name => "esp32c5";

    /// <summary>The RISC-V start-up and helpers, the chip's first steps and its registers.</summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            .. _riscv32.Startup(target).Files,
            Source(RegistersFileName), Source(InterruptsFileName), Source(PinsFileName), Source(BootFileName),
        ]);
    }

    /// <summary>The RISC-V trap entry and its dispatch (the CLIC's 48 interrupt ids, the target's "vectors").</summary>
    public RuntimeSources InterruptEntry(TargetDescription target) => _riscv32.InterruptEntry(target);

    /// <summary>The RISC-V runtime's: nothing.</summary>
    public RuntimeSources Atomics(TargetDescription target) => _riscv32.Atomics(target);

    /// <summary>One of this runtime's source files by name, e.g. <see cref="BootFileName"/>.</summary>
    public static GeneratedFile Source(string fileName) => new(fileName, EmbeddedText.Read(typeof(Esp32c5Runtime), fileName));
}
