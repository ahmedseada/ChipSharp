using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Analysis;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// What <c>chipsharp check -t uno</c> lets a program use, from the Uno's target files: the pins, the delays, the clock,
/// the console both ways (Console.Read; ReadLine makes a string), USART0 as Uart's port 0 on D1 and D0, and the pin,
/// timer and uart events; and what it refuses, saying why: no heap, no analog inputs, PWM, I2C or SPI yet, no second
/// serial port, a pin USART0 cannot use.
/// </summary>
public sealed class UnoCheckTests
{
    [Fact]
    public void A_program_with_everything_the_Uno_has_passes_the_check()
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
                    Pin.Input(2);
                    Pin.WatchFalling(2);
                    Pin.Watch(14);
                    Timing.EveryMilliseconds(100);
                    Clock.Start(new DateTime(2026, 11, 27, 12, 0, 0));
                    long start = Stopwatch.GetTimestamp();
                    Timing.Delay(TimeSpan.FromMilliseconds(20));
                    Console.WriteLine(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    Console.WriteLine(DateTime.Now.Second + Environment.TickCount);
                    Console.Write((char)Console.Read());
                    Uart.Open(0, 1, 0, 9600);
                    Uart.Watch(0);
                    Uart.WriteByte(0, 65);
                    Timing.DelayMicroseconds(500);
                }

                [Interrupt(Event.Pin)]
                static void Changed() => Pin.Toggle(13);

                [Interrupt(Event.Timer)]
                static void Tick() => Pin.Toggle(12);

                [Interrupt(Event.Uart)]
                static void Received() => received += Uart.ReadByte(0);
            }
            """), UnoProgram.Uno);

        Assert.True(analysis.IsSupported, analysis.Summary);
        Assert.Equal(
            [new TargetInterrupt("pin", 26, null), new TargetInterrupt("timer", 27, null), new TargetInterrupt("uart", 28, null)],
            Interrupts.ForTarget(UnoProgram.Uno).Values.OrderBy(i => i.Number));
    }

    [Fact]
    public void What_the_Uno_does_not_have_is_refused_with_what_to_do()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Adc.Input(14);
                    Pwm.Output(9, 1000);
                    I2c.Open(0, 18, 19, 100000);
                    Spi.Open(0, 13, 11, 12, 1000000, 0);
                    Uart.Open(1, 1, 0, 9600);
                    Uart.Open(0, 4, 0, 9600);
                    Console.WriteLine(Console.ReadLine());
                }
            }
            """), UnoProgram.Uno);

        Assert.False(analysis.IsSupported);
        var lines = analysis.Groups.Select(group => group.Line).ToList();
        string[] expected =
        [
            "ChipSharp.Hardware.Adc needs analog inputs (an ADC), and the target 'uno' has none",
            "ChipSharp.Hardware.Pwm needs PWM outputs, and the target 'uno' has none",
            "ChipSharp.Hardware.I2c needs an I2C bus, and the target 'uno' has none",
            "ChipSharp.Hardware.Spi needs an SPI bus, and the target 'uno' has none",
            "the target 'uno' has no UART1",
            "GPIO 4 cannot be UART0's TX on the target 'uno': 1 place in Program.Main() (IL_0049). UART0's TX can be GPIO 1.",
            "Console.ReadLine makes a string, which needs a heap, and the target 'uno' has no heap",
        ];
        Assert.All(expected, start => Assert.True(lines.Any(line => line.StartsWith(start, StringComparison.Ordinal)),
            $"no line starts with \"{start}\":\n{analysis.Summary}"));
    }
}
