namespace ChipSharp.Toolchain.Sdcc;

/// <summary>How <see cref="SdccToolchain"/> builds. Every setting is optional.</summary>
public sealed record SdccToolchainOptions
{
    /// <summary>
    /// The folder a build writes into: the generated files, what SDCC makes of each (.rel, .asm, .lst), the linked
    /// program (.ihx) and its map (.map). It is created when missing; files of the same name are replaced. When not
    /// set, every build gets a new folder under the system temp folder (the program's path tells which).
    /// </summary>
    public string? OutputFolder { get; init; }

    /// <summary>
    /// The program's file name without extension, e.g. "Blink" for Blink.ihx. When not set, the name of the first
    /// generated .c file, else "program". SDCC takes no spaces in file names: each becomes '_'.
    /// </summary>
    public string? ProgramName { get; init; }

    /// <summary>
    /// SDCC itself (sdcc.exe), or the folder it is in. When not set: the environment variable CHIPSHARP_SDCC (the same,
    /// either), then SDCC's install folder (Program Files\SDCC\bin), then the PATH.
    /// </summary>
    public string? SdccPath { get; init; }

    /// <summary>More sdcc arguments, after the target's "sdcc.flags", e.g. "-DDEBUG".</summary>
    public IReadOnlyList<string> ExtraFlags { get; init; } = [];

    /// <summary>
    /// Keep what SDCC compiles (each file's .rel) in the build cache and take it from there next time (the default),
    /// keyed by SDCC itself (where it is, its size and date), the port and flags, the file's name and text and the
    /// headers it can include. False compiles everything every time (<c>chipsharp build --no-cache</c>).
    /// </summary>
    public bool UseCache { get; init; } = true;

    /// <summary>
    /// The build cache's folder. When not set: the environment variable CHIPSHARP_CACHE, then
    /// %LOCALAPPDATA%\ChipSharp\cache, the clang toolchain's. Deleting it is always safe.
    /// </summary>
    public string? CacheFolder { get; init; }
}
