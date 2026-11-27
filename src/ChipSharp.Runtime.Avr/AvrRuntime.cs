using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;

namespace ChipSharp.Runtime.Avr;

/// <summary>
/// The runtime parts every AVR chip (ATmega328P, ATmega32U4, ATmega2560, ATtiny85, AVR Dx, ...) shares, as
/// freestanding C and assembly that the toolchain compiles with the program:
/// <list type="bullet">
/// <item><see cref="StartupFileName"/>: the reset handler <c>chipsharp_reset</c>, which clears r1 (the zero register
/// compiled code counts on), puts the stack at the end of RAM, copies .data (with .rodata) from flash, zeroes .bss and
/// jumps to <c>chipsharp_start</c>.</item>
/// <item><see cref="RuntimeFileName"/>: <c>chipsharp_start</c>, which runs <c>chipsharp_chip_init</c> (empty unless the
/// chip's runtime defines it), turns interrupts on and runs <c>chipsharp_entry</c>; and how a program stops:
/// <c>chipsharp_halt</c> when it has ended (interrupts off, SLEEP forever) and <c>chipsharp_halt_failure</c> when it
/// failed (interrupts off, BREAK, then SLEEP forever), both after <c>chipsharp_before_halt</c>, a chip runtime's hook
/// (a console sends what it still holds).</item>
/// <item><see cref="MultiplyFileName"/> and <see cref="DivideFileName"/>: the functions LLVM calls for what AVR has no
/// instruction for, with avr-gcc's names and registers: 32- and 64-bit multiplication (8- and 16-bit too on a chip
/// without MUL), and the division and remainder of 8- to 64-bit values (__udivmodqi4 ... __moddi3).</item>
/// <item><see cref="MemoryFileName"/>: memcpy, memmove, memset and memcmp.</item>
/// <item>Float math in software: <see cref="CompilerRt"/>, the very compiler-rt files the armv6m runtime ships, which on
/// AVR define the plain names LLVM calls (__addsf3, __muldf3, __ltdf2, __fixdfsi, __floatsidf, ...): float and double
/// exactly as IEEE 754 rounds them, as .NET does. AVR's clang makes double 32 bits unless told otherwise: the target's
/// clang flags must have -mdouble=64 (the runtime's C stops with an error without it).</item>
/// <item><see cref="FailuresFileName"/>: what an index outside an array (and the other failures .NET throws an
/// exception for) does on a chip whose runtime says nothing: the failure stop. Weak, so a chip runtime with a console
/// replaces them; and <c>chipsharp_stack_overflow</c>, for the code generator's stack checks.</item>
/// <item><see cref="VectorsFileName"/>, written for the target (<see cref="Vectors"/>): the interrupt vector table
/// (section .vectors), as many entries as the chip's <see cref="VectorsSetting"/> says, with weak handlers a chip runtime
/// or a program replaces, e.g. <c>chipsharp_isr_irq1</c>.</item>
/// </list>
/// The sources use the symbols of the linker script the toolchain writes for chips (__chipsharp_data_start,
/// __chipsharp_stack_top, ...: see <see cref="StartupFileName"/>). A chip's runtime adds its own files to these; on its
/// own, this runtime runs a program that only computes. The linker keeps only the functions a program uses.
/// </summary>
public sealed class AvrRuntime : IArchRuntime
{
    public const string StartupFileName = "avr_startup.S";

    /// <summary>The C file with chipsharp_start and the two ways a program stops.</summary>
    public const string RuntimeFileName = "avr_runtime.c";

    public const string MultiplyFileName = "avr_multiply.S";
    public const string DivideFileName = "avr_divide.S";
    public const string MemoryFileName = "avr_memory.S";

    /// <summary>The C file with the weak failure functions (ChipSharp_Runtime_IndexOutOfRange and friends).</summary>
    public const string FailuresFileName = "avr_failures.c";

    /// <summary>The assembly file with the vector table, which <see cref="InterruptEntry"/> writes for the target.</summary>
    public const string VectorsFileName = "avr_vectors.S";

    /// <summary>
    /// The target setting with the number of entries of the chip's vector table, the reset vector included, e.g. 26 on
    /// the ATmega328P.
    /// </summary>
    public const string VectorsSetting = "vectors";

    public string Name => "avr";

