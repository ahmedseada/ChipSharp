using ChipSharp.Tests.Library;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// TimeSpan on the Pico, in the rp2040js simulator: <c>Timing.Delay(TimeSpan)</c> waits as long as the TimeSpan says,
/// in any unit and beyond an int of microseconds, and ChipSharp.Library's TimeSpan prints over UART0 what .NET prints,
/// or stops where .NET throws.
/// </summary>
public sealed class TimeSpanSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [SimulatorFact]
    public async Task Delay_of_TimeSpan_FromMilliseconds_500_blinks_the_LED_every_500_ms()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            Pin.Output(25);
            while (true)
            {
                Pin.Toggle(25);
                Timing.Delay(TimeSpan.FromMilliseconds(500));
            }
            """, "TimeSpanBlink", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 2600);

        Assert.True(run.ExitCode == 0, run.ToString());
        var toggles = run.Pins.Where(p => p.Pin == 25 && p.State is "low" or "high").Skip(1).ToList();
        Assert.Equal(["high", "low", "high", "low", "high", "low"], toggles.Select(t => t.State));
        for (var i = 1; i < toggles.Count; i++)
        {
            Assert.InRange(toggles[i].Milliseconds - toggles[i - 1].Milliseconds, 499.9, 500.1);
        }
    }

    [SimulatorFact]
    public async Task Delay_waits_any_unit_returns_at_once_for_zero_or_less_and_waits_longer_than_an_int_of_microseconds()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Pin.Output(2);
                    Pin.Write(2, true);
                    Timing.Delay(TimeSpan.FromMicroseconds(250));
                    Pin.Write(2, false);
                    Timing.Delay(TimeSpan.Zero);
                    Pin.Write(2, true);
                    Timing.Delay(TimeSpan.FromSeconds(-1));
                    Pin.Write(2, false);
                    Timing.Delay(TimeSpan.FromSeconds(1.5));
                    Pin.Write(2, true);
                    Timing.Delay(new TimeSpan(0, 40, 0));
                    Pin.Write(2, false);
                }
            }
            """, "Delays", _scratch);

        // 40 minutes are 2 400 000 000 microseconds, more than int.MaxValue; the simulator skips the time the core sleeps
        var run = await Rp2040Simulator.RunAsync(program.Uf2, 2_402_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("the program has finished", run.Stopped);
        var levels = run.Pins.Where(p => p.Pin == 2 && p.State is "low" or "high").Select(p => p.Milliseconds).ToList();
        Assert.Equal(7, levels.Count); // Output, then the six writes
        // rp2040js may wake the core up to a microsecond early (see PicoSimulatorTests)
        Assert.InRange(levels[2] - levels[1], 0.249, 0.3);
        Assert.InRange(levels[3] - levels[2], 0, 0.02);
        Assert.InRange(levels[4] - levels[3], 0, 0.02);
        Assert.InRange(levels[5] - levels[4], 1_499.99, 1_500.1);
        Assert.InRange(levels[6] - levels[5], 2_399_999.99, 2_400_000.1);
    }

    [SimulatorFact]
    public async Task Every_member_prints_over_UART0_what_dotnet_prints()
    {
        var source = TimeSpanPrograms.Members(random: 0);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Members"), "Members",
            Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(source, "Members", Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 60_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text, System.Text.Encoding.UTF8.GetString(run.Uart0));
        Assert.Equal(dotnet.Output, run.Uart0);
    }

    [SimulatorFact]
    public async Task Where_dotnet_throws_the_message_goes_to_UART0_and_the_program_stops_at_a_breakpoint()
    {
        var index = 0;
        foreach (var (statement, dotnet) in TimeSpanPrograms.Failures.Take(2))
        {
            var program = PicoBuild.Build(TimeSpanPrograms.Failing(statement), "Failing", Path.Combine(_scratch, $"case{index++}"));

            var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

            Assert.True(run.ExitCode == 3, run.ToString());
            Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
            Assert.Equal("1\r\n" + TimeSpanPrograms.Unhandled(dotnet), System.Text.Encoding.ASCII.GetString(run.Uart0));
        }
    }
}
