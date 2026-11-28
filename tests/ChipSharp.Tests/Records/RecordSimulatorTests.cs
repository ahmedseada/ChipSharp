using System.Text;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Records;

/// <summary>
/// The programs of <see cref="RecordPrograms"/> built for the Pico (4-byte pointers: a type object takes 24 bytes of
/// flash, a StringBuilder 16 bytes of the heap and its buffer 2 bytes per char) and run in the simulator, compiled in
/// Release and in Debug: they print over UART0 what .NET prints; the garbage program's records and builders live
/// through many collections in a 16 KB heap.
/// </summary>
public sealed class RecordSimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => RecordPrograms.InReleaseAndDebug();

    [SimulatorTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name, bool debug)
    {
        var source = RecordPrograms.Source(name);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        var assembly = RecordPrograms.Compile(source, name, debug, [PicoBuild.HardwareAssembly]);
        var pico = PicoBuild.Build(IlToTir.Lower(WholeProgramPass.Analyze(assembly, PicoBuild.Pico)),
            Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 60_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.UTF8.GetString(run.Uart0));
    }

    [SimulatorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Records_and_builders_kept_in_arrays_and_fields_live_through_many_collections(bool debug)
    {
        var pico = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("pico-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(RecordPrograms.Garbage, "Garbage"), "Garbage",
            Path.Combine(_scratch, "dotnet"));
        var assembly = RecordPrograms.Compile(RecordPrograms.Garbage, "Garbage", debug, [PicoBuild.HardwareAssembly]);
        var module = IlToTir.Lower(WholeProgramPass.Analyze(assembly, pico));

        var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, "pico"), pico, 120_000);
        output.WriteLine($"Pico, 16 KB heap: {result.Time.TotalMilliseconds:0} ms simulated\n{result}\n{run.Stopped}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), result.Raw);
    }

    /// <summary>A StringBuilder given an index outside it says what .NET says over UART0, then the program stops at a breakpoint.</summary>
    [SimulatorFact]
    public async Task A_builder_given_a_bad_argument_stops_the_program_with_dotnets_message_over_UART0()
    {
        var program = PicoBuild.Build("""
            using System;
            using System.Text;

            class Program
            {
                static void Main()
                {
                    var text = new StringBuilder("go");
                    Console.WriteLine(text);
                    text.Append('!', -2);
                }
            }
            """, "Repeat", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.Equal("go\r\nUnhandled exception. System.ArgumentOutOfRangeException: repeatCount ('-2') must be a " +
            "non-negative value. (Parameter 'repeatCount')\r\nActual value was -2.\r\n", Encoding.UTF8.GetString(run.Uart0));
    }
}
