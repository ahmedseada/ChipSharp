namespace ChipSharp.Toolchain.Clang;

/// <summary>How <see cref="ClangToolchain"/> builds. Every setting is optional.</summary>
public sealed record ClangToolchainOptions
{
    /// <summary>
    /// The folder a build writes into: the generated files, the import libraries (PC) or the linker script and map
    /// (chip), and the program.
    /// It is created when missing; files of the same name are replaced. When not set, every build gets a new
    /// folder under the system temp folder (the program's path tells which).
    /// </summary>
    public string? OutputFolder { get; init; }

    /// <summary>
    /// The program's file name without extension, e.g. "Square" for Square.exe or "Blink" for Blink.elf.
    /// When not set, the name of the first generated .ll file, else "program".
    /// </summary>
    public string? ProgramName { get; init; }

    /// <summary>
    /// clang itself. When not set: the <see cref="LlvmBinFolder"/>, then the environment variables
    /// CHIPSHARP_CLANG and CHIPSHARP_LLVM_BIN, then LLVM's install folder (Program Files\LLVM\bin), then the PATH.
    /// </summary>
    public string? ClangPath { get; init; }

    /// <summary>
    /// The folder with LLVM's tools (clang, llvm-dlltool). When not set: the environment variable
    /// CHIPSHARP_LLVM_BIN, then the folder clang is in, then Program Files\LLVM\bin, then the PATH.
    /// </summary>
    public string? LlvmBinFolder { get; init; }

    /// <summary>More clang arguments, after the target's "clang.flags", e.g. "-g" for debug information.</summary>
    public IReadOnlyList<string> ExtraFlags { get; init; } = [];

    /// <summary>
    /// Keep what clang and llvm-dlltool make in the build cache and take it from there next time (the default): each
    /// source file is compiled once for a target, its flags and its text, so a build compiles the runtime's C once and
    /// later builds only link it. False compiles everything every time (<c>chipsharp build --no-cache</c>).
    /// </summary>
    public bool UseCache { get; init; } = true;

    /// <summary>
    /// The build cache's folder. When not set: the environment variable CHIPSHARP_CACHE, then
    /// %LOCALAPPDATA%\ChipSharp\cache. Deleting it is always safe.
    /// </summary>
    public string? CacheFolder { get; init; }

    /// <summary>
    /// The flag of <c>chipsharp build --verify-heap</c>, for <see cref="ExtraFlags"/>: the runtime's heap
    /// (chipsharp_heap.c) checks the whole heap after every collection, and stops the program saying what is wrong.
    /// </summary>
    public const string VerifyHeapFlag = "-DCHIPSHARP_VERIFY_HEAP";
}
