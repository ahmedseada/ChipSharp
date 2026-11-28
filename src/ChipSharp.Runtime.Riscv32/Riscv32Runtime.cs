using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;

namespace ChipSharp.Runtime.Riscv32;

/// <summary>
/// The runtime parts every 32-bit RISC-V chip (RV32 in machine mode: the ESP32-C5, ...) shares, as freestanding C and
/// assembly that the toolchain compiles with the program:
/// <list type="bullet">
/// <item><see cref="HardwareContract.HeaderFileName"/>: the hardware contract (src/ChipSharp.Runtime.Shared), the
/// functions every chip's runtime provides, which every chip runtime's C file includes.</item>
/// <item><see cref="StartupFileName"/>: the reset handler <c>chipsharp_reset</c>, the program's entry point: gp and the
/// stack, interrupts off, mtvec at the trap entry, the chip's first steps (<c>chipsharp_chip_boot</c>, weak: the
/// ESP32-C5's stops its watchdogs there), then <c>chipsharp_start</c>.</item>
/// <item><see cref="RuntimeFileName"/>: <c>chipsharp_start</c>, which copies .data (unless it was loaded where it is),
/// zeroes .bss, runs <c>chipsharp_chip_init</c> (weak, empty), the program (<c>chipsharp_entry</c>) and
/// <c>chipsharp_halt</c> (WFI forever); and the contract's weak <c>chipsharp_before_halt</c>.</item>
/// <item><see cref="HelpersFileName"/>: what LLVM calls for what RV32IMAC has no instruction for, which is little:
/// 64-bit division and remainder (<c>__udivdi3</c> and friends), and memcpy, memmove, memset and memcmp. The runtime has
/// its own rather than Zig's compiler-rt: these four and the float ones below are all a program calls, and so the
/// toolchain needs nothing but clang.</item>
/// <item>Float math in software, as the chips have no F or D: <see cref="CompilerRt"/> (the very compiler-rt files the
/// armv6m runtime ships, which on RISC-V define the plain names LLVM calls: <c>__addsf3</c>, <c>__muldf3</c>,
/// <c>__ltdf2</c>, <c>__fixdfsi</c>, ...), and the shared fmod and Math methods (<see cref="FloatMathFileName"/>,
/// <see cref="MathFileNames"/>).</item>
/// <item><see cref="FailuresFileName"/>: what an index outside an array (and the other failures .NET throws an exception
/// for) does on a chip whose runtime says nothing: the failure stop, <c>chipsharp_halt_failure</c> (EBREAK). Weak, so a
/// chip runtime with a console replaces them; and <c>chipsharp_stack_overflow</c>, for the code generator's stack
/// checks.</item>
/// <item><see cref="TrapFileName"/> and <see cref="VectorsFileName"/> (<see cref="InterruptEntry"/>): the trap entry
/// <c>chipsharp_trap</c> (section .vectors, 64-byte aligned) and its dispatch, written for the target
/// (<see cref="Vectors"/>): an interrupt's id (mcause's bits 11-0) runs <c>chipsharp_isr_irq&lt;id&gt;</c>, weak, for as
/// many ids as the chip's <see cref="VectorsSetting"/> says; an exception is the failure stop.</item>
/// <item>For a target with a heap: objects, the allocator and the garbage collector, shared with the other runtimes, and
/// what they need from the chip (<see cref="HeapChipFileName"/>: the heap region, the callee-saved registers and the
/// stack from there to <c>__chipsharp_stack_top</c>, interrupts off around an event's add and remove, no allocation in
/// an interrupt handler).</item>
/// </list>
/// The sources use the symbols of the linker script the clang toolchain writes for chips (__chipsharp_data_start,
/// __chipsharp_stack_top, __global_pointer$, ...). A chip's runtime adds its own files to these (the esp32c5 runtime);
/// on its own, this runtime runs a program that only computes. The linker keeps only the functions a program uses.
/// <para>
/// <see cref="Embedded"/> is the runtime "riscv32e", for RV32E chips (the CH32V003's RV32EC): the same files, which say
/// on their own what differs there (<c>__riscv_e</c>: the trap entry saves the ten registers RV32E's C functions may
/// change, the heap's scan its two callee-saved ones), and, as such a core has no M, <see cref="ArithmeticFileName"/>:
/// the 32-bit multiplication and division and the 64-bit multiplication LLVM calls.
/// </para>
/// </summary>
public sealed class Riscv32Runtime : IArchRuntime
{
    /// <summary>The runtime "riscv32": RV32 chips with M (the ESP32-C5's RV32IMAC).</summary>
    public Riscv32Runtime()
        : this(embedded: false)
    {
    }

    private Riscv32Runtime(bool embedded)
    {
        IsEmbedded = embedded;
    }

    /// <summary>The runtime "riscv32e": RV32E chips with no M (the CH32V003's RV32EC, target "riscv32e").</summary>
    public static Riscv32Runtime Embedded() => new(embedded: true);

