using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Ch32v003;

/// <summary>
/// What <c>chipsharp check</c> refuses on <c>ch32v003-evt</c>, before the program runs: PD1 (SWIO) and PD7 (NRST), and
/// the board's crystal pins PA1 and PA2, as plain pins or watched; USART1 on other pins than PD5 and PD6, or a port
/// other than 0; and the I2C bus, the SPI bus, the analog inputs and the PWM outputs, which the chip's runtime does not
/// have yet (M5's wave 2). A <c>Pull.Down</c>, the timer, pin and uart events, the clock and the console are the chip's
/// and allowed.
/// </summary>
public sealed class Ch32v003CheckTests
{
    private static string Problems(string source)
    {
        var assembly = CSharpCompiler.Compile([source], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], "Refused");
        var check = Ch32v003Build.Check(assembly, "Refused", Ch32v003Build.Board);
        Assert.False(check.IsSupported);
        return string.Join("\n", check.Groups.SelectMany(group => group.Problems.Select(problem => problem.Line)));
    }

    [Fact]
    public void A_pull_down_the_boards_pins_the_events_the_clock_and_USART1_are_allowed()
    {
        var assembly = CSharpCompiler.Compile(["""
            using System;
            using ChipSharp.Hardware;

            Pin.Input(27, Pull.Down);
            Pin.Input(16, Pull.Up);
            Pin.Output(24);
            Pin.Output(30);
            Pin.WatchRising(27);
            Timing.EveryMilliseconds(5);
            Uart.Open(0, 29, 30, 9600);
            Uart.Watch(0);
            Clock.Start(new DateTime(2026, 11, 28));
            Console.WriteLine(Console.Read());

            partial class Program
            {
                [Interrupt(Event.Pin)]
                static void Changed() => Pin.Toggle(24);

                [Interrupt(Event.Timer)]
                static void Tick() => Pin.Toggle(24);

                [Interrupt(Event.Uart)]
                static void Arrived() => Uart.ReadByte(0);
            }
            """], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], "Allowed");

        var check = Ch32v003Build.Check(assembly, "Allowed", Ch32v003Build.Board);

        Assert.True(check.IsSupported, string.Join("\n", check.Groups.Select(group => group.Line)));
    }

    [Fact]
    public void The_debug_wire_the_reset_pin_and_the_crystals_pins_are_refused_as_pins_and_as_watched()
    {
        var problems = Problems("""
            using ChipSharp.Hardware;

            Pin.Output(25);
            Pin.Input(31, Pull.Up);
            Pin.Write(1, true);
            Pin.Watch(2);
            Pin.WatchFalling(25);
            """);

        Assert.Contains("25 cannot be a pin a program uses", problems);
        Assert.Contains("31 cannot be a pin a program uses", problems);
        Assert.Contains("1 cannot be a pin a program uses", problems);
        Assert.Contains("2 cannot be watched", problems);
        Assert.Contains("25 cannot be watched", problems);
    }

    [Fact]
    public void USART1_on_other_pins_or_another_port_and_the_buses_analog_inputs_and_PWM_of_wave_2_are_refused()
    {
        var problems = Problems("""
            using ChipSharp.Hardware;

            Uart.Open(0, 24, 30, 9600);
            Uart.Open(1, 29, 30, 9600);
            I2c.Open(0, 17, 18, 100_000);
            Spi.Open(0, 21, 22, 23, 1_000_000, 0);
            Adc.Input(26);
            Pwm.Output(24, 1000);
            """);

        Assert.Contains("24 cannot be UART0's TX", problems);
        Assert.Contains("has no UART1", problems);
        Assert.Contains("ChipSharp.Hardware.I2c", problems);
        Assert.Contains("ChipSharp.Hardware.Spi", problems);
        Assert.Contains("ChipSharp.Hardware.Adc", problems);
        Assert.Contains("ChipSharp.Hardware.Pwm", problems);
    }
}
