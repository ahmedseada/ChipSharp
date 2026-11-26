using System.Text;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The programs of <see cref="PrintedTextPrograms"/> built for the Pico, in Release and in Debug, and run in the
/// simulator: they print over UART0 what .NET prints. Printed text needs no heap: on a Pico without one it prints the
/// same.
/// </summary>
public sealed class PrintedTextSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> ProgramNames => [.. PrintedTextPrograms.All.Select(program => program.Name)];

    [SimulatorTheory]
    [MemberData(nameof(ProgramNames))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints_in_Release_and_Debug(string name)
    {
        var source = PrintedTextPrograms.All.Single(program => program.Name == name).Source;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name,
            Path.Combine(_scratch, "dotnet"));

        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var assembly = CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly], name,
                OutputKind.ConsoleApplication, optimization);
            var program = WholeProgramPass.Analyze(IlAssembly.Load(assembly, name + ".dll"), PicoBuild.Pico);
            var pico = PicoBuild.Build(IlToTir.Lower(program), Path.Combine(_scratch, optimization.ToString()));

            var run = await Rp2040Simulator.RunAsync(pico.Uf2, 10_000);

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Contains("stopped: the program has finished", run.Stopped);
            Assert.Equal(dotnet.Text, Encoding.ASCII.GetString(run.Uart0));
            Assert.Equal(dotnet.Output, run.Uart0);
        }
    }

    [SimulatorFact]
    public async Task Alignment_counts_text_that_is_not_ASCII_in_UTF16_code_units()
    {
        var program = PicoBuild.Build(PrintedTextPrograms.NotAscii, "NotAscii", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(Encoding.UTF8.GetBytes(PrintedTextPrograms.NotAsciiOutput), run.Uart0);
    }

    [SimulatorFact]
    public async Task On_a_Pico_without_a_heap_the_PrintedText_sample_prints_what_dotnet_prints()
    {
        var targets = TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""));
        var target = new TargetLoader(targets).Load("pico-no-heap");
        Assert.False(target.HasHeap);
        var source = File.ReadAllText(Path.Combine(Samples.Folder, "PrintedText.cs"));

        var program = PicoBuild.Build(source, "PrintedText", Path.Combine(_scratch, "pico"), target);
        var run = await Rp2040Simulator.RunAsync(program.Uf2, 10_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(Samples.ExpectedOutput("PrintedText"), run.Uart0);
    }
}
