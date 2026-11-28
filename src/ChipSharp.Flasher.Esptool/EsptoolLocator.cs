using System.ComponentModel;
using System.Diagnostics;
using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;

namespace ChipSharp.Flasher.Esptool;

/// <summary>esptool as a command to run: the program, the arguments that come first (<c>-m esptool</c> for Python's module, the script for an esptool.py), and how it is called in messages.</summary>
public sealed record EsptoolTool(string Path, IReadOnlyList<string> Prefix, string Display)
{
    public bool Equals(EsptoolTool? other) =>
        other is not null && Path == other.Path && Display == other.Display && Prefix.SequenceEqual(other.Prefix);

    public override int GetHashCode() => HashCode.Combine(Path, Display, Prefix.Count);
}

/// <summary>
/// Finds esptool: the environment variable CHIPSHARP_ESPTOOL (esptool, esptool.exe or esptool.py, or the folder that
/// has one), else <c>esptool</c> (esptool.exe), then <c>esptool.py</c>, on the PATH, else Python's module
/// (<c>py -m esptool</c>, <c>python -m esptool</c>, <c>python3 -m esptool</c>) when that Python can import it.
/// </summary>
internal sealed class EsptoolLocator(Func<string, string?> environment, Func<string, bool> canImportEsptool,
    IReadOnlyList<string> toolsFolders)
{
    /// <summary>What to do when esptool is missing.</summary>
    internal static string Advice =>
        "esptool is Espressif's flashing tool: install it with Python (py -m pip install esptool; https://docs.espressif.com/" +
        $"projects/esptool), or unpack a release, and set {EsptoolFlasher.EsptoolVariable} to its " +
        $"{ProgrammerTool.FileName("esptool")}, or put its folder on the PATH.";

    private static readonly string[] Pythons = OperatingSystem.IsWindows() ? ["py", "python"] : ["python3", "python"];

    public EsptoolTool Find()
    {
        if (environment(EsptoolFlasher.EsptoolVariable) is { } value && !string.IsNullOrWhiteSpace(value))
        {
            return FromVariable(value.Trim().Trim('"'));
        }

        if ((ProgrammerTool.OnPath("esptool", environment) ?? OnPathAsIs("esptool.py")) is { } found)
        {
            return Tool(found);
        }

        if (ProgrammerTool.InFolders("esptool", toolsFolders) is { } installed)
        {
            return Tool(installed);
        }

        foreach (var python in Pythons)
        {
            if (ProgrammerTool.OnPath(python, environment) is { } path && canImportEsptool(path))
            {
                return new EsptoolTool(path, ["-m", "esptool"], $"{python} -m esptool");
            }
        }

        var folders = toolsFolders.Count == 0 ? "" : $" and in {string.Join(", ", toolsFolders.Select(f => $"'{f}'"))}";
        throw new ChipSharpException($"esptool was not found (looked on the PATH{folders}, and for a Python that has it). {Advice}");
    }

    private EsptoolTool FromVariable(string set)
    {
        var path = System.IO.Path.GetFullPath(set);
        if (Directory.Exists(path))
        {
            var inside = new[] { ProgrammerTool.FileName("esptool"), "esptool.py" }
                .Select(file => System.IO.Path.Combine(path, file)).FirstOrDefault(File.Exists);
            path = inside ?? System.IO.Path.Combine(path, ProgrammerTool.FileName("esptool"));
        }

        return File.Exists(path)
            ? Tool(path)
            : throw new ChipSharpException(
                $"esptool was not found at '{path}' (set by the environment variable {EsptoolFlasher.EsptoolVariable}). {Advice}");
    }

    /// <summary>A script (esptool.py) runs through Python; a program runs as it is.</summary>
    private EsptoolTool Tool(string path)
    {
        if (!path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            return new EsptoolTool(path, [], System.IO.Path.GetFileName(path));
        }

        var python = Pythons.Select(name => ProgrammerTool.OnPath(name, environment)).FirstOrDefault(p => p is not null)
            ?? throw new ChipSharpException(
                $"'{path}' is a Python script, and no Python was found on the PATH to run it. {Advice}");
        return new EsptoolTool(python, [path], System.IO.Path.GetFileName(path));
    }

    private string? OnPathAsIs(string file) =>
        (environment("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => System.IO.Path.Combine(folder.Trim('"'), file)).Where(File.Exists).Select(System.IO.Path.GetFullPath)
            .FirstOrDefault();

    /// <summary>True when <paramref name="python"/> imports esptool (<c>python -c "import esptool"</c> ends with 0).</summary>
    public static bool Imports(string python)
    {
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import esptool");
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
