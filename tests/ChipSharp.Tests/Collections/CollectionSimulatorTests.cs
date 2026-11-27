using System.Text;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Collections;

/// <summary>
/// The programs of <see cref="CollectionPrograms"/>, built for the Pico (4-byte pointers, so the entries of a dictionary
/// and the arrays of a list are laid out otherwise than on the PC) and run in the simulator, compiled in Release and in
/// Debug: they print over UART0 what .NET prints; the garbage program goes on through many collections in a 16 KB heap,
/// and a failure says .NET's words over UART0, then stops at a breakpoint.
/// </summary>
public sealed class CollectionSimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => CollectionPrograms.InReleaseAndDebug();

    [SimulatorTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name, bool debug)
    {
        var source = CollectionPrograms.Source(name);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        var assembly = CollectionPrograms.Compile(source, name, debug, [PicoBuild.HardwareAssembly]);
        var pico = PicoBuild.Build(IlToTir.Lower(WholeProgramPass.Analyze(assembly, PicoBuild.Pico)), Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 60_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.UTF8.GetString(run.Uart0));
    }

    /// <summary>The garbage program's thousands of collections, in a 16 KB heap: only collections make room.</summary>
    [SimulatorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collections_keep_what_they_hold_through_many_collections_in_a_small_heap(bool debug)
    {
        var pico = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("pico-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(CollectionPrograms.Garbage, "Garbage"),
            "Garbage", Path.Combine(_scratch, "dotnet"));
        var assembly = CollectionPrograms.Compile(CollectionPrograms.Garbage, "Garbage", debug, [PicoBuild.HardwareAssembly]);
        var module = IlToTir.Lower(WholeProgramPass.Analyze(assembly, pico));

        var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, "pico"), pico, 60_000);
        output.WriteLine($"Pico, 16 KB heap: {result.Time.TotalMilliseconds:0} ms simulated\n{result}\n{run.Stopped}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), result.Raw);
    }

    /// <summary>A failure of a collection, and a throw, say what .NET says over UART0; the program stops at a breakpoint.</summary>
    [SimulatorTheory]
    [InlineData("MissingKey")]
    [InlineData("ListModified")]
    [InlineData("StackModified")]
    [InlineData("ThrownOwn")]
    [InlineData("ThrownInner")]
    public async Task A_failure_says_what_dotnet_says_on_UART0_and_stops(string name)
    {
        var source = CollectionPrograms.FailureSource(name);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name,
            Path.Combine(_scratch, "dotnet"), failing: true);
        var assembly = CollectionPrograms.Compile(source, name, debug: false, [PicoBuild.HardwareAssembly]);
        var pico = PicoBuild.Build(IlToTir.Lower(WholeProgramPass.Analyze(assembly, PicoBuild.Pico)), Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 10_000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
        Assert.Equal("1\r\n" + CollectionProgramTests.WithoutStackTrace(dotnet.Errors), Encoding.UTF8.GetString(run.Uart0));
    }
}
