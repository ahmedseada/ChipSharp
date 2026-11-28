using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Library;

/// <summary>
/// Programs that use DateTime, DateOnly and TimeOnly, compiled by ChipSharp for the PC (with ChipSharp.Library's) and
/// run next to .NET with the invariant culture, in Release and Debug: every member and every format prints what .NET
/// prints, printed straight away, written into a span and kept in a string; and where .NET throws, the program stops
/// with .NET's message and exit code 1.
/// </summary>
public sealed class DateTimeProgramTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    /// <summary>Dates whose text the tests compare: the edges (but the last two days, which AddDays(1.25) passes) and random ones.</summary>
    public static IEnumerable<long> TextTicks(int random) =>
        DateTimeValues.EdgeTicks.Where(t => t < DateTime.MaxValue.Ticks - 2 * TimeSpan.TicksPerDay)
            .Concat(DateTimeValues.RandomTicks(random, seed: 31).Where(t => t < DateTime.MaxValue.Ticks - 2 * TimeSpan.TicksPerDay));

    [HostFact]
    [RunsLong]
    public async Task Every_member_prints_what_dotnet_prints_for_the_edges_and_random_dates()
    {
        await CompareAsync(DateTimePrograms.Members(random: 200), "Members", minimumLines: 9_000);
    }

    [HostFact]
    [RunsLong]
    public async Task Every_format_printed_written_into_a_span_and_kept_is_the_invariant_cultures_text()
    {
        await CompareAsync(DateTimePrograms.Text(TextTicks(random: 12), kept: true), "Text", minimumLines: 5_000);
    }

    [HostFact]
    [RunsLong]
    public async Task Where_dotnet_throws_the_program_stops_with_dotnets_message_and_exit_code_1()
    {
        var index = 0;
        foreach (var (statement, dotnet) in DateTimePrograms.Failures)
        {
            var source = DateTimePrograms.Failing(statement);
            var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(CSharpCompiler.Compile(source, "Failing"),
                "Failing.dll")));

            var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, $"case{index++}"));

            Assert.Equal("1\r\n", run.Text);
            Assert.Equal(1, run.ExitCode);
            Assert.Equal(TimeSpanPrograms.Unhandled(dotnet), run.Errors);
        }
    }

    private async Task CompareAsync(string source, string name, int minimumLines)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        Assert.True(dotnet.Text.Split('\n').Length > minimumLines, "The program prints too little to test much.");

        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var assembly = CSharpCompiler.Compile(source, name, OutputKind.ConsoleApplication, optimization);
            var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, name + ".dll")));

            var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, optimization.ToString()));

            Assert.Equal(0, chipSharp.ExitCode);
            Assert.Equal(dotnet.Text, chipSharp.Text);
            Assert.Equal(dotnet.Output, chipSharp.Output);
        }
    }
}