    /// <summary>True for the runtime "riscv32e" (<see cref="Embedded"/>).</summary>
    public bool IsEmbedded { get; }

    /// <summary>
    /// The C file with what LLVM calls on a core without M (the riscv32e runtime's): __mulsi3, __divsi3, __udivsi3,
    /// __modsi3, __umodsi3 and __muldi3.
    /// </summary>
    public const string ArithmeticFileName = "riscv32e_arithmetic.c";

    public const string StartupFileName = "riscv32_startup.S";

    /// <summary>The C file with chipsharp_start and the program's end.</summary>
    public const string RuntimeFileName = "riscv32_runtime.c";

    public const string HelpersFileName = "riscv32_helpers.c";

    /// <summary>The C file with the weak failure functions (ChipSharp_Runtime_IndexOutOfRange and friends).</summary>
    public const string FailuresFileName = "riscv32_failures.c";

    /// <summary>The assembly file with the trap entry, chipsharp_trap.</summary>
    public const string TrapFileName = "riscv32_trap.S";

    /// <summary>The C file with the trap's dispatch, which <see cref="InterruptEntry"/> writes for the target.</summary>
    public const string VectorsFileName = "riscv32_vectors.c";

    /// <summary>The C file with fmod and the Math methods (src/ChipSharp.Runtime.Shared).</summary>
    public const string FloatMathFileName = "chipsharp_float_math.c";

    /// <summary>
    /// The C files with the transcendental functions of Math and MathF (src/ChipSharp.Runtime.Shared, shared with the
    /// other runtimes): ChipSharp's entry points, and the functions they call from musl and from AMD's math library.
    /// </summary>
    public static IReadOnlyList<string> MathFileNames { get; } =
        ["chipsharp_math.c", "chipsharp_math_musl.c", "chipsharp_math_amd.c"];

    /// <summary>Strings (src/ChipSharp.Runtime.Shared): their descriptors, and what reads them and chars.</summary>
    public const string StringFileName = "chipsharp_string.c";

    /// <summary>The text of C#'s throw (src/ChipSharp.Runtime.Shared).</summary>
    public const string ExceptionsFileName = "chipsharp_exceptions.c";

    /// <summary>Objects, the allocator and the garbage collector (src/ChipSharp.Runtime.Shared), for a target with a heap.</summary>
    public const string HeapFileName = "chipsharp_heap.c";

    /// <summary>What the heap needs from a RISC-V chip.</summary>
    public const string HeapChipFileName = "riscv32_heap.c";

    /// <summary>What makes strings (src/ChipSharp.Runtime.Shared), for a target with a heap.</summary>
    public const string StringHeapFileName = "chipsharp_string_heap.c";

    /// <summary>The text of floats and doubles (src/ChipSharp.Runtime.Shared), which strings on the heap need.</summary>
    public const string FloatTextFileName = "chipsharp_float_text.c";

    /// <summary>
    /// The target setting with the number of interrupt ids the trap's dispatch has a handler for: the ESP32-C5's CLIC has
    /// 48 (0-15 the core's own, 16-47 its 32 interrupt lines).
    /// </summary>
    public const string VectorsSetting = "vectors";

    /// <summary>The most interrupt ids a dispatch may have: a CLIC's 4096.</summary>
    public const int MostVectors = 4096;

    public string Name => IsEmbedded ? "riscv32e" : "riscv32";

