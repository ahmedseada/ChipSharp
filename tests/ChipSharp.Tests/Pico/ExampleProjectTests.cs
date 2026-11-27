using System.Diagnostics;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// Every example project in examples/pico-*, built the way the README says (dotnet build of the project, then
/// <c>chipsharp build -t pico</c> of its .dll) and run for a while in the rp2040js simulator: it must not crash, and it
/// must blink the LED on GPIO 25, each example in its own way (a delay loop, a timer interrupt, a button).
/// </summary>
public sealed class ExampleProjectTests : IDisposable
{
    private const int Milliseconds = 1600;

    /// <summary>A button on GPIO 15, pressed three times, for the example that watches it.</summary>
    private static readonly string[] Button =
    [
        "--input", "15=low",
        "--input", "15=high@200ms", "--input", "15=low@400ms",
        "--input", "15=high@600ms", "--input", "15=low@800ms",
        "--input", "15=high@1000ms", "--input", "15=low@1200ms",
    ];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> Examples() => new(ExampleNames());

    [Fact]
    public void There_are_examples_for_the_pico()
    {
        Assert.Contains("pico-blink", ExampleNames());
    }

    [SimulatorTheory]
    [MemberData(nameof(Examples))]
    public async Task Every_example_project_builds_for_the_pico_and_blinks_in_the_simulator(string example)
    {
        var project = Directory.GetFiles(Path.Combine(Repository.Examples, example), "*.csproj").Single();
        var dll = await DotnetBuildAsync(project);

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "pico", "-o",
            Path.Combine(_scratch, "pico"));

        Assert.True(exitCode == 0, error);
        var lines = printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var uf2 = lines[0];
        Assert.Equal(Path.GetFileNameWithoutExtension(project) + ".uf2", Path.GetFileName(uf2));
        Assert.Matches(
            @"^flash \d+(\.\d)? KB of 2048 KB \(\d+\.\d%\), RAM \d+(\.\d)? (bytes?|KB) of 264 KB " +
            @"\(stack 8 KB reserved\)$",
            lines[1]);
        Assert.Equal(2, lines.Length);

        var run = await Rp2040Simulator.RunAsync(uf2, Milliseconds, Button);

        // still running when the time is up (each example loops forever), never stopped by a fault or a breakpoint
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.EndsWith($"stopped: {Milliseconds} ms of simulated time have passed", run.Stopped);
        var led = run.Pins.Where(p => p.Pin == 25 && p.State is "low" or "high").ToList();
        Assert.True(led.Count >= 3, run.ToString());
        Assert.Equal("low", led[0].State); // Pin.Output, as the program starts
        Assert.InRange(led[0].Milliseconds, 0, 5);
        for (var i = 1; i < led.Count; i++)
        {
            Assert.NotEqual(led[i - 1].State, led[i].State);
        }
    }

    /// <summary>The example folders for the Pico, e.g. "pico-blink".</summary>
    private static string[] ExampleNames() =>
    [
        .. Directory.GetDirectories(Repository.Examples, "pico-*").Select(folder => Path.GetFileName(folder)).Order(),
    ];

    /// <summary>
    /// Builds the project in Release as <c>dotnet build</c> does, with its bin and obj folders (and those of the
    /// projects it references) under the scratch folder rather than in the repository, and returns the program's .dll.
    /// </summary>
    private async Task<string> DotnetBuildAsync(string project)
    {
        var artifacts = Path.Combine(_scratch, "artifacts");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "build", project, "-c", "Release", "--artifacts-path", artifacts, "--disable-build-servers", "-nologo",
        })
        {
            start.ArgumentList.Add(argument);
        }

        // the test run's own MSBuild settings would point the build at the wrong SDK files
        var msbuild = start.Environment.Keys.Where(key => key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase));
        foreach (var name in msbuild.ToList())
        {
            start.Environment.Remove(name);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"dotnet build {project} failed:\n{await output}\n{await errors}");

        return Directory.GetFiles(Path.Combine(artifacts, "bin"), Path.GetFileNameWithoutExtension(project) + ".dll",
            SearchOption.AllDirectories).Single();
    }
}
