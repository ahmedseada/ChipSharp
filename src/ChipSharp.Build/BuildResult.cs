using ChipSharp.Abstractions;

namespace ChipSharp.Build;

/// <summary>What a build wrote.</summary>
/// <param name="OutputPath">
/// The file to run or flash, in the output folder: the linked program (e.g. Square.exe), or the image when the target
/// packages one (e.g. Blink.uf2).
/// </param>
/// <param name="IntermediatePaths">
/// The files kept with <see cref="BuildOptions.KeepIntermediates"/>, in the order they were written; empty without it.
/// </param>
/// <param name="Target">The target the program was built for, with everything it inherits merged in.</param>
public sealed record BuildResult(string OutputPath, IReadOnlyList<string> IntermediatePaths, TargetDescription Target);
