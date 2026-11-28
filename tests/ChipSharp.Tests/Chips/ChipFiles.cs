using ChipSharp.Abstractions;
using ChipSharp.Chips;
using ChipSharp.Targets;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Chips;

/// <summary>
/// The chips' descriptions in the repository (chips/), the headers made from them (src/ChipSharp.Chips/Generated), and
/// the targets they belong to.
/// </summary>
internal static class ChipFiles
{
    public static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    /// <summary>The environment variable that makes the generated headers' test write them instead of comparing them.</summary>
    public const string RegenerateVariable = "CHIPSHARP_REGENERATE";

    /// <summary>
    /// The chips whose headers are generated and kept: the chip's target, built with SDCC (the PFS154, the STM8S103) or
    /// with clang (the ESP32-C5, whose description takes its registers from Espressif's SVD).
    /// </summary>
    public static IReadOnlyList<string> Generated { get; } = ["pfs154", "stm8s103f3", "esp32c5"];

    public static string Folder => Path.Combine(Repository.Root, ChipCatalog.FolderName);

    public static string GeneratedFolder => Path.Combine(Repository.Root, "src", "ChipSharp.Chips", ChipCatalog.GeneratedFolderName);

    public static string Svd => Path.Combine(Folder, "svd", "RP2040.svd");

    /// <summary>Espressif's SVD file of the ESP32-C5, which chips/esp32c5.json takes the registers from.</summary>
    public static string Esp32c5Svd => Path.Combine(Folder, "svd", "esp32c5.svd");

    /// <summary>The description of the chip target <paramref name="name"/>, as the catalog finds it.</summary>
    public static ChipDescription Read(string name) =>
        ChipCatalog.Read(ChipCatalog.FindFile(Targets.Load(name), Folder) ?? throw new InvalidOperationException(name));

    /// <summary>The headers of <paramref name="name"/>'s chip, as the tests make and keep them.</summary>
    public static IReadOnlyList<GeneratedFile> Headers(string name)
    {
        var target = Targets.Load(name);
        var file = ChipCatalog.FindFile(target, Folder)!;
        var source = $"{ChipCatalog.FolderName}/{Path.GetRelativePath(Folder, file).Replace('\\', '/')}";
        return ChipHeaders.All(ChipCatalog.Read(file), ChipCatalog.DialectOf(target), source);
    }
}
