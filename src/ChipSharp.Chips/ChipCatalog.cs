using ChipSharp.Abstractions;

namespace ChipSharp.Chips;

/// <summary>
/// Where the chips' descriptions are: the repository's <see cref="FolderName"/> folder, one file per chip, ChipSharp's
/// own JSON (<c>pfs154.json</c>, <c>stm8s103f3.json</c>, named as the chip's target file) or a vendor's SVD (in
/// <c>svd</c>, which the chip's target file names with <c>"svd"</c>: rp2040.json's <c>"svd/RP2040.svd"</c>), or both: a
/// JSON description that takes its registers from a vendor's SVD file and adds the rest (<c>esp32c5.json</c>'s
/// <c>"svd": "svd/esp32c5.svd"</c>, see <see cref="ChipJson"/>).
/// </summary>
public static class ChipCatalog
{
    /// <summary>The folder of the descriptions, at the repository's root.</summary>
    public const string FolderName = "chips";

    /// <summary>The headers made from the descriptions, which the runtimes embed: src/ChipSharp.Chips/Generated.</summary>
    public const string GeneratedFolderName = "Generated";

    /// <summary>
    /// The description of <paramref name="target"/>'s chip in <paramref name="chipsFolder"/>: the file its "svd" names,
    /// else the JSON file named as a target of its inheritance chain (the board's, then the chip's, ...); null when there
    /// is none.
    /// </summary>
    public static string? FindFile(TargetDescription target, string chipsFolder)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(chipsFolder);
        if (target.Setting("svd") is { } svd)
        {
            var path = Path.Combine(chipsFolder, svd);
            return File.Exists(path) ? path : null;
        }

        return target.InheritanceChain.Select(name => Path.Combine(chipsFolder, name + ".json")).FirstOrDefault(File.Exists);
    }

    /// <summary>Reads a description: SVD for a .svd file, ChipSharp's JSON for any other.</summary>
    public static ChipDescription Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.EndsWith(".svd", StringComparison.OrdinalIgnoreCase) ? Svd.Read(path) : ChipJson.Read(path);
    }

    /// <summary>The C a target's runtime is compiled with: SDCC's for the toolchain "sdcc", else clang's and GCC's.</summary>
    public static CDialect DialectOf(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Toolchain == "sdcc" ? CDialect.Sdcc : CDialect.Gnu;
    }
}
