using System.Diagnostics;

namespace ChipSharp.Tests.Support;

/// <summary>Runs the real chipsharp command (built next to the tests, chipsharp.dll) with dotnet.</summary>
public static class ChipSharpCli
{
    /// <summary>A path as messages write it in a chipsharp command line: in quotes when it has a space.</summary>
    public static string Quoted(string path) => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;

    /// <summary>Runs chipsharp with <paramref name="arguments"/>; returns its exit code and what it wrote to standard output and error.</summary>
    public static Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments) =>
        RunInAsync(Environment.CurrentDirectory, arguments);

    /// <summary>Runs chipsharp in the folder <paramref name="workingFolder"/>, like <see cref="RunAsync"/>.</summary>
    public static Task<(int ExitCode, string Output, string Error)> RunInAsync(string workingFolder,
        params string[] arguments) =>
        RunWithAsync(workingFolder, new Dictionary<string, string>(), arguments);

    /// <summary>
    /// Runs chipsharp in the folder <paramref name="workingFolder"/> with these environment variables set (e.g.
    /// CHIPSHARP_AVRDUDE), like <see cref="RunAsync"/>.
    /// </summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunWithAsync(string workingFolder,
        IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "chipsharp.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }
}
