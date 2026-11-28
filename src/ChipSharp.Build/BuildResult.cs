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

    /// <summary>The target's extra images (<see cref="IImageFormat.Variants"/>) written beside <see cref="OutputPath"/>, with what each is for.</summary>
    public IReadOnlyList<(string Path, ImageVariant Variant)> Variants { get; init; } = [];

    /// <summary>The file <c>chipsharp flash</c> writes: the extra image that says so, else <see cref="OutputPath"/>.</summary>
    public string FlashPath => Variants.FirstOrDefault(v => v.Variant.Flashed).Path ?? OutputPath;

    /// <summary>One line that says which file is for what, when there is more than one image; else null.</summary>
    public string? ImagesLine => Variants.Count == 0
        ? null
        : string.Join("; ", Variants.Select(v => $"{System.IO.Path.GetFileName(v.Path)}: {v.Variant.Meaning}")
            .Prepend($"{System.IO.Path.GetFileName(OutputPath)}: {MainMeaning}"));

    /// <summary>What the main image is for, when there are extra ones.</summary>
    public string MainMeaning { get; init; } = "";

    /// <summary>The <see cref="Notes"/> by difference, as <c>chipsharp build</c> prints them.</summary>
    public IReadOnlyList<NoteGroup> NoteGroups { get; init; } = [];
}
