using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Heap;

/// <summary>
/// Arrays on the heap (see HeapArrays) on the PC: C# compiled the whole way, the whole-program pass for the target
/// included, in Release and Debug, and run; each prints what .NET prints. With a 16 KB heap the collector runs many times,
/// and every array that is still in use, however it is held, keeps its elements.
/// </summary>
public sealed class HeapArrayProgramTests(ITestOutputHelper output) : IDisposable
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

    [HostFact]
    public Task Arrays_returned_kept_null_compared_of_arrays_and_of_objects_print_what_dotnet_prints() =>
        PrintsWhatDotnetPrints(HeapArrayPrograms.Basics, "host");

    [HostFact]
    public Task Arrays_held_every_way_live_through_hundreds_of_collections_in_a_small_heap() =>
        PrintsWhatDotnetPrints(HeapArrayPrograms.Survivors(20_000), "host-16K");

    [HostFact]
    public Task Arrays_in_the_fields_of_objects_and_arrays_of_objects_live_through_collections_in_a_small_heap() =>
        PrintsWhatDotnetPrints(HeapArrayPrograms.Classes(3000), "host-16K");

    [HostFact]
    public async Task Null_a_negative_length_and_an_index_out_of_range_stop_the_program_with_dotnets_messages()
    {
        foreach (var what in HeapArrayPrograms.Failures)
        {
            var module = Module(HeapArrayPrograms.Failure(what), OptimizationLevel.Release, Targets.Load("host"));

            var run = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, what.Replace(' ', '-')), Targets.Load("host"));

            Assert.Equal((1, "1", HeapArrayPrograms.FailureMessage(what)), run.Outcome);
        }
    }

    [Fact]
    public void A_target_without_a_heap_builds_no_array_on_the_heap()
    {
        var pc = new TargetLoader(Build.TargetFiles.WithShipped(Path.Combine(_scratch, "no-heap"),
            ("pc-without-heap", """{ "inherits": "host", "capabilities": { "heap": false } }"""))).Load("pc-without-heap");

        var error = Assert.Throws<ChipSharpException>(() => Module(HeapArrayPrograms.Basics, OptimizationLevel.Release, pc));

        Assert.Contains("an array whose size is known only when the program runs needs a heap, and the target " +
            "'pc-without-heap' has no heap", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compiles <paramref name="source"/> (Release and Debug), runs it on .NET and as ChipSharp programs for the target
    /// <paramref name="target"/>, and compares.
    /// </summary>
    private async Task PrintsWhatDotnetPrints(string source, string target)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Program"), "Program",
            Path.Combine(_scratch, "dotnet"));
        Assert.NotEmpty(dotnet.Output);

        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var module = Module(source, optimization, Targets.Load(target));

            var run = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, optimization.ToString()), Targets.Load(target));
            output.WriteLine($"{optimization}, {target}: {run.Time.TotalMilliseconds:0} ms");

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Equal(dotnet.Text, run.Raw);
        }
    }

    /// <summary>The program in <paramref name="source"/>, checked for <paramref name="target"/> and translated.</summary>
    private static TirModule Module(string source, OptimizationLevel optimization, TargetDescription target)
    {
        var assembly = CSharpCompiler.Compile(source, "Program", OutputKind.ConsoleApplication, optimization);
        return Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll"), target));
    }
}