    /// <summary>
    /// The reset handler, the start and the stops, the compiler's helper functions (riscv32e's multiplication and
    /// division too) and float math, strings, the failures; the heap for a target with one.
    /// </summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            Source(HardwareContract.HeaderFileName),
            Source(StartupFileName), Source(RuntimeFileName), Source(HelpersFileName),
            .. IsEmbedded ? [Source(ArithmeticFileName)] : Array.Empty<GeneratedFile>(), .. CompilerRt.Sources(),
            Source(FloatMathFileName), .. MathFileNames.Select(Source), Source(StringFileName), Source(ExceptionsFileName),
            Source(FailuresFileName),
            .. target.HasHeap
                ? [Source(HeapFileName), Source(HeapChipFileName), Source(StringHeapFileName), Source(FloatTextFileName)]
                : Array.Empty<GeneratedFile>(),
        ]);
    }

    /// <summary>The trap entry, and its dispatch for as many interrupt ids as the target's <see cref="VectorsSetting"/> says.</summary>
    public RuntimeSources InterruptEntry(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            Source(TrapFileName), new GeneratedFile(VectorsFileName, Vectors(target.Name, VectorCount(target))),
        ]);
    }

    /// <summary>Nothing: ChipSharp's few atomic steps turn interrupts off around them (the heap's lock).</summary>
    public RuntimeSources Atomics(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([]);
    }

    /// <summary>The number of interrupt ids of the target's dispatch, from its <see cref="VectorsSetting"/>.</summary>
    public static int VectorCount(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var setting = target.Setting(VectorsSetting) ?? throw new ChipSharpException(
            $"Target '{target.Name}': '{VectorsSetting}' is missing: the number of interrupt ids the chip's interrupts " +
            "come with in mcause (48 on the ESP32-C5's CLIC), from its manual.");
        return int.TryParse(setting, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count is >= 1 and <= MostVectors
            ? count
            : throw new ChipSharpException(
                $"Target '{target.Name}': '{VectorsSetting}' is \"{setting}\"; it must be the number of the chip's " +
                $"interrupt ids, from 1 to {MostVectors} (48 on the ESP32-C5).");
    }

    /// <summary>
    /// The C of the trap's dispatch for <paramref name="count"/> interrupt ids: <c>chipsharp_trap_dispatch</c>, which the
    /// trap entry calls with mcause and mepc. An interrupt (mcause's bit 31) runs the handler of its id (bits 11-0):
    /// <c>chipsharp_isr_irq&lt;id&gt;</c>, weak, <c>chipsharp_unhandled_interrupt</c> (the failure stop) unless a chip
    /// runtime or a program's [Interrupt] method defines it; while it runs, <c>chipsharp_in_interrupt</c> is 1. An
    /// exception is <c>chipsharp_exception</c> (weak): the failure stop, or for a breakpoint (mcause 3: the failure
    /// stop's own EBREAK, with no debugger to stop at it) sleep for good.
    /// </summary>
    public static string Vectors(string target, int count)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var number = count.ToString(CultureInfo.InvariantCulture);
        var text = new StringBuilder()
            .Append("// ChipSharp runtime for 32-bit RISC-V chips: the trap's dispatch of target '").Append(target).Append("', ")
            .Append(number).Append(" interrupt ids (its \"").Append(VectorsSetting).Append("\"), written by the riscv32 runtime.\n")
            .Append("""
                //
                // The trap entry (riscv32_trap.S) calls chipsharp_trap_dispatch with mcause and mepc, its registers saved. An
                // interrupt (mcause's bit 31 set) runs the handler of its id, mcause's bits 11-0 (a CLINT's cause, a CLIC's
                // interrupt id): chipsharp_isr_irq<id>, which a chip runtime, or a program's [Interrupt] method, defines
                // (the target's "interrupts" table names the id of each event). Every one nobody defines is
                // chipsharp_unhandled_interrupt, which stops the program as a failure does: an interrupt was turned on
                // with nothing to handle it, and the chip would come back to it again and again. An exception is the
                // program gone wrong (an instruction the core does not have, an address nothing is at): the failure stop;
                // a breakpoint exception is the failure stop's own EBREAK on a chip with no debugger attached, after
                // which the core sleeps for good, in the trap, with interrupts off.

                __attribute__((noreturn)) void chipsharp_halt_failure(void);

                // How many interrupt handlers run now: 0, or 1 in one (they do not nest), for the heap's check
                volatile unsigned int chipsharp_in_interrupt;

                void chipsharp_unhandled_interrupt(void)
                {
                    chipsharp_halt_failure();
                }

                __attribute__((weak)) void chipsharp_exception(unsigned int cause, unsigned int pc)
                {
                    (void)pc;
                    if (cause == 3)
                    {
                        for (;;)
                        {
                            __asm__ volatile("wfi");
                        }
                    }

                    chipsharp_halt_failure();
                }

                #define CHIPSHARP_HANDLER(name) void name(void) __attribute__((weak, alias("chipsharp_unhandled_interrupt")))


                """);
        for (var id = 0; id < count; id++)
        {
            text.Append("CHIPSHARP_HANDLER(").Append(Handler(id)).Append(");\n");
        }

        text.Append("\nstatic void (*const chipsharp_isr_handlers[").Append(number).Append("])(void) =\n{\n");
        for (var id = 0; id < count; id++)
        {
            text.Append("    ").Append(Handler(id)).Append(",\n");
        }

        return text.Append("};\n").Append($$"""

            void chipsharp_trap_dispatch(unsigned int cause, unsigned int pc)
            {
                if ((cause & 0x80000000u) == 0)
                {
                    chipsharp_exception(cause & 0xfffu, pc);
                    return;
                }

                unsigned int id = cause & 0xfffu;
                chipsharp_in_interrupt = 1;
                if (id < {{number}}u)
                {
                    chipsharp_isr_handlers[id]();
                }
                else
                {
                    chipsharp_unhandled_interrupt();
                }

                chipsharp_in_interrupt = 0;
            }

            """).ToString().ReplaceLineEndings("\n");
    }

    /// <summary>The handler of interrupt id <paramref name="id"/>: the name the code generator exports an [Interrupt] method's entry by.</summary>
    public static string Handler(int id) => "chipsharp_isr_irq" + id.ToString(CultureInfo.InvariantCulture);

    /// <summary>One of this runtime's source files by name, e.g. <see cref="HelpersFileName"/>.</summary>
    public static GeneratedFile Source(string fileName) => new(fileName, EmbeddedText.Read(typeof(Riscv32Runtime), fileName));
}
