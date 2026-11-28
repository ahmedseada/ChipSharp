using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Programs with spans (<see cref="SpanPrograms"/>), compiled by ChipSharp for the PC (Release and Debug IL) and run:
/// each prints what .NET prints, and where .NET throws, the program stops with .NET's message. The same programs run on
/// the simulated Pico too (Pico/SpanSimulatorTests).
/// </summary>
public sealed class SpanProgramTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string> FailureNames => [.. SpanPrograms.Failures.Select(failure => failure.Name)];

    [HostFact]
    public Task Spans_of_arrays_stackalloc_and_constant_data_are_indexed_sliced_and_summed() =>
        PrintsWhatDotnetPrints(SpanPrograms.Views);

    [HostFact]
    public Task Searching_copying_filling_and_reading_through_a_ref_struct_give_what_dotnet_gives() =>
        PrintsWhatDotnetPrints(SpanPrograms.Copies);

    [HostFact]
    public Task BinaryPrimitives_and_MemoryMarshal_read_and_write_bytes_at_any_offset_like_dotnet() =>
        PrintsWhatDotnetPrints(SpanPrograms.Bytes);

    [HostTheory]
    [MemberData(nameof(FailureNames))]
    public async Task Where_dotnet_throws_the_program_stops_with_its_message_and_exit_code_1(string name)
    {
        var (_, source, printed, error) = SpanPrograms.Failures.Single(failure => failure.Name == name);
        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var assembly = CSharpCompiler.Compile(source, "Program", OutputKind.ConsoleApplication, optimization);
            var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll")));

            var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, $"{name}-{optimization}"));

            Assert.Equal(printed, run.Text);
            Assert.Equal(1, run.ExitCode);
            Assert.Equal(error, run.Errors);
        }

        // and .NET prints the same before it throws, with that exception's first line
        var dotnet = await HostRun.RunAsync(Dotnet.Path, [await Dotnet.WriteAsync(CSharpCompiler.Compile(source, name), name,
            Path.Combine(_scratch, "dotnet"))], Path.Combine(_scratch, "dotnet"));
        Assert.Equal(printed, dotnet.Text);
        Assert.StartsWith(error.TrimEnd(), dotnet.Errors, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compiles <paramref name="source"/> (Release and Debug), runs it on .NET and as ChipSharp programs, and compares.
    /// The whole-program pass checks it first.
    /// </summary>
    private async Task PrintsWhatDotnetPrints(string source)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Program"), "Program",
            Path.Combine(_scratch, "dotnet"));
        Assert.NotEmpty(dotnet.Output);

        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var assembly = CSharpCompiler.Compile(source, "Program", OutputKind.ConsoleApplication, optimization);
            var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll")));

            var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, optimization.ToString()));

            Assert.Equal(dotnet.Text, chipSharp.Text);
            Assert.Equal(dotnet.Output, chipSharp.Output);
            Assert.Equal(0, chipSharp.ExitCode);
        }
    }

    /// <summary>.NET for a program that throws, which <see cref="HostRun.RunOnDotnetAsync"/> does not take.</summary>
    private static class Dotnet
    {
        public static string Path
        {
            get
            {
                var framework = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
                var beside = System.IO.Path.GetFullPath(System.IO.Path.Combine(framework, "..", "..", "..", "dotnet.exe"));
                return File.Exists(beside) ? beside : "dotnet";
            }
        }

        /// <summary>Writes the program and its runtimeconfig.json into <paramref name="folder"/>; returns the .dll's path.</summary>
        public static async Task<string> WriteAsync(byte[] assembly, string name, string folder)
        {
            Directory.CreateDirectory(folder);
            var dll = System.IO.Path.Combine(folder, name + ".dll");
            await File.WriteAllBytesAsync(dll, assembly);
            var (major, minor) = (Environment.Version.Major, Environment.Version.Minor);
            await File.WriteAllTextAsync(System.IO.Path.Combine(folder, name + ".runtimeconfig.json"), $$"""
                {
                  "runtimeOptions": {
                    "tfm": "net{{major}}.{{minor}}",
                    "framework": { "name": "Microsoft.NETCore.App", "version": "{{major}}.{{minor}}.0" },
                    "configProperties": { "System.Globalization.Invariant": true }
                  }
                }
                """);
            return dll;
        }
    }
}