    /// <summary>The reset handler, the start and the stops, the compiler's helper functions and float math, the failures.</summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            Source(StartupFileName), Source(RuntimeFileName), Source(MultiplyFileName), Source(DivideFileName),
            Source(MemoryFileName), .. CompilerRt.Sources(), Source(FailuresFileName),
        ]);
    }

    /// <summary>The vector table, as many entries as the target's <see cref="VectorsSetting"/> says.</summary>
    public RuntimeSources InterruptEntry(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([new GeneratedFile(VectorsFileName, Vectors(target.Name, VectorCount(target)))]);
    }

    /// <summary>Nothing: an AVR program makes a step atomic by turning interrupts off around it.</summary>
    public RuntimeSources Atomics(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([]);
    }

    /// <summary>The number of entries of the target's vector table, from its <see cref="VectorsSetting"/>.</summary>
    public static int VectorCount(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var setting = target.Setting(VectorsSetting) ?? throw new ChipSharpException(
            $"Target '{target.Name}': '{VectorsSetting}' is missing: the number of entries of the chip's interrupt vector " +
            "table, the reset vector included (26 on the ATmega328P), from its datasheet.");
        return int.TryParse(setting, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= 128
            ? count
            : throw new ChipSharpException(
                $"Target '{target.Name}': '{VectorsSetting}' is \"{setting}\"; it must be the number of entries of the " +
                "chip's interrupt vector table, from 1 to 128 (26 on the ATmega328P).");
    }

    /// <summary>
    /// The assembly of a vector table of <paramref name="count"/> entries: entry 0 jumps to <c>chipsharp_reset</c>,
    /// entry n to <c>chipsharp_isr_irq&lt;n&gt;</c>, weak, which is <c>chipsharp_unhandled_interrupt</c> (the failure
    /// stop) unless something defines it.
    /// </summary>
    public static string Vectors(string target, int count)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var text = new StringBuilder()
            .Append("// ChipSharp runtime for AVR chips: the interrupt vector table of target '").Append(target).Append("', ")
            .Append(count.ToString(CultureInfo.InvariantCulture)).Append(" entries (its \"").Append(VectorsSetting)
            .Append("\"), written by the avr runtime.\n")
            .Append("""
                //
                // The table is at the start of flash (section .vectors, which the linker script puts first). Entry 0 is the
                // reset vector; entry n is the chip's interrupt n, counted as avr-libc's __vector_n counts them (1 is INT0 on
                // the ATmega328P). Each entry jumps to chipsharp_reset or to chipsharp_isr_irq<n>: with JMP, two words, on a
                // chip with more than 8 KB of flash, with RJMP, one word, on a smaller one, as the chip's table is laid out.
                // A chip runtime, or a program's [Interrupt] method, handles interrupt n by defining chipsharp_isr_irq<n> (an
                // AVR interrupt handler: it saves what it changes, SREG too, and returns with RETI). Every one nobody defines
                // is chipsharp_unhandled_interrupt, which stops the program as a failure does: an interrupt was turned on
                // with nothing to handle it, and the chip would come back to it again and again.

                #if defined(__AVR_HAVE_JMP_CALL__)
                #define VECTOR jmp
                #else
                #define VECTOR rjmp
                #endif

                    .section .vectors, "ax", @progbits
                    .global chipsharp_vectors
                chipsharp_vectors:
                    VECTOR chipsharp_reset

                """);
        for (var n = 1; n < count; n++)
        {
            text.Append("    VECTOR chipsharp_isr_irq").Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        text.Append("    .size chipsharp_vectors, . - chipsharp_vectors\n\n");
        for (var n = 1; n < count; n++)
        {
            var handler = "chipsharp_isr_irq" + n.ToString(CultureInfo.InvariantCulture);
            text.Append("    .weak ").Append(handler).Append('\n')
                .Append("    .set ").Append(handler).Append(", chipsharp_unhandled_interrupt\n");
        }

        return text.Append("""

                    .section .text.chipsharp_unhandled_interrupt, "ax", @progbits
                    .global chipsharp_unhandled_interrupt
                    .type chipsharp_unhandled_interrupt, @function
                chipsharp_unhandled_interrupt:
                    VECTOR chipsharp_halt_failure
                    .size chipsharp_unhandled_interrupt, . - chipsharp_unhandled_interrupt

                """).ToString().ReplaceLineEndings("\n");
    }

    /// <summary>One of this runtime's source files by name, e.g. <see cref="DivideFileName"/>.</summary>
    public static GeneratedFile Source(string fileName) => new(fileName, EmbeddedText.Read(typeof(AvrRuntime), fileName));
}
