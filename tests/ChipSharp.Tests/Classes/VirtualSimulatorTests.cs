using System.Text;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Classes;

/// <summary>
/// The programs of <see cref="VirtualPrograms"/>, built for the Pico (4-byte pointers: a virtual method table's slot is
/// 4 bytes, the table starts 40 bytes into the descriptor) and run in the simulator, compiled in Release and in Debug:
/// they print over UART0 what .NET prints; the zoo goes on through many collections in a 16 KB heap; a cast that fails
/// says so on UART0 and stops.
/// </summary>
public sealed class VirtualSimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => VirtualPrograms.InReleaseAndDebug();

    [SimulatorTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name, bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(VirtualPrograms.Source(name), name), name,
            Path.Combine(_scratch, "dotnet"));
        var assembly = VirtualPrograms.Compile(name, debug, [PicoBuild.HardwareAssembly]);
        var pico = PicoBuild.Build(IlToTir.Lower(WholeProgramPass.Analyze(assembly, PicoBuild.Pico)),
            Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 20_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.ASCII.GetString(run.Uart0));
    }

    /// <summary>The zoo's 20000 animals, some 300 KB on the Pico, in a 16 KB heap: only collections make room.</summary>
    [SimulatorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Virtual_calls_go_on_through_dozens_of_collections_in_a_small_heap(bool debug)
    {
        var pico = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("pico-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(VirtualPrograms.Zoo, "Zoo"), "Zoo",
            Path.Combine(_scratch, "dotnet"));
        var assembly = VirtualPrograms.Compile("Zoo", debug, [PicoBuild.HardwareAssembly]);
        var module = IlToTir.Lower(WholeProgramPass.Analyze(assembly, pico));

        var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, "pico"), pico, 60_000);
        output.WriteLine($"Pico, 16 KB heap: {result.Time.TotalMilliseconds:0} ms simulated\n{result}\n{run.Stopped}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), result.Raw);
    }

    [SimulatorFact]
    public async Task A_cast_that_fails_says_so_on_UART0_and_stops()
    {
        var program = PicoBuild.Build("""
            using System;

            interface IShape
            {
            }

            class Program
            {
                class Circle
                {
                }

                static void Main()
                {
                    object circle = new Circle();
                    Console.WriteLine(circle is IShape);
                    var shape = (IShape)circle;
                    Console.WriteLine(shape == null);
                }
            }
            """, "Casts", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
        Assert.Equal("False\r\nUnhandled exception. System.InvalidCastException: Unable to cast object of type 'Circle' " +
            "to type 'IShape'.\r\n", Encoding.ASCII.GetString(run.Uart0));
    }
}
