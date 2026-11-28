using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;

namespace ChipSharp.Runtime.Pdk14;

/// <summary>
/// The runtime parts every Padauk PDK14 chip (PFS154, PMS154C, PFS172, ...) shares, as SDCC's C and assembly
/// (sdcc -mpdk14), which the toolchain "sdcc" builds with the program. SDCC's linker keeps every file it is given whole
/// and has no weak symbols, so most files are <see cref="GeneratedFile.Library"/>: the toolchain puts them in a library,
/// from which the linker takes a file for a symbol nothing else defines. A program has only what it uses (2K words of
/// program memory, 128 bytes of RAM), and a chip runtime's definition, in a file linked whole, takes the place of a
/// library file's empty one:
/// <list type="bullet">
/// <item><see cref="HardwareContract.HeaderFileName"/>: the hardware contract, which every C file includes; and
/// <see cref="HeaderFileName"/>, what this runtime gives a chip runtime's C (interrupts off and back, the parameters of
/// the contract's reentrant functions).</item>
/// <item><see cref="StartupFileName"/> (linked whole): main(), where SDCC's start-up (the stack, the variables, the
/// chip runtime's __sdcc_external_startup) goes, which runs <c>chipsharp_chip_init</c>, turns interrupts on, runs
/// <c>chipsharp_entry</c> and stops with <c>chipsharp_halt</c>; and the interrupt vector's handler, declared.</item>
/// <item><see cref="InterruptFileName"/> (linked whole, the interrupt entry): the one interrupt handler, into which every
/// file that handles an interrupt puts its part (the area INTSRC), and <c>chipsharp_pdk14_interrupts_back</c>.</item>
/// <item><see cref="StopsFileName"/>: <c>chipsharp_halt</c> (stopsys for good) and <c>chipsharp_halt_failure</c> (the
/// reset instruction, which a simulator stops at), and every failure a program can have that takes no parameter, as
/// other names of the failure stop: a PDK14 chip has no console to say what happened.</item>
/// <item><see cref="ArgumentsFileNames"/>: the copies of a reentrant function's parameters and result, for the chip
/// runtime's assembly entries (see <see cref="HeaderFileName"/>).</item>
/// <item><see cref="DefaultFileNames"/>: what the contract has on every chip that a chip runtime may leave out, each a
/// file of its own: <c>chipsharp_chip_init</c>, <c>chipsharp_before_halt</c> and <c>chipsharp_failure_write</c> empty,
/// <c>ChipSharp_Runtime_LocalUtcOffset</c> 0, <c>chipsharp_hardware_failure</c> the failure stop.</item>
/// <item><see cref="ArgumentFailures"/>: the failures that take parameters, a file each, written for the build: SDCC
/// keeps a function's parameters in RAM, which only a program that can fail so gives them.</item>
/// </list>
/// The program's C (the code generator "c") calls the contract's functions as the header declares them; on its own, this
/// runtime runs a program that only computes.
/// </summary>
public sealed class Pdk14Runtime : IArchRuntime
{
    /// <summary>The C file with main(), linked whole: SDCC puts its start-up there.</summary>
    public const string StartupFileName = "pdk14_startup.c";

    /// <summary>The assembly file with the interrupt handler, linked whole.</summary>
    public const string InterruptFileName = "pdk14_interrupt.s";

    /// <summary>The assembly file with the stops and the failures without parameters.</summary>
    public const string StopsFileName = "pdk14_stops.s";

    /// <summary>
    /// The assembly files with the copies of the reentrant functions' parameters and results: the 8 bytes and the copies
    /// of one or two C# ints (pdk14_arguments.s), of a long, and of a long result, each a file of its own.
    /// </summary>
    public static IReadOnlyList<string> ArgumentsFileNames { get; } =
        ["pdk14_arguments.s", "pdk14_argument_8.s", "pdk14_result_8.s"];

    /// <summary>The header a chip runtime's C includes for what this runtime gives it.</summary>
    public const string HeaderFileName = "pdk14_runtime.h";

    /// <summary>The functions of the contract a chip runtime may leave out, each in a file of its own in the library.</summary>
    public static IReadOnlyList<string> DefaultFileNames { get; } =
    [
        "pdk14_chip_init.c", "pdk14_before_halt.c", "pdk14_failure_write.c", "pdk14_local_utc_offset.s",
        "pdk14_hardware_failure.c",
    ];

