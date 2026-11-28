using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Riscv32;

namespace ChipSharp.Runtime.Ch32v003;

/// <summary>
/// The runtime of WCH CH32V003 programs (target "ch32v003" and its boards, e.g. "ch32v003-evt"): the RISC-V runtime's
/// E variant (<see cref="Riscv32Runtime.Embedded"/>, "riscv32e": the reset handler, the trap entry and its dispatch, the
/// helpers with the multiplication and division RV32EC has no instruction for, float math, the failure stop) plus what
/// this chip needs, which implements the hardware contract (src/ChipSharp.Runtime.Shared/chipsharp_hardware.h):
/// <list type="bullet">
/// <item><see cref="VectorTableFileName"/>: the vector table, the first 156 bytes of flash (the target's "link.head"),
/// where the core starts: a jump to <c>chipsharp_reset</c>, then <c>chipsharp_trap</c>'s address for each of the PFIC's
/// other 38 numbers.</item>
/// <item><see cref="BootFileName"/>: <c>chipsharp_chip_boot</c>, the chip's first steps (before .data and .bss): HPE and
/// nesting off (INTSYSCR), mtvec at the vector table (vectored, addresses), and the clock from 8 MHz to 48 MHz (the
/// flash's wait state, the PLL from the HSI).</item>
/// <item><see cref="RegistersFileName"/>, <see cref="InterruptsFileName"/>, <see cref="PinsFileName"/>: the chip's
/// registers, interrupts and pins, as ChipSharp.Chips writes them from WCH's SVD and chips/ch32v003.json
/// (src/ChipSharp.Chips/Generated); <see cref="ChipHeaderFileName"/> has what the runtime's C files share.</item>
/// <item><see cref="SourceFileName"/>: start-up (the ports' clocks, SysTick counting at 6 MHz), SysTick's 64-bit count
/// and its one compare, <c>ChipSharp_Runtime_Delay*</c>, <c>ChipSharp_Runtime_Clock*</c>,
/// <c>ChipSharp_Runtime_Register*</c>, and what runs before the program stops.</item>
/// <item><see cref="PinsSourceFileName"/>: <c>ChipSharp_Runtime_Pin*</c> by port * 8 + bit (PA1-PA2, PC0-PC7, PD0 and
/// PD2-PD6; PD1, the debug wire, and PD7, NRST, are left alone), the pulls up and down, and the pin event on EXTI's lines
/// 0-7.</item>
/// <item><see cref="TimerFileName"/>: the timer event on SysTick's compare.</item>
/// <item><see cref="UsartFileName"/>: USART1 on PD5 and PD6 as the console (Console.Write, Console.Read, the failure
/// messages), ChipSharp.Hardware's Uart port 0 with a 16-byte receive buffer, and the uart event;
/// <see cref="ConsoleFileName"/> makes the text, shared with the other runtimes.</item>
/// <item><see cref="FailuresFileName"/>: what the program does where .NET would throw, said on the console.</item>
/// </list>
/// The 2 KB of SRAM: the linker keeps only what a program uses (-ffunction-sections, --gc-sections), and the handlers of
/// the interrupts the runtime takes (SysTick's, USART1's), which the trap's dispatch names in every program, reach the
/// timer event's and the receiving's code and variables through hooks their modules put in, so that a program that
/// blinks has none of them. targets/chip/ch32v003.json says what the chip is and why the start-up is as it is.
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

    /// <summary>What the runtime's C files share: the time on SysTick, the hooks, the ports' registers by pin.</summary>
    public const string ChipHeaderFileName = "ch32v003_chip.h";

    /// <summary>The C file with the start-up, SysTick, the delays, the clock, the registers and the program's end.</summary>
    public const string SourceFileName = "ch32v003_runtime.c";

    /// <summary>The C file with the pins, their pulls and the pin event.</summary>
    public const string PinsSourceFileName = "ch32v003_pins.c";

    /// <summary>The C file with the timer event.</summary>
    public const string TimerFileName = "ch32v003_timer.c";

    /// <summary>The C file with USART1: the console both ways, Uart and the uart event.</summary>
    public const string UsartFileName = "ch32v003_usart.c";

    /// <summary>The C file with what the program does where .NET would throw, said on the console.</summary>
    public const string FailuresFileName = "ch32v003_failures.c";

    /// <summary>The C file with Console.Write and Console.WriteLine, shared with the other runtimes (src/ChipSharp.Runtime.Shared).</summary>
    public const string ConsoleFileName = "chipsharp_console.c";

    /// <summary>The C file that writes floats and doubles as .NET does, shared with the other runtimes.</summary>
    public const string FloatTextFileName = "chipsharp_float_text.c";

    /// <summary>The system clock the start-up sets: the PLL, twice the HSI's 24 MHz.</summary>
    public const long ClockHertz = 48_000_000;

    private readonly Riscv32Runtime _riscv32e = Riscv32Runtime.Embedded();

    public string Name => "ch32v003";

    /// <summary>
    /// The RISC-V start-up and helpers (riscv32e's), the chip's registers, interrupts and pins, its vector table and its
    /// first steps, its own runtime functions, the console, the text of floats and the failures (the linker leaves out of
    /// a program what it does not use). A board's "clockHz", when it says one, must be the 48 MHz the start-up sets.
    /// </summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Setting("clockHz") is { } clock && clock != ClockHertz.ToString(CultureInfo.InvariantCulture))
        {
            throw new ChipSharpException($"Target '{target.Name}': 'clockHz' is \"{clock}\"; the ch32v003 runtime runs the " +
                $"CPU at {ClockHertz} (the PLL, twice the HSI's 24 MHz).");
        }

        // with a heap, the RISC-V runtime has the text of floats already (strings need it)
        var riscv32e = _riscv32e.Startup(target).Files;
        return new RuntimeSources(
        [
            .. riscv32e,
            Source(RegistersFileName), Source(InterruptsFileName), Source(PinsFileName), Source(ChipHeaderFileName),
            Source(VectorTableFileName), Source(BootFileName), Source(SourceFileName), Source(PinsSourceFileName),
            Source(TimerFileName), Source(UsartFileName), Source(FailuresFileName), Source(ConsoleFileName),
            .. riscv32e.Any(file => file.FileName == FloatTextFileName) ? [] : new[] { Source(FloatTextFileName) },
        ]);
    }

    /// <summary>The RISC-V trap entry and its dispatch (the PFIC's 39 vector numbers, the target's "vectors").</summary>
    public RuntimeSources InterruptEntry(TargetDescription target) => _riscv32e.InterruptEntry(target);

    /// <summary>The RISC-V runtime's: nothing.</summary>
    public RuntimeSources Atomics(TargetDescription target) => _riscv32e.Atomics(target);

    /// <summary>One of this runtime's source files by name, e.g. <see cref="BootFileName"/>.</summary>
    public static GeneratedFile Source(string fileName) => new(fileName, EmbeddedText.Read(typeof(Ch32v003Runtime), fileName));
}
