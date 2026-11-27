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
/// The methods of System.Array on the Raspberry Pi Pico, in the simulator: the programs of the PC tests
/// (<see cref="ArrayMethodPrograms"/>), checked for the target and built the whole way, in Release and Debug, print over
/// UART0 what .NET prints; where .NET throws, the program says what .NET says and stops at a breakpoint. A chip without
/// a heap has every method that makes no array.
/// </summary>
public sealed class ArrayMethodSimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
    private TargetLoader? _targets;

    private TargetLoader Targets => _targets ??= new TargetLoader(Build.TargetFiles.WithShipped(
        Path.Combine(_scratch, "targets"),
        ("pico-16K", """{ "inherits": "pico", "capabilities": { "heap": "16K" } }"""),
        ("pico-noheap", """{ "inherits": "pico", "capabilities": { "heap": false } }""")));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [SimulatorFact]
    public Task Copy_ConstrainedCopy_CopyTo_and_Clear_print_over_UART0_what_dotnet_prints() =>
        PrintsWhatDotnetPrints(ArrayMethodPrograms.Copies, "pico");

    [SimulatorFact]
    public Task Fill_IndexOf_LastIndexOf_and_Reverse_print_over_UART0_what_dotnet_prints() =>
        PrintsWhatDotnetPrints(ArrayMethodPrograms.Searches, "pico");

    [SimulatorFact]
    public Task Sort_and_BinarySearch_order_soft_floats_and_both_zeros_as_dotnet_does() =>
        PrintsWhatDotnetPrints(ArrayMethodPrograms.Sorting, "pico");

    [SimulatorFact]
    public Task Resize_and_Clone_keep_every_element_through_many_collections_in_a_small_heap() =>
        PrintsWhatDotnetPrints(ArrayMethodPrograms.NewArrays(600), "pico-16K");

    [SimulatorFact]
    public Task The_empty_arrays_and_clones_have_their_own_types() =>
        PrintsWhatDotnetPrints(ArrayMethodPrograms.EmptyArrays, "pico");

    [SimulatorFact]
    public async Task A_chip_without_a_heap_has_the_methods_that_make_no_array_and_says_which_need_one()
    {
        var target = Targets.Load("pico-noheap");
        Assert.False(target.HasHeap);
        await PrintsWhatDotnetPrints(ArrayMethodPrograms.WithoutHeap, "pico-noheap");

        var problems = WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile("""
            using System;

            class Program
            {
                static void Main()
                {
                    int[] values = { 1, 2 };
                    Array.Resize(ref values, 3);
                    int[] copy = (int[])values.Clone();
                    Console.WriteLine(copy.Length);
                }
            }
            """), "Test.dll"), target).Problems.Select(problem => problem.Line);
        Assert.Contains(problems, problem => problem.Contains("(call): resizing an array with Array.Resize needs a heap, "
            + "and the target 'pico-noheap' (264 KB RAM) has no heap.", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("(callvirt): copying an array into a new one with "
            + "array.Clone() needs a heap", StringComparison.Ordinal));
    }

    [SimulatorFact]
    public async Task Where_dotnet_throws_the_program_says_so_on_UART0_and_stops_at_a_breakpoint()
    {
        // a failure of each kind: the messages with the value in them, null, too short, a start, a count, a range
        string[] names =
        [
            "Copy range negative source index", "Copy null destination", "Copy range destination too short",
            "Copy long huge length", "CopyTo of null", "Clear past the end", "Fill range count past the end",
            "LastIndexOf start at the end", "Reverse past the end", "Sort negative length", "Resize negative size",
        ];
        foreach (var name in names)
        {
            var source = ArrayMethodPrograms.Failure(ArrayMethodPrograms.Failures.Single(failure => failure.Name == name).Statement);
            var dotnet = await ThrowingDotnet.RunAsync(CSharpCompiler.Compile(source, "Program"),
                Path.Combine(_scratch, name.Replace(' ', '-'), "dotnet"));
            var expected = string.Concat(dotnet.Errors.Split('\n')
                .TakeWhile(line => !line.StartsWith("   at ", StringComparison.Ordinal)).Select(line => line + "\n"));
            foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
            {
                var assembly = CSharpCompiler.Compile(source, "Program", OutputKind.ConsoleApplication, optimization);
                var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll"), Targets.Load("pico")));

                var (result, run, _) = await HeapRun.OnPicoAsync(module,
                    Path.Combine(_scratch, name.Replace(' ', '-'), optimization.ToString()), Targets.Load("pico"), 1000);

                Assert.True(run.ExitCode == 3, $"{name}: {run}");
                Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
                Assert.Equal("1\r\n" + expected, result.Raw);
            }
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
                Targets.Load(target), 120_000);
            output.WriteLine($"{optimization}, {target}: {result.Time.TotalMilliseconds:0} ms simulated\n{run.Stopped}");

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Contains("stopped: the program has finished", run.Stopped);
            Assert.Equal(dotnet.Text, result.Raw);
        }
    }
}
