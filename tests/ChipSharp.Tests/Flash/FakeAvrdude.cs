namespace ChipSharp.Tests.Flash;

/// <summary>
/// A stand-in for avrdude, for the tests of the flasher "avrdude" (no test may run a real one: it would reset and
/// write whatever board is on the port): a batch file that writes the arguments it was given to a file next to it,
/// prints <paramref name="said"/> to standard error, as avrdude prints, and ends with <paramref name="exitCode"/>.
/// </summary>
internal sealed class FakeAvrdude
{
    public FakeAvrdude(string folder, int exitCode = 0, params string[] said)
    {
        Directory.CreateDirectory(folder);
        Path = System.IO.Path.Combine(folder, "avrdude.cmd");
        Arguments = System.IO.Path.Combine(folder, "arguments.txt");
        var lines = new List<string> { "@echo off", $">\"%~dp0arguments.txt\" echo %*" };
        lines.AddRange(said.Select(line => $"echo {line} 1>&2"));
        lines.Add($"exit /b {exitCode}");
        File.WriteAllText(Path, string.Join("\r\n", lines) + "\r\n");
    }

    /// <summary>The batch file, to run as avrdude.</summary>
    public string Path { get; }

    /// <summary>The file the arguments of the last run are written to (as cmd's %* has them: quoted where they were).</summary>
    public string Arguments { get; }

    /// <summary>The command line of the last run, after "avrdude"; null when it did not run.</summary>
    public string? CommandLine => File.Exists(Arguments) ? File.ReadAllText(Arguments).TrimEnd() : null;
}
