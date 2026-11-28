using System.Text;
using ChipSharp.Tests.Conformance;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// C# programs built with <c>-t esp32c5-devkitc</c> and run in chipsim's esp32c5: what the chip's runtime does that the
/// conformance suite (which every chip passes) does not say: the clock it sets, a program with objects, strings and
/// collections whose garbage is collected while events run, the console on the USB Serial/JTAG port both ways, and the
/// pins the board keeps.
/// </summary>
public sealed class Esp32c5RuntimeTests : ConformanceTests
{
    private static readonly ChipSimulator Chip = ChipSimulators.Get("esp32c5-devkitc");

    private const int Console = 1; // the adapter's serial port of the USB console (0 is UART0)

    [Fact]
    public async Task Blink_on_a_GPIO_runs_at_160_MHz_and_toggles_with_the_delays()
    {
        if (Chip.Missing is not null)
        {
            return;
        }

        var run = await RunAsync(Chip, "Blink", """
            Pin.Output(Output);
            for (var i = 0; i < 4; i++)
            {
                Pin.Toggle(Output);
                Timing.DelayMilliseconds(10);
            }
            """, new ChipStimulus(200));

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        Assert.Contains("CPU clock 160 MHz", run.Output);
        Assert.Equal(["low", "high", "low", "high", "low"], run.Levels(Chip.Pins.Output).Select(change => change.State));
        var toggles = Toggles(run, Chip.Pins.Output);
        for (var i = 0; i < toggles.Count; i++)
        {
            AssertIn(toggles[i], 10.0 * i - Printed, 10.0 * i + 0.1, "the time of a toggle", run);
        }
    }

    [Fact]
    public async Task A_button_event_and_a_timer_event_run_their_methods_while_Main_sleeps()
    {
        if (Chip.Missing is not null)
        {
            return;
        }

        var run = await RunAsync(Chip, "Events", """
            Pin.Output(Output);
            Pin.Output(Second);
            Pin.Input(Input);
            Pin.WatchFalling(Input);
            Timing.EveryMilliseconds(5);
            Timing.DelayMilliseconds(40);
            Timing.Stop();
            """, new ChipStimulus(100) { Inputs = [new(Chip.Pins.Input, true), new(Chip.Pins.Input, false, 12)] }, """
                [Interrupt(Event.Pin)]
                static void Pressed() => Pin.Toggle(Output);

                [Interrupt(Event.Timer)]
                static void Tick() => Pin.Toggle(Second);
            """);

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        Assert.Equal(["low", "high"], run.Levels(Chip.Pins.Output).Select(change => change.State));
        var ticks = Toggles(run, Chip.Pins.Second);
        Assert.InRange(ticks.Count, 7, 8); // the eighth at 40 ms, when the delay ends
        AssertIn(ticks[0], 5 - Printed, 5.1, "the first tick", run);
    }

    [Fact]
    public async Task The_console_on_USB_writes_and_reads_lines()
    {
        if (Chip.Missing is not null)
        {
            return;
        }

        var run = await RunAsync(Chip, "UsbEcho", """
            Console.WriteLine("ready");
            var line = Console.ReadLine();
            Console.WriteLine(line!.ToUpperInvariant() + " " + line.Length);
            """, new ChipStimulus(200) { Sends = [new(Console, "hello\n"u8.ToArray(), 5)] });

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        Assert.Equal("ready\r\nHELLO 5\r\n", Encoding.ASCII.GetString(run.SerialOf(Console)));
    }

    [Fact]
    public async Task A_program_with_objects_collects_its_garbage_while_events_run()
    {
        if (Chip.Missing is not null)
        {
            return;
        }

        var run = await RunAsync(Chip, "HeapEvents", """
            using System.Collections.Generic;
            Check.Start();
            Timing.EveryMilliseconds(1);
            var names = new List<string>();
            long total = 0;
            for (var round = 0; round < 40; round++)
            {
                names.Clear();
                for (var i = 0; i < 100; i++)
                {
                    names.Add("item " + (round * 100 + i));
                }

                total += names[99].Length + new Node(round).Value();
                if (round % 10 == 9)
                {
                    GC.Collect();
                }
            }

            Timing.Stop();
            Console.WriteLine(total);
            Check.That(Program.ticks > 0);
            Check.That(names[0] == "item 3900");
            """, new ChipStimulus(2000), """
                static volatile int ticks;

                [Interrupt(Event.Timer)]
                static void Tick() => ticks++;

                abstract class Shape
                {
                    public abstract int Value();
                }

                sealed class Node(int number) : Shape
                {
                    public override int Value() => number * 2;
                }
            """);

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        AssertChecks(Chip, run, 2);
        Assert.Equal("1909\r\n", Encoding.ASCII.GetString(run.SerialOf(Console)));
    }

    [Fact]
    public async Task A_program_that_runs_out_of_heap_says_so_on_the_console_and_fails()
    {
        if (Chip.Missing is not null)
        {
            return;
        }

        var run = await RunAsync(Chip, "OutOfMemory", """
            using System.Collections.Generic;
            var all = new List<byte[]>();
            while (true)
            {
                all.Add(new byte[8192]);
            }
            """, new ChipStimulus(2000));

        Assert.True(run.Stop == ChipStop.Failed, run.ToString());
        Assert.Contains("OutOfMemoryException", Encoding.ASCII.GetString(run.SerialOf(Console)));
    }

    [Fact]
    public void Check_refuses_the_pins_the_board_keeps_and_the_hardware_the_runtime_does_not_have_yet()
    {
        static string Problems(string source)
        {
            var assembly = CSharpCompiler.Compile([source], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], "Refused");
            var check = Esp32c5Build.Check(assembly, "Refused", Esp32c5Build.Board);
            Assert.False(check.IsSupported);
            return string.Join("\n", check.Groups.SelectMany(group => group.Problems.Select(problem => problem.Line)));
        }

        var pins = Problems("""
            using ChipSharp.Hardware;

            Pin.Output(27);
            Pin.Output(6);
            Pin.Watch(13);
            Uart.Open(0, 1, 12, 115200);
            """);
        Assert.True(pins.Contains("27 cannot be a pin a program uses"), pins);
        Assert.True(pins.Contains("13 cannot be watched"), pins);
        Assert.True(pins.Contains("cannot be UART0's TX"), pins);
        Assert.DoesNotContain("6 cannot be", pins);

        var buses = Problems("""
            using ChipSharp.Hardware;

            I2c.Open(0, 1, 4, 100000);
            Adc.Input(1);
            """);
        Assert.Contains("I2c", buses);
        Assert.Contains("Adc", buses);
    }
}
