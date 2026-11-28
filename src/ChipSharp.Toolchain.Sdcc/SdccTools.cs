using ChipSharp.Abstractions;

namespace ChipSharp.Toolchain.Sdcc;

/// <summary>
/// Finds SDCC: the SdccPath option, else the environment variable CHIPSHARP_SDCC (either must hold it: sdcc.exe, or
/// the folder it is in), else SDCC's install folder (Program Files\SDCC\bin, where its Windows installer puts it), else
/// sdcc on the PATH. The assembler of a port (sdasstm8, sdaspdk14) is the one next to sdcc.
/// </summary>
internal sealed class SdccTools(SdccToolchainOptions options, Func<string, string?> environment)
{
    private static readonly string ExeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";

    /// <summary>Where SDCC's Windows installer puts it: Program Files\SDCC\bin; null without a Program Files folder.</summary>
    public static string? InstallFolder => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is
        { Length: > 0 } programFiles
        ? Path.Combine(programFiles, "SDCC", "bin")
        : null;

    public string Sdcc()
    {
        var file = "sdcc" + ExeSuffix;
        var (path, setBy) = !string.IsNullOrWhiteSpace(options.SdccPath)
            ? (options.SdccPath.Trim().Trim('"'), "the SdccPath option")
            : Variable(SdccToolchain.SdccVariable) is { } variable
                ? (variable, $"the environment variable {SdccToolchain.SdccVariable}")
                : (null, null);
        if (path is not null)
        {
            // a folder is taken to mean the sdcc in it
            var fullPath = Path.GetFullPath(Directory.Exists(path) ? Path.Combine(path, file) : path);
            return File.Exists(fullPath)
                ? fullPath
                : throw new ChipSharpException($"SDCC was not found at '{fullPath}' (set by {setBy}). {Advice}");
        }

        var folders = new List<string>();
        if (InstallFolder is { } install)
        {
            folders.Add(install);
        }

        var searchPath = (environment("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => folder.Trim('"'));
        foreach (var folder in folders.Concat(searchPath))
        {
            var candidate = Path.Combine(folder, file);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new ChipSharpException(
            $"SDCC was not found (looked in {string.Join(", ", folders.Select(f => $"'{f}'"))} and on the PATH). {Advice}");
    }

    /// <summary>The assembler of <paramref name="port"/> next to <paramref name="sdcc"/>, e.g. sdasstm8 for stm8.</summary>
    public static string Assembler(string sdcc, string port)
    {
        var path = Path.Combine(Path.GetDirectoryName(sdcc)!, $"sdas{port}{ExeSuffix}");
        return File.Exists(path)
            ? path
            : throw new ChipSharpException($"SDCC's assembler for the port {port} is not next to sdcc: '{path}' is " +
                "missing. Install SDCC again, with that port.");
    }

    /// <summary>SDCC's archiver next to <paramref name="sdcc"/>: sdar, which makes the libraries its linker reads.</summary>
    public static string Archiver(string sdcc)
    {
        var path = Path.Combine(Path.GetDirectoryName(sdcc)!, $"sdar{ExeSuffix}");
        return File.Exists(path)
            ? path
            : throw new ChipSharpException($"SDCC's archiver is not next to sdcc: '{path}' is missing. Install SDCC " +
                "again.");
    }

    /// <summary>What to do when SDCC is missing.</summary>
    private static string Advice =>
        "The Padauk and STM8 chips (e.g. the targets pfs154-bare and stm8s103-board) are built through C with SDCC. " +
        "Install SDCC 4.6 or later (https://sdcc.sourceforge.net/; its Windows installer puts it in " +
        $"Program Files\\SDCC) and set {SdccToolchain.SdccVariable} to its sdcc{ExeSuffix} or its bin folder, or put " +
        "that folder on the PATH.";

    private string? Variable(string name) =>
        environment(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim().Trim('"') : null;
}
