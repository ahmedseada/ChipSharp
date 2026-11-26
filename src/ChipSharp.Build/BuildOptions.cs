using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Build;

/// <summary>How <see cref="ChipSharpBuilder"/> builds a program. Every setting is optional.</summary>
public sealed record BuildOptions
{
    /// <summary>The target a build is for when none is given: the PC ChipSharp runs on.</summary>
    public const string DefaultTarget = "host";

    /// <summary>The target to build for, e.g. "host" or "pico". Default: <see cref="DefaultTarget"/>.</summary>
    public string Target { get; init; } = DefaultTarget;

    /// <summary>
    /// The folder with the target files. When not set: the environment variable CHIPSHARP_TARGETS, then the
    /// "targets" folder next to the running program (chipsharp.exe ships them there).
    /// </summary>
    public string? TargetsFolder { get; init; }

    /// <summary>
    /// The folder the program (or the chip image) is written to. It is created when missing; files of the same name
    /// are replaced. When not set: chipsharp-output\&lt;target&gt; under the current folder.
    /// </summary>
    public string? OutputFolder { get; init; }

    /// <summary>
    /// The program's file name without extension, e.g. "Blink" for Blink.uf2. When not set: the assembly's name.
    /// </summary>
    public string? ProgramName { get; init; }

    /// <summary>
    /// Also write what the program went through into the output folder: its TIR (&lt;program&gt;.tir), the files the
    /// code generator wrote (e.g. the .ll) and, when the target packages an image, the linked program it was made from.
    /// </summary>
    public bool KeepIntermediates { get; init; }

    /// <summary>clang itself, for the toolchains that run it; see <see cref="ClangToolchainOptions.ClangPath"/>.</summary>
    public string? ClangPath { get; init; }

    /// <summary>The folder with LLVM's tools; see <see cref="ClangToolchainOptions.LlvmBinFolder"/>.</summary>
    public string? LlvmBinFolder { get; init; }
}
