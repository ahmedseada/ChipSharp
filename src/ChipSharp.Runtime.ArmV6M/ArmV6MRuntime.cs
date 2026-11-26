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
/// <item><see cref="VectorsFileName"/>: the vector table (section .vectors) with weak handlers a chip runtime
/// replaces, e.g. <c>chipsharp_isr_irq0</c>.</item>
/// </list>
/// The sources use the symbols of the linker script the clang toolchain writes for chips (__chipsharp_data_start,
/// __chipsharp_stack_top, ...). A chip's runtime adds its own files to these (see the rp2040 runtime); on its own,
/// this runtime runs a program that only computes.
/// </summary>
public sealed class ArmV6MRuntime : IArchRuntime
{
    public const string StartupFileName = "armv6m_startup.c";
    public const string HelpersFileName = "armv6m_helpers.c";
    public const string DivModFileName = "armv6m_divmod.S";
    public const string VectorsFileName = "armv6m_vectors.c";

    public string Name => "armv6m";

    /// <summary>The reset handler and the compiler's helper functions.</summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([Source(StartupFileName), Source(HelpersFileName), Source(DivModFileName)]);
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
