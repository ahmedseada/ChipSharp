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
/// The programs of <see cref="ClassPrograms"/>, built for the Pico (4-byte pointers: an object of two fields takes 12
/// bytes, 16 in the heap) and run in the simulator, compiled in Release and in Debug: they print over UART0 what .NET
/// prints; in a 16 KB heap the survivors live through many collections; an interrupt handler uses objects made before.
/// </summary>
public sealed class ClassSimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => ClassPrograms.InReleaseAndDebug();

    [SimulatorTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name, bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(ClassPrograms.Source(name), name), name,
            Path.Combine(_scratch, "dotnet"));
        var assembly = ClassPrograms.Compile(name, debug, [PicoBuild.HardwareAssembly]);
        var pico = PicoBuild.Build(IlToTir.Lower(WholeProgramPass.Analyze(assembly, PicoBuild.Pico)),
            Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 20_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.ASCII.GetString(run.Uart0));
    }

    /// <summary>Some 1.7 MB of objects in a 16 KB heap: only collections make room, and they must keep every survivor.</summary>
    [SimulatorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Objects_in_every_place_a_reference_lives_survive_a_hundred_collections(bool debug)
    {
        var pico = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("pico-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(ClassPrograms.Survivors, "Survivors"),
            "Survivors", Path.Combine(_scratch, "dotnet"));
        var assembly = ClassPrograms.Compile("Survivors", debug, [PicoBuild.HardwareAssembly]);
        var module = IlToTir.Lower(WholeProgramPass.Analyze(assembly, pico));

        var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, "pico"), pico, 60_000);
        output.WriteLine($"Pico, 16 KB heap: {result.Time.TotalMilliseconds:0} ms simulated\n{result}\n{run.Stopped}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), result.Raw);
    }

    [SimulatorFact]
    public async Task An_interrupt_handler_walks_objects_that_Main_made_before()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            class Step
            {
                public int Pin;
                public Step Next;

                public Step(int pin, Step next)
                {
                    Pin = pin;
                    Next = next;
                }
            }

            class Program
            {
                static volatile Step first;
                static volatile int ticks;

                static void Main()
                {
                    var last = new Step(4, null);
                    first = new Step(2, new Step(3, last));
                    last.Next = first;
                    Pin.Output(2);
                    Pin.Output(3);
                    Pin.Output(4);
                    Timing.EveryMilliseconds(10);
                    while (ticks < 6)
                    {
                    }

                    Timing.Stop();
                    Console.WriteLine(ticks);
                    Console.WriteLine(At(ticks).Pin);
                }

                // the step the ring is at after so many ticks: read from Main's objects, never stored (the collector
                // runs with interrupts on, so a handler stores no object)
                static Step At(int count)
                {
                    Step step = first;
                    for (int i = 0; i < count % 3; i++)
                    {
                        step = step.Next;
                    }

                    return step;
                }

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    Pin.Toggle(At(ticks).Pin);
                    ticks++;
                }
            }
            """, "Steps", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("6\r\n2\r\n", Encoding.ASCII.GetString(run.Uart0));
        // Pin.Output drives each pin low; then the handler toggles them round the ring, twice
        var changes = run.Pins.Where(p => p.Pin is 2 or 3 or 4 && p.State is "low" or "high")
            .Select(p => $"{p.Pin} {p.State}").Skip(3).ToList();
        Assert.Equal(["2 high", "3 high", "4 high", "2 low", "3 low", "4 low"], changes);
    }
}
