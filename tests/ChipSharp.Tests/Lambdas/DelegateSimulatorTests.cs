using System.Text;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Lambdas;

/// <summary>
/// The programs of <see cref="DelegatePrograms"/>, built for the Pico (4-byte pointers: a delegate is 16 bytes of the
/// heap) and run in the simulator, compiled in Release and in Debug: they print over UART0 what .NET prints; the churn goes
/// on through many collections in a 16 KB heap; a timer interrupt invokes a delegate kept in a static field.
/// </summary>
public sealed class DelegateSimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => DelegatePrograms.InReleaseAndDebug();

    [SimulatorTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name, bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(DelegatePrograms.Source(name), name), name,
            Path.Combine(_scratch, "dotnet"));
        var assembly = DelegatePrograms.Compile(name, debug, [PicoBuild.HardwareAssembly]);
        var pico = PicoBuild.Build(IlToTir.Lower(WholeProgramPass.Analyze(assembly, PicoBuild.Pico)),
            Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 60_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.UTF8.GetString(run.Uart0));
    }

    /// <summary>The churn's closures, some 600 KB on the Pico, in a 16 KB heap: only collections make room.</summary>
    [SimulatorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closures_go_on_through_many_collections_in_a_small_heap(bool debug)
    {
        var pico = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("pico-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(DelegatePrograms.Churn, "Churn"), "Churn",
            Path.Combine(_scratch, "dotnet"));
        var assembly = DelegatePrograms.Compile("Churn", debug, [PicoBuild.HardwareAssembly]);
        var module = IlToTir.Lower(WholeProgramPass.Analyze(assembly, pico));

        var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, "pico"), pico, 120_000);
        output.WriteLine($"Pico, 16 KB heap: {result.Time.TotalMilliseconds:0} ms simulated\n{result}\n{run.Stopped}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), result.Raw);
    }

    /// <summary>
    /// On a Pico without a heap, local functions that C# calls directly keep the variables they capture in a struct of
    /// the method that declares them, passed by reference: no object, so they run as on .NET, in a struct's method too.
    /// </summary>
    [SimulatorFact]
    public async Task Local_functions_capture_variables_without_a_heap()
    {
        var target = new ChipSharp.Targets.TargetLoader(ChipSharp.Tests.Build.TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");
        var source = """
            using System;

            struct Meter
            {
                public int Scale;

                public int Read(int raw)
                {
                    var offset = 3;
                    var scale = Scale;
                    int Scaled(int x) => x * scale + offset;
                    offset = 4;
                    return Scaled(raw) + Scaled(1);
                }
            }

            static class Program
            {
                static void Main()
                {
                    var total = 0;
                    var step = 2;
                    void Add(int x) => total += x * step;
                    int Twice(int x) => Add2(x) + Add2(x);
                    int Add2(int x) => x + step;
                    for (var i = 0; i < 5; i++)
                    {
                        Add(i);
                        step++;
                    }

                    Console.WriteLine(total);
                    Console.WriteLine(Twice(10));
                    Console.WriteLine(new Meter { Scale = 5 }.Read(2));
                }
            }
            """;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Locals"), "Locals",
            Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(source, "Locals", Path.Combine(_scratch, "pico"), target);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.ASCII.GetString(run.Uart0));
    }

    /// <summary>
    /// A timer interrupt invokes the delegate Main keeps in a static field (a lambda that captures a counter, then a
    /// multicast one), which makes no object: the interrupt counts and Main prints what it counted.
    /// </summary>
    [SimulatorFact]
    public async Task An_interrupt_handler_invokes_a_delegate_kept_in_a_static_field()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            class Ticks
            {
                public volatile int Count;
            }

            partial class Program
            {
                static volatile Action onTick;
                static volatile int done;

                static void Main()
                {
                    var ticks = new Ticks();
                    var extra = new Ticks();
                    onTick = () => ticks.Count++;
                    Timing.EveryMilliseconds(1);
                    while (ticks.Count < 5)
                    {
                    }

                    onTick += () => extra.Count += 10;
                    while (extra.Count < 50)
                    {
                    }

                    Timing.Stop();
                    Console.WriteLine(ticks.Count >= 10);
                    Console.WriteLine(extra.Count >= 50);
                }

                [Interrupt(Event.Timer)]
                static void Tick() => onTick?.Invoke();
            }
            """, "Ticks", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 2000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("True\r\nTrue\r\n", Encoding.ASCII.GetString(run.Uart0));
    }
}
