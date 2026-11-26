using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Heap;

/// <summary>
/// Arrays on the heap (see HeapArrays) on the Raspberry Pi Pico, in the simulator: the programs of the PC tests
/// (<see cref="HeapArrayPrograms"/>), checked for the target and built the whole way, in Release and Debug, print over
/// UART0 what .NET prints; with a 16 KB heap the collector runs many times.
/// </summary>
public sealed class HeapArraySimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
    private TargetLoader? _targets;

    private TargetLoader Targets => _targets ??= HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K");

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [SimulatorFact]
    public Task Arrays_returned_kept_null_compared_of_arrays_and_of_objects_print_what_dotnet_prints() =>
        PrintsWhatDotnetPrints(HeapArrayPrograms.Basics, "pico");

    [SimulatorFact]
    public Task Arrays_held_every_way_live_through_many_collections_in_a_small_heap() =>
        PrintsWhatDotnetPrints(HeapArrayPrograms.Survivors(4000), "pico-16K");

    [SimulatorFact]
    public async Task Null_and_a_negative_length_stop_the_program_with_dotnets_messages_on_UART0()
    {
        foreach (var what in new[] { "element of null", "negative length" })
        {
            var assembly = CSharpCompiler.Compile(HeapArrayPrograms.Failure(what), "Program");
            var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll"), Targets.Load("pico")));

            var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, what.Replace(' ', '-')),
                Targets.Load("pico"), 1000);

            Assert.True(run.ExitCode == 3, run.ToString());
            Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
            Assert.Equal("1\r\n" + HeapArrayPrograms.FailureMessage(what), result.Raw);
        }
    }

    private async Task PrintsWhatDotnetPrints(string source, string target)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Program"), "Program",
            Path.Combine(_scratch, "dotnet"));

        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var assembly = CSharpCompiler.Compile(source, "Program", OutputKind.ConsoleApplication, optimization);
            var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll"), Targets.Load(target)));

            var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, optimization.ToString()),
                Targets.Load(target), 60_000);
            output.WriteLine($"{optimization}, {target}: {result.Time.TotalMilliseconds:0} ms simulated\n{run.Stopped}");

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Contains("stopped: the program has finished", run.Stopped);
            Assert.Equal(dotnet.Text, result.Raw);
        }
    }
}
