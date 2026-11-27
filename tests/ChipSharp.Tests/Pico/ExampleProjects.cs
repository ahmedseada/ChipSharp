using System.Diagnostics;

namespace ChipSharp.Tests.Pico;

/// <summary>The example projects in examples/, built with <c>dotnet build</c> as their README and comments say.</summary>
internal static class ExampleProjects
{
    /// <summary>The project file of the example in examples/<paramref name="example"/>, e.g. "pico-blink".</summary>
    public static string Project(string example) =>
        Directory.GetFiles(Path.Combine(Repository.Examples, example), "*.csproj").Single();

    /// <summary>
    /// Builds <paramref name="project"/> in Release as <c>dotnet build</c> does, with its bin and obj folders (and those
    /// of the projects it references) under <paramref name="scratch"/> rather than in the repository, and returns the
    /// program's .dll.
    /// </summary>
    public static async Task<string> DotnetBuildAsync(string project, string scratch)
    {
        var artifacts = Path.Combine(scratch, "artifacts");
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
