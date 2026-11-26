namespace ChipSharp.Abstractions;

/// <summary>One file a code generator or runtime wrote, e.g. "program.ll" (LLVM IR) or "startup.S".</summary>
public sealed record GeneratedFile(string FileName, string Content);

/// <summary>What a code generator produced for one program.</summary>
public sealed record GeneratedCode(IReadOnlyList<GeneratedFile> Files);

/// <summary>A linked program ready to package, e.g. an .elf file.</summary>
public sealed record LinkedBinary(string Path);

/// <summary>Source files an architecture runtime contributes to the build.</summary>
public sealed record RuntimeSources(IReadOnlyList<GeneratedFile> Files);

/// <summary>Where and how to flash, e.g. a serial port.</summary>
public sealed record FlashOptions(string? Port = null);

/// <summary>Progress of a flash: what it is doing and how far (0 to 1).</summary>
public sealed record FlashProgress(string Stage, double Fraction);
