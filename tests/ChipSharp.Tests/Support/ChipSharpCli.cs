using System.Diagnostics;

namespace ChipSharp.Tests.Support;

/// <summary>Runs the real chipsharp command (built next to the tests, chipsharp.dll) with dotnet.</summary>
public static class ChipSharpCli
{
    /// <summary>Runs chipsharp with <paramref name="arguments"/>; returns its exit code and what it wrote to standard output and error.</summary>
    public static Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments) =>
        RunInAsync(Environment.CurrentDirectory, arguments);

    /// <summary>Runs chipsharp in the folder <paramref name="workingFolder"/>, like <see cref="RunAsync"/>.</summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunInAsync(string workingFolder,
        params string[] arguments)
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

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }
}
