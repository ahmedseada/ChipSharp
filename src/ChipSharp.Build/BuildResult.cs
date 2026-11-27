using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;

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
/// <param name="Memory">How much of the target's flash and RAM the program takes (for a PC, how big it is).</param>
public sealed record BuildResult(string OutputPath, IReadOnlyList<string> IntermediatePaths, TargetDescription Target,
    MemoryReport Memory)
{
    /// <summary>
    /// The places whose result in .NET depends on the PC's language settings, where the program's is the same
    /// everywhere (see <see cref="AnalysisResult.Notes"/>): they do not stop a build. Empty when there are none.
    /// </summary>
    public IReadOnlyList<Note> Notes { get; init; } = [];

    /// <summary>The <see cref="Notes"/> by difference, as <c>chipsharp build</c> prints them.</summary>
    public IReadOnlyList<NoteGroup> NoteGroups { get; init; } = [];
}
