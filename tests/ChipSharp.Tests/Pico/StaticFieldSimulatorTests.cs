using System.Text;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The static-field programs of <see cref="StaticFieldPrograms"/> built for the Pico and run in the simulator: they
/// print over UART0 what .NET prints. On the chip the globals live in RAM: the ones with a starting value are copied
/// there from flash at start-up (.data), the others start at zero (.bss).
/// </summary>
public sealed class StaticFieldSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string> ProgramNames => [.. StaticFieldPrograms.All.Select(program => program.Name)];

    [SimulatorTheory]
    [MemberData(nameof(ProgramNames))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name)
    {
        var source = StaticFieldPrograms.All.Single(program => program.Name == name).Source;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name,
            Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(source, name, Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 10_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.ASCII.GetString(run.Uart0));
    }
}