    /// <summary>
    /// The failures a program can have that take parameters (the others are in <see cref="StopsFileName"/>), with their
    /// parameters in C, as the code generator passes them: C# ints as chipsharp_i32, longs as chipsharp_i64, text as an
    /// address. (Math.Clamp of doubles, ChipSharp_Runtime_MinMaxFloat64, is not here: SDCC has no 64-bit double.)
    /// </summary>
    public static IReadOnlyList<(string Name, string Parameters)> ArgumentFailures { get; } =
    [
        ("ChipSharp_Runtime_InvalidCast", "const void *from, const void *to"),
        ("ChipSharp_Runtime_EnumCompareType", "const void *passed, const void *type"),
        ("ChipSharp_Runtime_LengthBelowZero", "chipsharp_i32 value"),
        ("ChipSharp_Runtime_SourceIndexBelowZero", "chipsharp_i32 value"),
        ("ChipSharp_Runtime_DestinationIndexBelowZero", "chipsharp_i32 value"),
        ("ChipSharp_Runtime_MinMaxInt64", "chipsharp_i64 min, chipsharp_i64 max"),
        ("ChipSharp_Runtime_MinMaxUInt64", "unsigned long long min, unsigned long long max"),
        ("ChipSharp_Runtime_MinMaxFloat32", "float min, float max"),
        ("ChipSharp_Runtime_RoundingDigits", "chipsharp_i32 maximum"),
        ("ChipSharp_Runtime_InvalidMidpointRounding", "chipsharp_i32 mode"),
        ("ChipSharp_Runtime_Unhandled", "const void *text"),
        ("chipsharp_fail", "const char *message"),
        ("chipsharp_fatal", "const char *message"),
    ];

    public string Name => "pdk14";

    /// <summary>The headers, the start-up, the stops and failures, the parameters' copies and the defaults.</summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            Source(HardwareContract.HeaderFileName),
            Source(HeaderFileName),
            Source(StartupFileName),
            Source(StopsFileName) with { Library = true },
            .. ArgumentsFileNames.Select(name => Source(name) with { Library = true }),
            .. DefaultFileNames.Select(name => Source(name) with { Library = true }),
            .. ArgumentFailures.Select(failure => ArgumentFailure(failure.Name, failure.Parameters)),
        ]);
    }

    /// <summary>The interrupt handler, linked whole (the vector goes there).</summary>
    public RuntimeSources InterruptEntry(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([Source(InterruptFileName)]);
    }

    /// <summary>Nothing: a PDK14 program makes a step atomic by turning interrupts off around it (disgint).</summary>
    public RuntimeSources Atomics(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([]);
    }

    /// <summary>
    /// The library file of the failure <paramref name="name"/>, whose C parameters are <paramref name="parameters"/>:
    /// the failure stop, the parameters unused but where SDCC's callers put them.
    /// </summary>
    public static GeneratedFile ArgumentFailure(string name, string parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(parameters);
        var names = parameters.Split(',').Select(parameter => parameter.Trim().Split(' ', '*')[^1]);
        var text = new StringBuilder()
            .Append("// ChipSharp runtime for Padauk PDK14 chips: ").Append(name)
            .Append(", a failure, written by the pdk14 runtime. A PDK14\n")
            .Append("// chip has no console to say it: the failure stop. A file of its own, as SDCC keeps the parameters in RAM.\n\n")
            .Append("#include \"").Append(HardwareContract.HeaderFileName).Append("\"\n\n")
            .Append("CHIPSHARP_NORETURN void ").Append(name).Append('(').Append(parameters).Append(")\n{\n");
        foreach (var parameter in names)
        {
            text.Append("    (void)").Append(parameter).Append(";\n");
        }

        text.Append("    chipsharp_halt_failure();\n}\n");
        return new GeneratedFile(FailureFileName(name), text.ToString()) { Library = true };
    }

    /// <summary>The file of a failure with parameters: pdk14_fail_ and its name without ChipSharp_Runtime_.</summary>
    public static string FailureFileName(string name) =>
        "pdk14_fail_" + (name.StartsWith(HardwareContract.RuntimePrefix, StringComparison.Ordinal)
            ? name[HardwareContract.RuntimePrefix.Length..]
            : name) + ".c";

    /// <summary>One of this runtime's files by name, e.g. <see cref="StopsFileName"/>, linked whole.</summary>
    public static GeneratedFile Source(string fileName) => new(fileName, EmbeddedText.Read(typeof(Pdk14Runtime), fileName));
}
