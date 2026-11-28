using System.Text;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Analysis;
using ChipSharp.Tests.Sdcc;

namespace ChipSharp.Tests.Stm8;

/// <summary>
/// C# programs built for the blue STM8S103 board by the build pipeline (the code generator "c", SDCC, the stm8 and
/// stm8s103f3 runtimes) and run in chipsim: a blink of the board's LED (PB5), a button through the pin event, a serial
/// echo, a servo's PWM; what a blink and an echo take of the 8 KB of flash and 1 KB of RAM; and what chipsharp check
/// -t stm8s103-board refuses, saying why.
/// </summary>
public sealed class Stm8sProgramTests : IDisposable
{
    private const string Finished = "the program has finished";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [SdccFact]
    public void A_blink_toggles_the_boards_LED_on_PB5_every_half_second_in_under_a_KB()
    {
        var program = Stm8sProgram.Build(_scratch, "Blink", """
            using ChipSharp.Hardware;

            Pin.Output(13);
            while (true)
            {
                Pin.Toggle(13);
                Timing.DelayMilliseconds(500);
            }
            """);

        var run = program.Run(1600);

        // PB5 is true open-drain: high is the pin let go (the LED off, its resistor to 3.3 V)
        var led = run.Pins.Where(p => p.Pin == "PB5").ToList();
        Assert.Equal(["low", "input", "low", "input", "low"], led.Select(p => p.State));
        for (var i = 2; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds - led[i - 1].Milliseconds, 499.99, 500.1);
        }

