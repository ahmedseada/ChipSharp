using System.Buffers.Binary;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Image.Uf2;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// Interrupts on the Pico: the handlers of the timer and pin events in the vector table, and the examples and the
/// timer and pin calls run in the rp2040js simulator.
/// </summary>
public sealed class InterruptSimulatorTests : IDisposable
{
    private const uint FlashStart = 0x10000000;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private static string Example(string folder) => File.ReadAllText(Path.Combine(Repository.Examples, folder, "Program.cs"));

    [PicoFact]
    public void The_handlers_take_the_place_of_the_weak_defaults_in_the_vector_table()
    {
        var program = PicoBuild.Build("""
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Pin.WatchRising(15);
                    Timing.EveryMilliseconds(10);
                }

                [Interrupt(Event.Timer)]
                static void Tick() => Pin.Toggle(25);

                [Interrupt(Event.Pin)]
                static void Pressed() => Pin.Toggle(24);
            }
            """, "Vectors", _scratch);
        var elf = ElfFile.Read(program.Elf);
        var flash = PicoBuild.FlashImage(elf);
        uint Vector(int index) => BinaryPrimitives.ReadUInt32LittleEndian(flash.AsSpan(0x100 + 4 * index));
        var unhandled = elf.Symbols["chipsharp_unhandled_exception"];

        // IRQ 1 (TIMER_IRQ_1) and IRQ 13 (IO_IRQ_BANK0), as rp2040.json's "interrupts" says, at 16 + the IRQ
        Assert.Equal(elf.Symbols["chipsharp_isr_irq1"], Vector(16 + 1));
        Assert.Equal(elf.Symbols["chipsharp_isr_irq13"], Vector(16 + 13));
        Assert.NotEqual(unhandled, Vector(16 + 1));
        Assert.NotEqual(unhandled, Vector(16 + 13));
        Assert.NotEqual(Vector(16 + 1), Vector(16 + 13));
        Assert.InRange(Vector(16 + 1), FlashStart, FlashStart + 0x200000);
        Assert.Equal(1u, Vector(16 + 1) & 1); // Thumb
        Assert.Equal(unhandled, Vector(16 + 2)); // no handler for IRQ 2: the runtime's default
        Assert.Equal(elf.Symbols["chipsharp_isr_irq0"], Vector(16)); // the delays' alarm, the runtime's own
    }

