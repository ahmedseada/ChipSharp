using System.Text;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Collections;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Tuples;

/// <summary>
/// The programs of <see cref="TuplePrograms"/> built for the Pico (4-byte pointers: a tuple of an int and a string takes
/// 8 bytes, a span of strings views 4-byte references) and run in the simulator, compiled in Release and in Debug: they
/// print over UART0 what .NET prints; the tuples that need no heap run on a Pico without one, the garbage program's
/// tuples and collection expressions live through many collections in a 16 KB heap, and a failure says .NET's words.
/// </summary>
public sealed class TupleSimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => TuplePrograms.InReleaseAndDebug();

    [SimulatorTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name, bool debug)
    {
        var source = TuplePrograms.Source(name);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        var assembly = TuplePrograms.Compile(source, name, debug, [PicoBuild.HardwareAssembly]);
        var pico = PicoBuild.Build(IlToTir.Lower(WholeProgramPass.Analyze(assembly, PicoBuild.Pico)), Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 60_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.UTF8.GetString(run.Uart0));
    }

    /// <summary>The tuples of <see cref="TuplePrograms.Basics"/> on a Pico whose target file says it has no heap.</summary>
    [SimulatorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tuples_need_no_heap_on_a_Pico_without_one(bool debug)
    {
        var noHeap = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(TuplePrograms.Basics, "Basics"), "Basics",
            Path.Combine(_scratch, "dotnet"));
        var assembly = TuplePrograms.Compile(TuplePrograms.Basics, "Basics", debug, [PicoBuild.HardwareAssembly]);
        var module = IlToTir.Lower(WholeProgramPass.Analyze(assembly, noHeap));

        var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, "pico"), noHeap, 60_000);

        Assert.False(module.UsesHeap);
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), result.Raw);
    }

    /// <summary>The garbage program's tuples, lists and arrays in a 16 KB heap: only collections make room.</summary>
    [SimulatorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tuples_and_collection_expressions_keep_what_they_hold_through_many_collections(bool debug)
    {
        var pico = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("pico-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(TuplePrograms.Garbage, "Garbage"), "Garbage",
            Path.Combine(_scratch, "dotnet"));
        var assembly = TuplePrograms.Compile(TuplePrograms.Garbage, "Garbage", debug, [PicoBuild.HardwareAssembly]);
        var module = IlToTir.Lower(WholeProgramPass.Analyze(assembly, pico));

        var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, "pico"), pico, 120_000);
        output.WriteLine($"Pico, 16 KB heap: {result.Time.TotalMilliseconds:0} ms simulated\n{result}\n{run.Stopped}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), result.Raw);
    }

    /// <summary>The default comparer of a class that is not comparable says .NET's words over UART0, then stops.</summary>
    [SimulatorTheory]
    [InlineData("ComparerOfAClassThatIsNotComparable")]
    [InlineData("SortOfAClassThatIsNotComparable")]
    public async Task A_failure_says_what_dotnet_says_on_UART0_and_stops(string name)
    {
        var source = TuplePrograms.FailureSource(name);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name,
            Path.Combine(_scratch, "dotnet"), failing: true);
        var assembly = TuplePrograms.Compile(source, name, debug: false, [PicoBuild.HardwareAssembly]);
        var pico = PicoBuild.Build(IlToTir.Lower(WholeProgramPass.Analyze(assembly, PicoBuild.Pico)), Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 10_000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n") + CollectionProgramTests.WithoutStackTrace(dotnet.Errors),
            Encoding.UTF8.GetString(run.Uart0));
    }
}
