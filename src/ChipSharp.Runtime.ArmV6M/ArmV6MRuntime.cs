using ChipSharp.Abstractions;

namespace ChipSharp.Runtime.ArmV6M;

/// <summary>
/// The runtime parts every ARMv6-M chip (Cortex-M0 and M0+: RP2040, STM32F0/G0/C0, ...) shares, as freestanding C
/// and assembly that the toolchain compiles with the program:
/// <list type="bullet">
/// <item><see cref="StartupFileName"/>: the reset handler <c>chipsharp_reset</c>, which copies .data, zeroes .bss,
/// calls <c>chipsharp_chip_init</c> (empty unless the chip's runtime defines it), runs <c>chipsharp_entry</c> and
/// then sleeps forever.</item>
/// <item><see cref="HelpersFileName"/> and <see cref="DivModFileName"/>: the functions LLVM calls for what ARMv6-M
/// has no instruction for (32- and 64-bit division, 64-bit multiply and shifts, memory copies), written with
/// 32-bit operations only.</item>
/// <item>Float math in software, as ARMv6-M has no float hardware: <see cref="CompilerRt"/> for the arithmetic and
/// conversions LLVM calls (__aeabi_fadd, __aeabi_dmul, __aeabi_d2iz, ...), <see cref="FloatCompareFileName"/> for its
/// comparisons, and <see cref="FloatMathFileName"/> (shared with the other runtimes) for fmod and the Math methods
/// programs call.</item>
/// <item><see cref="FailuresFileName"/>: what an index outside an array (and the other failures .NET throws an
/// exception for) does on a chip whose runtime says nothing: stop at a breakpoint. Weak, so a chip runtime with a
/// console replaces them.</item>
/// <item><see cref="VectorsFileName"/>: the vector table (section .vectors) with weak handlers a chip runtime
/// replaces, e.g. <c>chipsharp_isr_irq0</c>.</item>
/// <item>For a target with a heap: objects, the allocator and the garbage collector, shared with the other runtimes
/// (<see cref="HeapFileName"/>), and what they need from the chip (<see cref="HeapChipFileName"/>: the linker script's
/// heap region, the registers and the stack, interrupts off while the collector runs, and no allocation in an interrupt
/// handler).</item>
/// </list>
/// The sources use the symbols of the linker script the clang toolchain writes for chips (__chipsharp_data_start,
/// __chipsharp_stack_top, ...). A chip's runtime adds its own files to these (see the rp2040 runtime); on its own,
/// this runtime runs a program that only computes. The linker keeps only the functions a program uses.
/// </summary>
public sealed class ArmV6MRuntime : IArchRuntime
{
    public const string StartupFileName = "armv6m_startup.c";
    public const string HelpersFileName = "armv6m_helpers.c";
    public const string DivModFileName = "armv6m_divmod.S";
    public const string VectorsFileName = "armv6m_vectors.c";

    /// <summary>The C file with the float and double comparisons LLVM calls (__aeabi_fcmplt and friends).</summary>
    public const string FloatCompareFileName = "armv6m_float_compare.c";

    /// <summary>The C file with the weak failure functions (ChipSharp_Runtime_IndexOutOfRange and friends).</summary>
    public const string FailuresFileName = "armv6m_failures.c";

    /// <summary>The C file with fmod and the Math methods (src/ChipSharp.Runtime.Shared).</summary>
    public const string FloatMathFileName = "chipsharp_float_math.c";

    /// <summary>Objects, the allocator and the garbage collector (src/ChipSharp.Runtime.Shared), for a target with a heap.</summary>
    public const string HeapFileName = "chipsharp_heap.c";

    /// <summary>Strings (src/ChipSharp.Runtime.Shared): their descriptors, and what reads them and chars, for every target.</summary>
    public const string StringFileName = "chipsharp_string.c";

    /// <summary>What makes strings (src/ChipSharp.Runtime.Shared), for a target with a heap, with the text of floats.</summary>
    public const string StringHeapFileName = "chipsharp_string_heap.c";

    /// <summary>The text of floats and doubles (src/ChipSharp.Runtime.Shared), which strings on the heap need.</summary>
    public const string FloatTextFileName = "chipsharp_float_text.c";

    /// <summary>What the heap needs from an ARMv6-M chip.</summary>
    public const string HeapChipFileName = "armv6m_heap.c";

    /// <summary>C#'s throw (src/ChipSharp.Runtime.Shared), for a target with a heap: an exception is an object.</summary>
    public const string ExceptionsFileName = "chipsharp_exceptions.c";

    public string Name => "armv6m";

    /// <summary>The reset handler and the compiler's helper functions, float math included; the heap for a target with one.</summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            Source(StartupFileName), Source(HelpersFileName), Source(DivModFileName), Source(FloatCompareFileName),
            Source(FloatMathFileName), .. CompilerRt.Sources(), Source(FailuresFileName), Source(StringFileName),
            .. target.HasHeap
                ? [Source(HeapFileName), Source(HeapChipFileName), Source(StringHeapFileName), Source(FloatTextFileName),
                    Source(ExceptionsFileName)]
                : Array.Empty<GeneratedFile>(),
        ]);
    }

    /// <summary>The vector table and the default exception handlers.</summary>
    public RuntimeSources InterruptEntry(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([Source(VectorsFileName)]);
    }

    /// <summary>Nothing yet: ARMv6-M has no atomic instructions, and M1 programs need none.</summary>
    public RuntimeSources Atomics(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([]);
    }

    /// <summary>One of this runtime's source files by name, e.g. <see cref="HelpersFileName"/>.</summary>
    public static GeneratedFile Source(string fileName) => new(fileName, EmbeddedText.Read(typeof(ArmV6MRuntime), fileName));
}
