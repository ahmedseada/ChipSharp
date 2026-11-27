using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Strings;

/// <summary>
/// The programs of <see cref="StringPrograms"/> built for the Pico (4-byte pointers: a literal is 8 bytes and its chars,
/// in flash) and run in the simulator, in Release and in Debug: they print over UART0, byte for byte, what .NET prints
/// with a UTF-8 console; in a 16 KB heap the strings they keep live through many collections.
/// </summary>
public sealed class StringSimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => StringPrograms.InReleaseAndDebug();

    [SimulatorTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name, bool debug)
    {
        var source = StringPrograms.Source(name);
        var dotnet = await HostRun.RunOnDotnetAsync(StringPrograms.ForDotnet(source, name), name,
            Path.Combine(_scratch, "dotnet"));
        var assembly = StringPrograms.Compile(source, name, debug, [PicoBuild.HardwareAssembly]);
        var pico = PicoBuild.Build(IlToTir.Lower(WholeProgramPass.Analyze(assembly, PicoBuild.Pico)),
            Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 30_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Output, run.Uart0);
    }

    [SimulatorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Strings_kept_everywhere_live_through_many_collections(bool debug)
    {
        var pico = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("pico-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(StringPrograms.ForDotnet(StringPrograms.Garbage, "Garbage"),
            "Garbage", Path.Combine(_scratch, "dotnet"));
        var assembly = StringPrograms.Compile(StringPrograms.Garbage, "Garbage", debug, [PicoBuild.HardwareAssembly]);
        var module = IlToTir.Lower(WholeProgramPass.Analyze(assembly, pico));

        var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, "pico"), pico, 120_000);
        output.WriteLine($"Pico, 16 KB heap: {result.Time.TotalMilliseconds:0} ms simulated\n{result}\n{run.Stopped}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    /// <summary>A failure says what .NET says over UART0, then the program stops at a breakpoint.</summary>
    [SimulatorFact]
    public async Task What_dotnet_throws_for_stops_the_program_with_dotnets_message_over_UART0()
    {
        var program = PicoBuild.Build("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine("go");
                    Console.WriteLine(int.Parse(" 12x"));
                }
            }
            """, "Parse", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.Equal("go\r\nUnhandled exception. System.FormatException: The input string ' 12x' was not in a correct " +
            "format.\r\n", System.Text.Encoding.UTF8.GetString(run.Uart0));
    }
}