        Assert.InRange(program.Memory.Code!.Used, 500ul, 1024ul);
        Assert.InRange(program.Memory.Data!.Used, 1ul, 32ul);
    }

    [SdccFact]
    public void A_button_to_GND_with_its_pull_up_on_runs_the_pin_event_at_each_press()
    {
        var program = Stm8sProgram.Build(_scratch, "Button", """
            using ChipSharp.Hardware;

            const uint PortA_CR1 = 0x5003;
            Pin.Output(1);
            Pin.Input(3);
            Registers.Write(PortA_CR1, Registers.Read(PortA_CR1) | 1u << 3); // PA3's pull-up on: high until pressed
            Pin.WatchFalling(3);
            Timing.DelayMilliseconds(100);

            partial class Program
            {
                [Interrupt(Event.Pin)]
                static void Pressed() => Pin.Toggle(1);
            }
            """);

        var run = program.Run(200, "--input", "PA3=low@20ms", "--input", "PA3=high@40ms", "--input", "PA3=low@60ms",
            "--input", "PA3=high@80ms");

        Assert.Contains(Finished, run.Stopped);
        Assert.Contains(run.Pins, p => p.Pin == "PA3" && p.State == "input pull-up");
        var toggles = run.Pins.Where(p => p.Pin == "PA1").Skip(1).ToList();
        Assert.Equal(["high", "low"], toggles.Select(p => p.State));
        Assert.InRange(toggles[0].Milliseconds, 20, 20.05);
        Assert.InRange(toggles[1].Milliseconds, 60, 60.05);
    }

    [SdccFact]
    public void A_serial_echo_sends_back_what_it_receives_on_UART1()
    {
        var program = Stm8sProgram.Build(_scratch, "Echo", """
            using System;
            using ChipSharp.Hardware;

            Uart.Open(0, tx: 29, rx: 30, baud: 115200);
            while (true)
            {
                Uart.WriteByte(0, (byte)Console.Read());
            }
            """);

        var run = program.Run(50, "--send", "hello, STM8\\n@5ms");

        Assert.Equal("hello, STM8\n", Encoding.ASCII.GetString(run.Serial));
        Assert.Equal(["UART1 baud 115108"], run.After("UART1 baud"));
        Assert.InRange(program.Memory.Code!.Used, 1000ul, 2048ul);
        Assert.InRange(program.Memory.Data!.Used, 32ul, 80ul); // the receive buffer's 32 bytes among them
    }

    [SdccFact]
    public void A_servo_on_TIM2s_PD4_gets_its_pulses_at_50_Hz_to_the_microsecond()
    {
        var program = Stm8sProgram.Build(_scratch, "Servo", """
            using ChipSharp.Hardware;

            Pwm.Output(28, 50);
            Pwm.PulseMicroseconds(28, 1000);
            Timing.DelayMilliseconds(100);
            Pwm.PulseMicroseconds(28, 1500);
            Timing.DelayMilliseconds(100);
            Pwm.PulseMicroseconds(28, 2000);
            Timing.DelayMilliseconds(100);
            """);

        var run = program.Run(400);

        Assert.Contains(Finished, run.Stopped);
        Assert.Equal(
            ["PD4 PWM 50.00 Hz, duty 5.00% (1000.00 us high)", "PD4 PWM 50.00 Hz, duty 7.50% (1500.00 us high)",
                "PD4 PWM 50.00 Hz, duty 10.00% (2000.00 us high)"],
            run.After("PD4 PWM 50"));
    }

    [Fact]
    public void A_program_with_everything_the_board_has_passes_the_check()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load("""
            using System;
            using System.Diagnostics;
            using ChipSharp.Hardware;

            class Program
            {
                static volatile int received;

                static void Main()
                {
                    Pin.Output(13);
                    Pin.Input(20);
                    Pin.WatchFalling(20);
                    Timing.EveryMilliseconds(100);
                    Clock.Start(new DateTime(2026, 11, 28, 12, 0, 0));
                    long start = Stopwatch.GetTimestamp();
                    Timing.Delay(TimeSpan.FromMilliseconds(20));
                    Console.WriteLine(Stopwatch.GetElapsedTime(start).Ticks + DateTime.Now.Second + Environment.TickCount);
                    Console.Write((char)Console.Read());
                    Uart.Open(0, 29, 30, 9600);
                    Uart.Watch(0);
                    Uart.WriteByte(0, 65);
                    Adc.Input(26);
                    Console.WriteLine(Adc.Read(26));
                    Pwm.Output(28, 1000);
                    Pwm.Output(27, 1000);
                    Pwm.PulseMicroseconds(3, 250);
                    Pwm.Stop(28);
                    I2c.Open(0, sda: 13, scl: 12, frequency: 400_000);
                    Span<byte> bytes = stackalloc byte[2];
                    Console.WriteLine(I2c.WriteRead(0, 0x68, bytes[..1], bytes) && I2c.Probe(0, 0x27));
                    Spi.Open(0, sck: 21, tx: 22, rx: -1, frequency: 4_000_000, mode: 3);
                    Spi.Write(0, bytes);
                }

                [Interrupt(Event.Pin)]
                static void Changed() => Pin.Toggle(13);

                [Interrupt(Event.Timer)]
                static void Tick() => Pin.Toggle(1);

                [Interrupt(Event.Uart)]
                static void Received() => received += Uart.ReadByte(0);
            }
            """), Stm8sProgram.Board);

        Assert.True(analysis.IsSupported, analysis.Summary);
        Assert.Equal(
            [new TargetInterrupt("pin", 30, null), new TargetInterrupt("timer", 31, null), new TargetInterrupt("uart", 32, null)],
            Interrupts.ForTarget(Stm8sProgram.Board).Values.OrderBy(i => i.Number));
    }

    [Fact]
    public void What_the_board_does_not_have_is_refused_with_what_to_do()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Adc.Input(19);
                    Pwm.Output(19, 1000);
                    I2c.Open(0, 4, 12, 100000);
                    I2c.Open(1, 13, 12, 100000);
                    Spi.Open(0, 21, 22, 23, 10000000, 0);
                    Uart.Open(1, 29, 30, 9600);
                    Uart.Open(0, 4, 30, 9600);
                    Console.WriteLine(Console.ReadLine());
                    object box = new object();
                    Console.WriteLine(box.GetHashCode());
                }
            }
            """), Stm8sProgram.Board);

        Assert.False(analysis.IsSupported);
        var lines = analysis.Groups.Select(group => group.Line).ToList();
        string[] expected =
        [
            "pin 19 (PC3) cannot be an analog input on the target 'stm8s103-board'",
            "pin 19 (PC3) cannot be a PWM output on the target 'stm8s103-board'",
            "A PWM output can be pins 3 (PA3), 27 (PD3) or 28 (PD4).",
            "pin 4 is not an SDA pin of I2C bus 0 (I2c.Open's sda) on target 'stm8s103-board'",
            "I2C bus 1 (I2c.Open's bus) does not exist on target 'stm8s103-board'",
            "an SPI clock of 10000000 Hz (Spi.Open's frequency) is not one target 'stm8s103-board' makes",
            "the target 'stm8s103-board' has no UART1",
            "pin 4 cannot be UART0's TX on the target 'stm8s103-board'",
            "Console.ReadLine makes a string, which needs a heap, and the target 'stm8s103-board' has no heap",
        ];
        Assert.All(expected, part => Assert.True(lines.Any(line => line.StartsWith(part, StringComparison.Ordinal)
            || line.EndsWith(part, StringComparison.Ordinal)), $"no line starts or ends with \"{part}\":\n{analysis.Summary}"));
        Assert.Contains(lines, line => line.Contains("needs a heap", StringComparison.Ordinal)
            && line.Contains("new", StringComparison.Ordinal));
    }
}
