using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The array programs of the PC tests, built for the Pico and run in the rp2040js simulator: each prints over UART0
/// what .NET prints, and an index outside an array says so on UART0 and stops the program at a breakpoint.
/// </summary>
public sealed class ArraySimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string> ProgramNames =>
        ["ElementTypes", "Initializers", "Callees", "Loops", "Indexes", "BigArrays", "StructsAndReferences"];

    [SimulatorTheory]
    [MemberData(nameof(ProgramNames))]
    public async Task An_array_program_prints_over_UART0_what_dotnet_prints(string name)
    {
        var source = name switch
        {
            "ElementTypes" => ArrayPrograms.ElementTypes,
            "Initializers" => ArrayPrograms.Initializers,
            "Callees" => ArrayPrograms.Callees,
            "Loops" => ArrayPrograms.Loops,
            "Indexes" => ArrayPrograms.Indexes,
            "StructsAndReferences" => ArrayPrograms.StructsAndReferences,
            _ => ArrayPrograms.BigArrays,
        };
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(source, name, Path.Combine(_scratch, name));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 60_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text, System.Text.Encoding.ASCII.GetString(run.Uart0));
        Assert.Equal(dotnet.Output, run.Uart0);
    }

    [SimulatorFact]
    public async Task An_index_outside_the_array_says_so_on_UART0_and_stops_at_a_breakpoint()
    {
        var index = 0;
        foreach (var (source, printed) in ArrayPrograms.OutOfRange)
        {
            var program = PicoBuild.Build(source, "OutOfRange", Path.Combine(_scratch, $"case{index++}"));

            var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

            Assert.True(run.ExitCode == 3, run.ToString());
            Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
            Assert.Equal(printed + ArrayPrograms.IndexOutOfRangeMessage, System.Text.Encoding.ASCII.GetString(run.Uart0));
        }
    }
}
