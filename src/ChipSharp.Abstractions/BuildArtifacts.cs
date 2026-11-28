namespace ChipSharp.Abstractions;

/// <summary>One file a code generator or runtime wrote, e.g. "program.ll" (LLVM IR) or "startup.S".</summary>
public sealed record GeneratedFile(string FileName, string Content);

/// <summary>What a code generator produced for one program.</summary>
public sealed record GeneratedCode(IReadOnlyList<GeneratedFile> Files);

/// <summary>A linked program ready to package, e.g. an .elf file.</summary>
public sealed record LinkedBinary(string Path);

/// <summary>Source files an architecture runtime contributes to the build.</summary>
public sealed record RuntimeSources(IReadOnlyList<GeneratedFile> Files);

/// <summary>Where and how to flash.</summary>
/// <param name="Port">
/// Where the board is, as its flasher names it: a serial port for avrdude (COM5), the drive (or folder) of a UF2 boot
/// loader for uf2-copy (E:\), the Easy PDK programmer's serial port for easypdkprog (COM7), the ST-LINK's serial number
/// for stm8flash (when more than one is plugged in). Null: the flasher finds it, and says which it found, or why it
/// cannot.
/// </param>
public sealed record FlashOptions(string? Port = null);

/// <summary>Progress of a flash: what it is doing and how far (0 to 1).</summary>
public sealed record FlashProgress(string Stage, double Fraction);
