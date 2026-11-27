using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Library;

/// <summary>
/// Programs that use TimeSpan, compiled by ChipSharp for the PC (with ChipSharp.Library's TimeSpan) and run next to
/// .NET: every member prints what .NET prints, and where .NET throws the program stops with .NET's message and exit
/// code 1.
/// </summary>
public sealed class TimeSpanProgramTests : IDisposable
{
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
    public async Task Every_member_prints_what_dotnet_prints_for_the_edges_and_random_values()
    {
        var source = TimeSpanPrograms.Members(random: 60);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Members"), "Members",
            Path.Combine(_scratch, "dotnet"));
        Assert.True(dotnet.Text.Split('\n').Length > 5_000, "The program prints too little to test much.");

        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var assembly = CSharpCompiler.Compile(source, "Members", OutputKind.ConsoleApplication, optimization);
            var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Members.dll")));

            var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, optimization.ToString()));

            Assert.Equal(0, chipSharp.ExitCode);
            Assert.Equal(dotnet.Text, chipSharp.Text);
            Assert.Equal(dotnet.Output, chipSharp.Output);
        }
    }

    [HostFact]
    [RunsLong]
    public async Task Where_dotnet_throws_the_program_stops_with_dotnets_message_and_exit_code_1()
    {
        var index = 0;
        foreach (var (statement, dotnet) in TimeSpanPrograms.Failures)
        {
            var source = TimeSpanPrograms.Failing(statement);
            var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(CSharpCompiler.Compile(source, "Failing"),
                "Failing.dll")));

            var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, $"case{index++}"));

            Assert.Equal("1\r\n", run.Text);
            Assert.Equal(1, run.ExitCode);
            Assert.Equal(TimeSpanPrograms.Unhandled(dotnet), run.Errors);
        }
    }
}
