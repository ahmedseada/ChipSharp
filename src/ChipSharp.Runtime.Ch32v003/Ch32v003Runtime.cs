using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Riscv32;

namespace ChipSharp.Runtime.Ch32v003;

/// <summary>
/// The runtime of WCH CH32V003 programs (target "ch32v003" and its boards, e.g. "ch32v003-evt"): the RISC-V runtime's
/// E variant (<see cref="Riscv32Runtime.Embedded"/>, "riscv32e": the reset handler, the trap entry and its dispatch, the
/// helpers with the multiplication and division RV32EC has no instruction for, float math, the failure stop) plus what
/// this chip needs:
/// <list type="bullet">
/// <item><see cref="VectorTableFileName"/>: the vector table, the first 156 bytes of flash (the target's "link.head"),
/// where the core starts: a jump to <c>chipsharp_reset</c>, then <c>chipsharp_trap</c>'s address for each of the PFIC's
/// other 38 numbers.</item>
/// <item><see cref="BootFileName"/>: <c>chipsharp_chip_boot</c>, the chip's first steps (before .data and .bss): HPE and
/// nesting off (INTSYSCR), mtvec at the vector table (vectored, addresses), and the clock from 8 MHz to 48 MHz (the
/// flash's wait state, the PLL from the HSI).</item>
/// <item><see cref="RegistersFileName"/>, <see cref="InterruptsFileName"/>, <see cref="PinsFileName"/>: the chip's
/// registers, interrupts and pins, as ChipSharp.Chips writes them from WCH's SVD and chips/ch32v003.json
/// (src/ChipSharp.Chips/Generated).</item>
/// </list>
/// The pins, the delays, the clock, the console and the events (the hardware contract,
/// src/ChipSharp.Runtime.Shared/chipsharp_hardware.h) come with milestone M5's wave 1; until then this runtime runs a
/// program that only computes. targets/chip/ch32v003.json says what the chip is and why the start-up is as it is.
/// </summary>
public sealed class Ch32v003Runtime : IArchRuntime
{
    /// <summary>The assembly file with the vector table, the section .ch32v003_vectors at the start of flash.</summary>
    public const string VectorTableFileName = "ch32v003_vector_table.S";

    /// <summary>The section of the vector table, which the target's "link.head" puts first in flash.</summary>
    public const string VectorTableSection = ".ch32v003_vectors";

    /// <summary>The C file with the chip's first steps, chipsharp_chip_boot.</summary>
    public const string BootFileName = "ch32v003_boot.c";

    /// <summary>The chip's registers (ChipSharp.Chips, from WCH's SVD and chips/ch32v003.json).</summary>
    public const string RegistersFileName = "ch32v003_registers.h";

    /// <summary>The chip's interrupts: the PFIC's vector numbers.</summary>
    public const string InterruptsFileName = "ch32v003_interrupts.h";

    /// <summary>The chip's pins: PA1-PA2, PC0-PC7, PD0-PD7, their ports' registers.</summary>
    public const string PinsFileName = "ch32v003_pins.h";

    /// <summary>The system clock the start-up sets: the PLL, twice the HSI's 24 MHz.</summary>
    public const long ClockHertz = 48_000_000;

    private readonly Riscv32Runtime _riscv32e = Riscv32Runtime.Embedded();

    public string Name => "ch32v003";

    /// <summary>
    /// The RISC-V start-up and helpers (riscv32e's), the chip's registers, interrupts and pins, its vector table and its
    /// first steps. A board's "clockHz", when it says one, must be the 48 MHz the start-up sets.
    /// </summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Setting("clockHz") is { } clock && clock != ClockHertz.ToString(CultureInfo.InvariantCulture))
        {
            throw new ChipSharpException($"Target '{target.Name}': 'clockHz' is \"{clock}\"; the ch32v003 runtime runs the " +
                $"CPU at {ClockHertz} (the PLL, twice the HSI's 24 MHz).");
        }

        return new RuntimeSources(
        [
            .. _riscv32e.Startup(target).Files,
            Source(RegistersFileName), Source(InterruptsFileName), Source(PinsFileName),
            Source(VectorTableFileName), Source(BootFileName),
        ]);
    }

    /// <summary>The RISC-V trap entry and its dispatch (the PFIC's 39 vector numbers, the target's "vectors").</summary>
    public RuntimeSources InterruptEntry(TargetDescription target) => _riscv32e.InterruptEntry(target);

    /// <summary>The RISC-V runtime's: nothing.</summary>
    public RuntimeSources Atomics(TargetDescription target) => _riscv32e.Atomics(target);

    /// <summary>One of this runtime's source files by name, e.g. <see cref="BootFileName"/>.</summary>
    public static GeneratedFile Source(string fileName) => new(fileName, EmbeddedText.Read(typeof(Ch32v003Runtime), fileName));
}
