using ChipSharp.Abstractions;
using ChipSharp.Targets;
using ChipSharp.Tests.CBackend;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// A target the backend conformance suite builds every program for (<see cref="BackendConformanceTests"/>): the PC through
/// one of ChipSharp's code generators, or a chip through one, run in its simulator. <paramref name="Chip"/> is the
/// simulator's name for the chip (chipsim's pfs154, stm8s103), null for the PC.
/// </summary>
internal sealed record BackendTarget(string Name, string Backend, string? Chip)
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    /// <summary>
    /// The shipped target; a chip's with a console, which the tests' startup has (<see cref="ChipConsole"/>) and its
    /// runtime is to have. The PFS154 keeps 16 of its 128 bytes of RAM for the stack, not the 48 the target reserves for a
    /// timer event that arrives while a pin is written: the programs are shallow, and the tests' own startup and console
    /// take RAM.
    /// </summary>
    public TargetDescription Load() => Chip is null ? Shipped.Load(Name) : Chip == "pfs154"
        ? CSdcc.WithStack(CSdcc.With(Shipped.Load(Name), "console", "true"), "16")
        : CSdcc.With(Shipped.Load(Name), "console", "true");

    public override string ToString() => Name;
}

/// <summary>
/// Every backend ChipSharp has, by the targets it builds for: each program of every corpus goes through each, on the PC
/// and on the simulated chips, and must print what .NET prints. A new code generator adds its targets here, and the
/// programs it cannot take yet to <see cref="BackendSkips"/>, with why.
/// </summary>
internal static class BackendTargets
{
    public static IReadOnlyList<BackendTarget> All { get; } =
    [
        // the PC: the llvm code generator, the C one; clang and the windows-x64 runtime
        new("host", "llvm", null),
        new("host-c", "c", null),

        // the SDCC chips: the C code generator and SDCC, in chipsim, with the tests' startup and console
        new("pfs154-bare", "c", "pfs154"),
        new("stm8s103-board", "c", "stm8s103"),

        // the ESP32-C5 (M4): the llvm code generator for riscv32 and the esp32c5 runtime, in the chip's simulator, once
        // the simulator and the runtime's console come (wave 1): new("esp32c5-devkitc", "llvm", "esp32c5"), run by the
        // build pipeline as the PC's are. Until then its programs compile and link (Esp32c5ProgramTests)
    ];

    public static BackendTarget Get(string name) => All.Single(target => target.Name == name);
}
