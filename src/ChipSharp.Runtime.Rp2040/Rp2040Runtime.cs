using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;

namespace ChipSharp.Runtime.Rp2040;

/// <summary>
/// The runtime of Raspberry Pi RP2040 programs (target "rp2040" and its boards, e.g. "pico"): the ARMv6-M runtime
/// (<see cref="ArmV6MRuntime"/>: reset handler, vector table, divide and multiply helpers) plus what this chip needs:
/// <list type="bullet">
/// <item><see cref="Boot2FileName"/>: the second-stage bootloader the target's "boot.stage2" names, padded and
/// checksummed (<see cref="Boot2Image"/>), in the section .boot2 the target's link settings put first in flash.</item>
/// <item><see cref="SourceFileName"/>: chip start-up (crystal, PLL, 125 MHz clocks, the microsecond timer) and the
/// runtime functions programs call: <c>ChipSharp_Runtime_Pin*</c>, <c>ChipSharp_Runtime_Delay*</c>,
/// <c>ChipSharp_Runtime_Register*</c> (ChipSharp.Hardware) and <c>ChipSharp_Runtime_WriteLine*</c>
/// (Console.WriteLine on UART0: GPIO 0, 115200 baud).</item>
/// <item><see cref="FloatTextFileName"/>: floats and doubles as text, as .NET writes them, shared with the other
/// runtimes (src/ChipSharp.Runtime.Shared).</item>
/// <item><see cref="FailuresFileName"/>: what the program does where .NET would throw.</item>
/// <item><see cref="InterruptsFileName"/>, with the vector table: the timer and pin events of the program's
/// <c>[Interrupt]</c> methods (<c>ChipSharp_Runtime_Timer*</c>, <c>ChipSharp_Runtime_Pin*Watch</c>), and the functions
/// that clear them, which the target's "interrupts" table names.</item>
/// </list>
/// </summary>
public sealed class Rp2040Runtime : IArchRuntime
{
    /// <summary>The C file with the chip's start-up and runtime functions.</summary>
    public const string SourceFileName = "rp2040_runtime.c";

    /// <summary>The C file that writes floats and doubles as .NET does, for Console.WriteLine.</summary>
    public const string FloatTextFileName = "chipsharp_float_text.c";

    /// <summary>The assembly file with the padded boot2.</summary>
    public const string Boot2FileName = "rp2040_boot2.S";

    /// <summary>
    /// The C file with what a program does where .NET would throw: <c>ChipSharp_Runtime_IndexOutOfRange</c>, which
    /// says so on UART0 and stops at a breakpoint.
    /// </summary>
    public const string FailuresFileName = "rp2040_failures.c";

    /// <summary>The C file with the events of <c>[Interrupt]</c> methods: the timer (alarm 1) and pin changes.</summary>
    public const string InterruptsFileName = "rp2040_interrupts.c";

    /// <summary>The second-stage bootloaders this runtime ships, as a target's "boot.stage2" names them.</summary>
    public static IReadOnlyList<string> Boot2Names { get; } = ["boot2_w25q080.bin", "boot2_generic_03h.bin"];

    private readonly ArmV6MRuntime _armv6m = new();

    public string Name => "rp2040";

    /// <summary>
    /// The ARMv6-M start-up and helpers, boot2, the chip's own start-up and runtime functions, and the text of floats.
    /// </summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            .. _armv6m.Startup(target).Files,
            Boot2(target),
            Source(SourceFileName),
            Source(FloatTextFileName),
            Source(FailuresFileName),
        ]);
    }

    /// <summary>
    /// The ARMv6-M vector table and the events of <c>[Interrupt]</c> methods (<see cref="InterruptsFileName"/>); the
    /// handler of the delays' timer interrupt is in <see cref="SourceFileName"/>.
    /// </summary>
    public RuntimeSources InterruptEntry(TargetDescription target) =>
        new([.. _armv6m.InterruptEntry(target).Files, Source(InterruptsFileName)]);

    public RuntimeSources Atomics(TargetDescription target) => _armv6m.Atomics(target);

    /// <summary>The 256 bytes of the boot2 that <paramref name="target"/>'s "boot.stage2" names, padded and checksummed.</summary>
    public static byte[] Boot2Bytes(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var name = Boot2Name(target);
        return Boot2Image.Pad(EmbeddedText.ReadBytes(typeof(Rp2040Runtime), name), name);
    }

    /// <summary>One of this runtime's C files by name, e.g. <see cref="FloatTextFileName"/>.</summary>
    public static GeneratedFile Source(string fileName) => new(fileName, EmbeddedText.Read(typeof(Rp2040Runtime), fileName));

    private static GeneratedFile Boot2(TargetDescription target) =>
        new(Boot2FileName, Boot2Image.ToAssembly(Boot2Bytes(target), Boot2Name(target)));

    private static string Boot2Name(TargetDescription target)
    {
        var name = target.Setting("boot.stage2") ?? throw new ChipSharpException(
            $"Target '{target.Name}': 'boot.stage2' is missing; the RP2040 needs a second-stage bootloader for its " +
            $"flash chip, e.g. \"boot2_w25q080.bin\". This runtime has: {string.Join(", ", Boot2Names)}.");
        return Boot2Names.Contains(name, StringComparer.Ordinal)
            ? name
            : throw new ChipSharpException(
                $"Target '{target.Name}': 'boot.stage2' is \"{name}\", which the rp2040 runtime does not have. " +
                $"It has: {string.Join(", ", Boot2Names)}.");
    }
}
