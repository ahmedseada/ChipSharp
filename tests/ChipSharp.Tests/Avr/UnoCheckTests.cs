using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Analysis;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// What <c>chipsharp check -t uno</c> lets a program use, from the Uno's target files: the pins, the delays, the clock,
/// the console both ways (Console.Read; ReadLine makes a string), USART0 as Uart's port 0 on D1 and D0, the pin, timer
/// and uart events, the analog inputs A0-A5, the PWM pins, the I2C bus on A4 and A5 and the SPI bus on D13, D11 and D12;
/// and what it refuses, saying why and naming the pins as the Uno's headers do: no heap, no second serial port or bus,
/// a pin, a clock or a bus the Uno does not have for them.
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
                    Adc.Input(14);
                    Adc.Input(19);
                    Console.WriteLine(Adc.Read(14) + Adc.Volts(19) + Adc.Temperature());
                    Pwm.Output(3, 1000);
                    Pwm.Output(5, 1000);
                    Pwm.Output(6, 490);
                    Pwm.Output(9, 50);
                    Pwm.PulseMicroseconds(9, 1500);
                    Pwm.Output(11, 31372);
                    Pwm.Duty(3, 0.25);
                    Pwm.Stop(5);
                    I2c.Open(0, sda: 18, scl: 19, frequency: 400_000);
                    Span<byte> bytes = stackalloc byte[2];
                    Console.WriteLine(I2c.WriteRead(0, 0x68, bytes[..1], bytes) && I2c.Probe(0, 0x27));
                    Spi.Open(0, sck: 13, tx: 11, rx: -1, frequency: 4_000_000, mode: 3);
                    Spi.Write(0, bytes);
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
                    Adc.Input(2);
                    Pwm.Output(10, 1000);
                    I2c.Open(0, 4, 19, 100000);
                    I2c.Open(1, 18, 19, 100000);
                    I2c.Open(0, 18, 19, 1000000);
                    Spi.Open(0, 13, 7, -1, 1000000, 0);
                    Spi.Open(0, 13, 11, 12, 10000000, 0);
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
            "pin 2 (D2) cannot be an analog input on the target 'uno'",
            "An analog input can be pins 14 to 19 (A0 to A5).",
            "pin 10 (D10) cannot be a PWM output on the target 'uno'",
            "A PWM output can be pins 3 (D3), 5 (D5), 6 (D6), 9 (D9) or 11 (D11).",
            "pin 4 (D4) is not an SDA pin of I2C bus 0 (I2c.Open's sda) on target 'uno'",
            "Its SDA pin is pin 18 (A4).",
            "I2C bus 1 (I2c.Open's bus) does not exist on target 'uno'",
            "It has one I2C bus, bus 0.",
            "an I2C clock of 1000000 Hz (I2c.Open's frequency) is not one target 'uno' makes",
            "It makes 490 to 400000 Hz.",
            "pin 7 (D7) is not a TX pin of SPI bus 0 (Spi.Open's tx) on target 'uno'",
            "Its TX pin is pin 11 (D11) (-1 for none).",
            "an SPI clock of 10000000 Hz (Spi.Open's frequency) is not one target 'uno' makes",
            "It makes 125000 to 8000000 Hz.",
            "the target 'uno' has no UART1",
            "pin 4 (D4) cannot be UART0's TX on the target 'uno'",
            "UART0's TX can be pin 1 (D1).",
            "Console.ReadLine makes a string, which needs a heap, and the target 'uno' has no heap",
        ];
        Assert.All(expected, part => Assert.True(lines.Any(line => line.StartsWith(part, StringComparison.Ordinal)
            || line.EndsWith(part, StringComparison.Ordinal)), $"no line starts or ends with \"{part}\":\n{analysis.Summary}"));
    }
}
