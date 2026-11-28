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
    /// <summary>The file extension of the image, without the dot; the format's name unless it says another (esp: "bin").</summary>
    string Extension => Name;

    void Package(LinkedBinary binary, TargetDescription target, Stream output);

    /// <summary>
    /// Further images of the same program, each written beside the main one as <c>&lt;program&gt;.&lt;suffix&gt;.&lt;extension&gt;</c>
    /// (none for most formats). The ESP image has one: the ROM-direct image <c>chipsharp flash</c> writes.
    /// </summary>
    IReadOnlyList<ImageVariant> Variants => [];

    /// <summary>What the main image is for, when the format has <see cref="Variants"/>.</summary>
    string MainMeaning => "";

    /// <summary>Packages the variant with <paramref name="suffix"/> (one of <see cref="Variants"/>).</summary>
    void PackageVariant(string suffix, LinkedBinary binary, TargetDescription target, Stream output) =>
        throw new NotSupportedException($"The {Name} image has no variant '{suffix}'.");
}

/// <summary>An extra image of an <see cref="IImageFormat"/>: its file name suffix, what it is for, and whether <c>chipsharp flash</c> writes it.</summary>
public sealed record ImageVariant(string Suffix, string Meaning, bool Flashed);

/// <summary>
/// Puts an image onto a real board, e.g. "uf2-copy", "avrdude", "esptool", "wch-link". The target is the board's,
/// with the flasher's settings for it (avrdude's programmer and speed, the UF2 drive's name); the image is the kind
/// its "image" names (a .hex, a .uf2).
/// </summary>
public interface IFlasher : INamedComponent
{
    Task FlashAsync(string imagePath, TargetDescription target, FlashOptions options, IProgress<FlashProgress>? progress,
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
