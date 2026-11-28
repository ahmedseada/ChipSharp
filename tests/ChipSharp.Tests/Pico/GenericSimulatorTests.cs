using System.Text;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The generic programs of <see cref="GenericPrograms"/>, checked for a Pico without a heap (generics over values need
/// none), built for the Pico and run in the simulator, compiled in Release and in Debug: they print over UART0 what .NET
/// prints. On the Cortex-M0+ a long is aligned to 8 and nint is 4 bytes, so the instances' layouts and nint's limits
/// differ from the PC's.
/// </summary>
public sealed class GenericSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => GenericPrograms.InReleaseAndDebug();

    [SimulatorTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name, bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(GenericPrograms.Source(name), name), name,
            Path.Combine(_scratch, "dotnet"));
        var assembly = GenericPrograms.Compile(name, debug, [PicoBuild.HardwareAssembly]);
        var program = WholeProgramPass.Check(assembly, GenericPrograms.PicoWithoutHeap(_scratch)).ToProgram();
        var pico = PicoBuild.Build(IlToTir.Lower(program), Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 10_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.ASCII.GetString(run.Uart0));
    }
}