    [SimulatorFact]
    public async Task The_timer_blink_example_toggles_GPIO_25_every_500_ms_while_Main_does_nothing()
    {
        var program = PicoBuild.Build(Example("pico-timer-blink"), "PicoTimerBlink", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 2600);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.EndsWith("stopped: 2600 ms of simulated time have passed", run.Stopped);
        var led = run.Pins.Where(p => p.Pin == 25 && p.State is "low" or "high").ToList();
        Assert.Equal(["low", "high", "low", "high", "low", "high"], led.Select(p => p.State));
        // Pin.Output, then the timer: 500 ms after it starts, and every 500 ms after that
        Assert.InRange(led[0].Milliseconds, 0, 5);
        for (var i = 1; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds - led[0].Milliseconds, 500 * i - 0.01, 500 * i + 0.01);
        }
    }

    [SimulatorFact]
    public async Task The_button_example_toggles_the_LED_at_each_press_and_not_at_each_release()
    {
        var program = PicoBuild.Build(Example("pico-button"), "PicoButton", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000,
            "--input", "15=low", "--input", "15=high@100ms", "--input", "15=low@200ms",
            "--input", "15=high@300.5ms", "--input", "15=low@400ms", "--input", "15=high@700ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("[   300.500 ms] input: GPIO15 high", run.Output);
        var led = run.Pins.Where(p => p.Pin == 25 && p.State is "low" or "high").ToList();
        Assert.Equal(["low", "high", "low", "high"], led.Select(p => p.State));
        Assert.InRange(led[0].Milliseconds, 0, 5);
        Assert.InRange(led[1].Milliseconds, 100, 100.01);
        Assert.InRange(led[2].Milliseconds, 300.5, 300.51);
        Assert.InRange(led[3].Milliseconds, 700, 700.01);
    }

    [SimulatorFact]
    public async Task A_handler_counts_in_a_volatile_field_that_Main_prints_over_UART0()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static volatile int ticks;

                static void Main()
                {
                    Timing.EveryMilliseconds(10);
                    var shown = 0;
                    while (shown < 5)
                    {
                        var now = ticks;
                        if (now != shown)
                        {
                            shown = now;
                            Console.WriteLine(shown);
                        }
                    }

                    Timing.Stop();
                    Timing.DelayMilliseconds(50);
                    Console.WriteLine(ticks);
                }

                [Interrupt(Event.Timer)]
                static void Tick() => ticks++;
            }
            """, "Counter", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped); // stopped: nothing wakes the chip again
        Assert.Equal("1\r\n2\r\n3\r\n4\r\n5\r\n5\r\n", Encoding.ASCII.GetString(run.Uart0));
    }

    [SimulatorFact]
    public async Task Every_timer_and_pin_call_works_in_the_simulator()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static volatile int ticks;
                static volatile int changes;

                static void Main()
                {
                    Timing.AfterMilliseconds(2);        // once
                    Timing.DelayMilliseconds(10);
                    Console.WriteLine(ticks);
                    Timing.AfterMicroseconds(0);        // at once
                    Timing.DelayMicroseconds(10);
                    Console.WriteLine(ticks);
                    Timing.EveryMicroseconds(1000);     // 10 times in 10.5 ms
                    Timing.DelayMicroseconds(10500);
                    Timing.EveryMilliseconds(0);        // stops it
                    Console.WriteLine(ticks);
                    Timing.DelayMilliseconds(5);
                    Console.WriteLine(ticks);

                    Pin.Input(14);
                    Pin.Watch(14);                      // GPIO 14 changes at 40, 50, 60, 70, 80 and 90 ms
                    Timing.DelayMilliseconds(30);
                    Console.WriteLine(changes);         // at about 55 ms: high at 40, low at 50
                    Pin.WatchFalling(14);
                    Timing.DelayMilliseconds(20);
                    Console.WriteLine(changes);         // at about 75 ms: low at 70
                    Pin.Unwatch(14);
                    Timing.DelayMilliseconds(20);
                    Console.WriteLine(changes);
                }

                [Interrupt(Event.Timer)]
                static void Tick() => ticks++;

                [Interrupt(Event.Pin)]
                static void Changed() => changes++;
            }
            """, "Events", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000,
            "--input", "14=high@40ms", "--input", "14=low@50ms", "--input", "14=high@60ms",
            "--input", "14=low@70ms", "--input", "14=high@80ms", "--input", "14=low@90ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal("1\r\n2\r\n12\r\n12\r\n2\r\n3\r\n3\r\n", Encoding.ASCII.GetString(run.Uart0));
    }

    [Fact]
    public void Building_a_program_with_a_handler_for_the_PC_says_that_interrupts_need_a_chip()
    {
        var dll = Path.Combine(_scratch, "TimerBlink.dll");
        Directory.CreateDirectory(_scratch);
        File.WriteAllBytes(dll, CSharpCompiler.Compile([Example("pico-timer-blink")], [PicoBuild.HardwareAssembly], "TimerBlink"));

        var error = Assert.Throws<ChipSharpException>(() => ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = "host",
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = _scratch,
        }));

        Assert.Contains("[Interrupt] methods need interrupts, and target 'host' has none: 1 place in Program.Toggle(). " +
            "Interrupts are a chip's, not a PC's: build the program for a chip, e.g. with -t pico.", error.Message);
    }

    [Fact]
    public async Task Check_names_the_handlers_and_checks_them_against_a_target()
    {
        var dll = Path.Combine(_scratch, "TimerBlink.dll");
        Directory.CreateDirectory(_scratch);
        File.WriteAllBytes(dll, CSharpCompiler.Compile([Example("pico-timer-blink")], [PicoBuild.HardwareAssembly], "TimerBlink"));

        var (exitCode, output, _) = await ChipSharpCli.RunAsync("check", dll, "-t", "pico");
        var (hostExitCode, _, hostError) = await ChipSharpCli.RunAsync("check", dll, "-t", "host");

        Assert.Equal(0, exitCode);
        Assert.EndsWith("""
              Program.<Main>$(string[])
              Program.Toggle() (handles the timer event)

            """.ReplaceLineEndings("\n"), output.ReplaceLineEndings("\n"));
        Assert.Equal(1, hostExitCode);
        Assert.Contains("[Interrupt] methods need interrupts, and target 'host' has none", hostError);
    }
}
