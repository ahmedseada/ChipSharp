using System.Text;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The struct, enum and reference programs of <see cref="StructPrograms"/> built for the Pico and run in the simulator:
/// they print over UART0 what .NET prints. On the Cortex-M0+ a pointer is 4 bytes and a long is aligned to 8, so the
/// layouts differ from the PC's, and struct copies go through the runtime's memcpy.
/// </summary>
public sealed class StructSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> ProgramNames => [.. StructPrograms.All.Select(program => program.Name)];

    [SimulatorTheory]
    [MemberData(nameof(ProgramNames))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name)
    {
        var source = StructPrograms.All.Single(program => program.Name == name).Source;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name,
            Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(source, name, Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 10_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.ASCII.GetString(run.Uart0));
    }
}
