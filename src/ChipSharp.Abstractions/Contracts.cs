using ChipSharp.Ir;

namespace ChipSharp.Abstractions;

/// <summary>Every pluggable part has a name. Target files refer to parts by these names.</summary>
public interface INamedComponent
{
    string Name { get; }
}

/// <summary>
/// Turns a whole program (TIR) into code for a family of targets:
/// "llvm" writes LLVM IR, "c" writes C, a native generator writes machine code directly.
/// </summary>
public interface ICodeGenerator : INamedComponent
{
    GeneratedCode Generate(TirModule module, TargetDescription target);
}

/// <summary>Turns generated code into a linked program, e.g. "lld", "sdcc", "xc8", "gcc-sh".</summary>
public interface IToolchain : INamedComponent
{
    LinkedBinary Build(GeneratedCode code, TargetDescription target);
}

/// <summary>Packages a linked program in the form a chip loads, e.g. "uf2", "esp-image", "ihex".</summary>
public interface IImageFormat : INamedComponent
{
    void Package(LinkedBinary binary, TargetDescription target, Stream output);
}

/// <summary>Puts an image onto a real board, e.g. "uf2-copy", "esptool", "wch-link".</summary>
public interface IFlasher : INamedComponent
{
    Task FlashAsync(string imagePath, FlashOptions options, IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// The small per-architecture part of the runtime: startup code, interrupt entry and atomics.
/// <see cref="INamedComponent.Name"/> is the architecture, e.g. "armv6m", matching a target's "runtime".
/// </summary>
public interface IArchRuntime : INamedComponent
{
    RuntimeSources Startup(TargetDescription target);
    RuntimeSources InterruptEntry(TargetDescription target);
    RuntimeSources Atomics(TargetDescription target);
}
