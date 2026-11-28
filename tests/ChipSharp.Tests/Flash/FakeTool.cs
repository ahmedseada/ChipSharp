namespace ChipSharp.Tests.Flash;

/// <summary>
/// A stand-in for a programmer's tool (avrdude, easypdkprog, stm8flash), for the tests of the flashers that run one (no
/// test may run a real one: it would write whatever board or chip is plugged in): a batch file named
/// <paramref name="name"/>.cmd that writes the arguments it was given to a file next to it, prints
/// <paramref name="said"/> to standard error, as the tools print, and ends with <paramref name="exitCode"/>.
/// </summary>
internal sealed class FakeTool
{
    public FakeTool(string folder, string name, int exitCode = 0, params string[] said)
    {
        Directory.CreateDirectory(folder);
        Name = name;
        Path = System.IO.Path.Combine(folder, $"{name}.cmd");
        Arguments = System.IO.Path.Combine(folder, "arguments.txt");
        var lines = new List<string> { "@echo off", $">\"%~dp0arguments.txt\" echo %*" };
        lines.AddRange(said.Select(line => $"echo {line} 1>&2"));
        lines.Add($"exit /b {exitCode}");
        File.WriteAllText(Path, string.Join("\r\n", lines) + "\r\n");
    }

    /// <summary>The tool's name, e.g. "avrdude".</summary>
    public string Name { get; }

    /// <summary>The batch file, to run as the tool.</summary>
    public string Path { get; }

    /// <summary>The file the arguments of the last run are written to (as cmd's %* has them: quoted where they were).</summary>
    public string Arguments { get; }

    /// <summary>The command line of the last run, after the tool's name; null when it did not run.</summary>
    public string? CommandLine => File.Exists(Arguments) ? File.ReadAllText(Arguments).TrimEnd() : null;
}
