using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Narrowing;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.CBackend;

/// <summary>
/// The samples through the c code generator on the PC (target "host-c": the C compiled by clang with the windows-x64
/// runtime): each must print exactly what .NET prints, as through the llvm code generator. With narrowing on, too, as
/// an 8-bit chip's programs are: arithmetic in 8 and 16 bits, which C promotes to int.
/// </summary>
public sealed class CHostSampleTests : IDisposable
{
    private static readonly string ShippedTargets = Path.Combine(AppContext.BaseDirectory, "targets");
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [HostFact]
    [RunsLong]
    public async Task Every_sample_built_for_host_c_prints_exactly_what_dotnet_prints()
    {
        var output = Path.Combine(_scratch, "out");
        var failures = await Concurrently.SelectAsync(Samples.Names, async name =>
        {
            var dll = Samples.Compile(name, Path.Combine(_scratch, "dll"));
            BuildResult result;
            try
            {
                result = ChipSharpBuilder.Build(dll, new BuildOptions
                {
                    Target = "host-c",
                    TargetsFolder = ShippedTargets,
                    OutputFolder = Path.Combine(output, name),
                    KeepIntermediates = true,
                });
            }
            catch (ChipSharpException e)
            {
                return $"{name}: {e.Message}";
            }

            Assert.Equal("host-c", result.Target.Name);
            var run = await HostRun.RunAsync(result.OutputPath, [], _scratch);
            return run.ExitCode != 0 || !run.Output.SequenceEqual(Samples.ExpectedOutput(name)) ? $"{name}: {run}" : null;
        });

        Assert.True(failures.All(failure => failure is null), string.Join("\n\n", failures.OfType<string>()));
    }

    [HostFact]
    [RunsLong]
    public async Task Every_sample_with_its_arithmetic_narrowed_prints_through_C_what_dotnet_prints()
    {
        var target = NarrowingProgramTests.Narrowed(CTargets.HostC);
        var failures = await Concurrently.SelectAsync(Samples.Names, async name =>
        {
            var dll = Samples.Compile(name, Path.Combine(_scratch, "dll"));
            var module = IlToTir.Lower(WholeProgramPass.Analyze(IlAssembly.Load(dll), target));
            try
            {
                var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, name), target);
                return run.ExitCode != 0 || !run.Output.SequenceEqual(Samples.ExpectedOutput(name)) ? $"{name}: {run}" : null;
            }
            catch (ChipSharpException e)
            {
                return $"{name}: {e.Message}";
            }
        });

        Assert.True(failures.All(failure => failure is null), string.Join("\n\n", failures.OfType<string>()));
    }
}
