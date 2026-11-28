using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;

namespace ChipSharp.Flasher.Shared;

/// <summary>
/// A programmer's own command-line tool, which a flasher runs (avrdude, easypdkprog, stm8flash): finding it (an
/// environment variable, the PATH, the folders it is installed in), running it, and what a message shows of it (its
/// command line, its last lines).
/// </summary>
public static partial class ProgrammerTool
{
    /// <summary>How many of a tool's last lines a failure's message shows.</summary>
    public const int ShownLines = 12;

    /// <summary>The tool's program file: avrdude.exe on Windows, avrdude elsewhere.</summary>
    public static string FileName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <summary>
    /// The tool that the environment variable <paramref name="variable"/> names: the file, or the folder it is in (in
    /// quotes or not); null when the variable is not set. Set, it must hold the tool, else an error that says where it
    /// looked, then <paramref name="advice"/>.
    /// </summary>
    public static string? FromVariable(string tool, string variable, Func<string, string?> environment, string advice)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment(variable) is not { } value || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var set = value.Trim().Trim('"');
        var fullPath = Path.GetFullPath(Directory.Exists(set) ? Path.Combine(set, FileName(tool)) : set);
        return File.Exists(fullPath)
            ? fullPath
            : throw new ChipSharpException(
                $"{tool} was not found at '{fullPath}' (set by the environment variable {variable}). {advice}");
    }

    /// <summary>The tool in the first folder of the PATH that has it; null when none has.</summary>
    public static string? OnPath(string tool, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return InFolders(tool, (environment("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => folder.Trim('"')));
    }

    /// <summary>The tool in the first of <paramref name="folders"/> that has it; null when none has.</summary>
    public static string? InFolders(string tool, IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        return folders.Select(folder => Path.Combine(folder, FileName(tool))).Where(File.Exists)
            .Select(Path.GetFullPath).FirstOrDefault();
    }

    /// <summary>
    /// The folders in <paramref name="folder"/>, one for each version of a tool (6.3.0-arduino17, 7.1-arduino.1,
    /// 2019.02.05), the newest first, by their numbers in turn; none when the folder is not there.
    /// </summary>
    public static IEnumerable<string> Versions(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetDirectories(folder).OrderByDescending(Path.GetFileName, VersionComparer.Instance)
            : [];

    /// <summary>The arguments as a command line shows them: those with a space in quotes.</summary>
    public static string CommandLine(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(argument => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument));

    /// <summary>True when the tool's output has one of <paramref name="texts"/>, in any case.</summary>
    public static bool Says(string output, params string[] texts) =>
        texts.Any(text => output.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What a failure's message shows of the tool's output: "\n&lt;tool&gt; said:" and its last <see cref="ShownLines"/>
    /// lines, indented; or " &lt;tool&gt; printed nothing.".
    /// </summary>
    public static string Said(string tool, string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0).ToList();
        var shown = lines.Skip(Math.Max(0, lines.Count - ShownLines)).ToList();
        return shown.Count == 0 ? $" {tool} printed nothing." : $"\n{tool} said:\n  {string.Join("\n  ", shown)}";
    }

    /// <summary>
    /// Runs the tool <paramref name="tool"/> (its file <paramref name="path"/>) with <paramref name="arguments"/>; returns
    /// its exit code and what it printed, standard output and error as they came. Cancelled, it is stopped.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string tool, string path,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var output = new List<string>();
        void Add(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.Add(e.Data);
                }
            }
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += Add;
        process.ErrorDataReceived += Add;
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new ChipSharpException($"{tool} ('{path}') could not be started: {e.Message}", e);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        lock (output)
        {
            return (process.ExitCode, string.Join('\n', output));
        }
    }

    /// <summary>Orders versions such as 6.3.0-arduino17 and 7.1-arduino.1 by their numbers, in turn.</summary>
    private sealed partial class VersionComparer : IComparer<string?>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Numbers(x ?? "");
            var b = Numbers(y ?? "");
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                if (a[i] != b[i])
                {
                    return a[i].CompareTo(b[i]);
                }
            }

            return a.Count != b.Count ? a.Count.CompareTo(b.Count) : string.CompareOrdinal(x, y);
        }

        private static List<long> Numbers(string version) =>
            [.. Digits().Matches(version).Select(match => long.Parse(match.Value, CultureInfo.InvariantCulture))];

        [GeneratedRegex(@"\d{1,18}")]
        private static partial Regex Digits();
    }
}
