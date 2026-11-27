using System.Text;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// ChipSharp.Hardware's Adc and Pwm on the simulated Uno, the same C# as on the Pico: A0-A5 in the chip's own 10 bits
/// against 5 V, volts and the temperature sensor; a handler that reads an input while the program's conversion runs;
/// the PWM pins at the frequencies Timer0 and Timer2 make, D9 to the half microsecond (a servo's 50 Hz), duty cycles
/// and pulses, the steady levels, Stop; and the mistakes that stop the program.
/// </summary>
public sealed class UnoAnalogPwmSimulatorTests : IDisposable
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

    [AvrSimulatorFact]
    public async Task The_ADC_reads_the_voltages_and_the_temperature_the_simulator_gives_in_10_bits()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "Analog", """
            using System;
            using ChipSharp.Hardware;

            Say.Open();
            for (var pin = 14; pin <= 19; pin++)
            {
                Adc.Input(pin);
            }

            Show();
            Timing.DelayMilliseconds(50);
            Show();

            static void Show()
            {
                for (var pin = 14; pin <= 17; pin++)
                {
                    Say.Number(Adc.Read(pin));
                    Say.Space();
                }

                // millivolts, and hundredths of a degree
                Say.Number((int)(Adc.Volts(15) * 1000));
                Say.Space();
                Say.Number((int)(Adc.Temperature() * 100 + 0.5));
                Say.Line();
            }
            """ + UnoSay.Source);

        var run = await AvrSimulator.RunAsync(program.Hex, 200,
            "--adc", "A0=0V", "--adc", "A1=2.5V", "--adc", "A2=5V", "--adc", "A3=700", "--adc", "A1=1V@20ms",
            "--adc", "temp=30C@20ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        Assert.Contains("input: A1 1.000 V (204)", run.After("input: A1"));
        Assert.Contains("input: temperature 30 C (358)", run.After("input: temperature"));

        // volts / 5 V * 1024, at most 1023; Volts is the reading * 5 / 1024; the temperature 25 C (352) at first, then
        // 358: 25 + 6 / 1.28 degrees
        Assert.Equal("0 512 1023 700 2500 2500\n0 204 1023 700 996 2969\n", Encoding.ASCII.GetString(run.Usart0));
        Assert.Equal(996, (int)(204 * 5.0 / 1024 * 1000));
        Assert.Equal(2969, (int)((25 + (358 - 352) / 1.28) * 100 + 0.5));
    }

    [AvrSimulatorFact]
    public async Task A_handler_that_reads_an_input_while_the_program_converts_gets_its_own_and_the_program_too()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "AnalogHandler", """
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static volatile int ticks;
                static volatile int wrong;

                static void Main()
                {
                    Say.Open();
                    Adc.Input(14);
                    Adc.Input(15);
                    var mistaken = 0;
                    Timing.EveryMicroseconds(150);
                    for (var i = 0; i < 300; i++)
                    {
                        if (Adc.Read(14) != 100)
                        {
                            mistaken++;
                        }
                    }

                    Timing.Stop();
                    Say.Bool(ticks > 100);
                    Say.Space();
                    Say.Number(wrong);
                    Say.Space();
                    Say.Number(mistaken);
                    Say.Line();
                }

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    ticks++;
                    if (Adc.Read(15) != 900)
                    {
                        wrong++;
                    }
                }
            }
            """ + UnoSay.Source);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000, "--adc", "A0=100", "--adc", "A1=900");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("T 0 0\n", Encoding.ASCII.GetString(run.Usart0));
    }

    [AvrSimulatorFact]
    public async Task PWM_outputs_run_at_their_timers_frequencies_and_D9_at_a_servos_to_the_half_microsecond()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "Pwm", """
            using ChipSharp.Hardware;

            Pwm.Output(5, 1000);            // Timer0: 16 MHz / 64 / 256, 976.56 Hz, the nearest
            Pwm.Output(6, 1000);            // its other output, the same frequency, another duty cycle
            Pwm.Duty(5, 0.25);
            Pwm.Duty(6, 0.75);
            Pwm.Output(3, 500);             // Timer2: 16 MHz / 64 / 510, 490.20 Hz (phase correct)
            Pwm.Output(11, 500);
            Pwm.Duty(3, 0.5);
            Pwm.PulseMicroseconds(11, 510); // 64 counts of 4 us, twice: up and down
            Pwm.Output(9, 50);              // a servo's
            Pwm.PulseMicroseconds(9, 1500);
            Timing.DelayMilliseconds(100);
            Pwm.PulseMicroseconds(9, 1000);
            Pwm.Duty(3, 0);
            Timing.DelayMilliseconds(100);
            Pwm.Stop(5);
            Pwm.Duty(6, 1);
            Pwm.Duty(9, 0.1);
            Timing.DelayMilliseconds(100);
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        var pwm = run.Pwm;

        // Timer0's: the simulator ends a fast PWM's pulse one count before the chip does (at OCR, not OCR + 1); and its
        // edges can come a clock cycle (62.5 ns) late when the program reads the timer then
        var d5 = pwm.First(p => p.Pin == "D5");
        Assert.InRange(d5.Hertz, 976.5, 976.65);
        Assert.InRange(d5.Duty, 24.5, 25.01);
        var d6 = pwm.First(p => p.Pin == "D6");
        Assert.InRange(d6.Hertz, 976.5, 976.65);
        Assert.InRange(d6.Duty, 74.5, 75.01);

        // Timer2's, phase correct: 2 * OCR counts of 510 high
        var d3 = pwm.First(p => p.Pin == "D3");
        Assert.InRange(d3.Hertz, 490.15, 490.25);
        Assert.InRange(d3.Duty, 50.15, 50.25);
        var d11 = pwm.First(p => p.Pin == "D11");
        Assert.InRange(d11.Hertz, 490.15, 490.25);
        Assert.InRange(d11.HighMicroseconds, 511.9, 512.1);

        // D9, Timer1's compare A: 20 ms and the pulse to the half microsecond, the new pulse from the next period on
        Assert.Equal([(50, 1500), (50, 1000), (50, 2000)], Periods(run, "D9"));

        // duty 0 and 1 are plain levels; Stop lets go of the pin
        var after = run.Time("D9 PWM 50.00 Hz, duty 5.00%");
        Assert.Contains(run.After("D3 "), line => line is "D3 low" or "D3 PWM steady low");
        Assert.Contains(run.Pins, p => p.Pin == "D5" && p.State == "input" && p.Milliseconds > after);
        Assert.Contains(run.After("D6 "), line => line is "D6 high" or "D6 PWM steady high");
    }

    [AvrSimulatorFact]
    public async Task D9_makes_any_frequency_from_1_Hz_and_drops_a_pulse_shorter_than_20_microseconds()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "PwmD9", """
            using ChipSharp.Hardware;

            Pwm.Output(9, 7);               // 142857 us, more than four rounds of Timer1
            Pwm.Duty(9, 0.25);
            Timing.DelayMilliseconds(600);
            Pwm.Output(9, 1000);
            Pwm.Duty(9, 0.5);
            Timing.DelayMilliseconds(20);
            Pwm.PulseMicroseconds(9, 15);   // too short: low
            Timing.DelayMilliseconds(20);
            Pwm.PulseMicroseconds(9, 990);  // the gap too short: high
            Timing.DelayMilliseconds(20);
            Pwm.Stop(9);
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 2000);

        Assert.True(run.ExitCode == 0, run.ToString());

        // the last 7 Hz period is cut short when Output starts D9 afresh at 1000 Hz
        var periods = Periods(run, "D9");
        Assert.Equal((7, 35714), periods[0]);
        Assert.Equal((1000, 500), periods[^1]);
        Assert.Equal(3, periods.Count);
        Assert.Equal("D9 PWM steady low", run.After("D9 PWM steady").First());
        Assert.Contains("D9 PWM steady high", run.After("D9 PWM steady"));
        Assert.Equal("input", run.Pins.Last(p => p.Pin == "D9").State);
    }

    /// <summary>
    /// The periods a pin's PWM output had, in whole hertz and microseconds high, each once while it lasts: the simulator
    /// prints a period again when an edge comes a clock cycle late, which leaves its frequency within 0.1% and its
    /// pulse within a microsecond.
    /// </summary>
    private static List<(int Hertz, int HighMicroseconds)> Periods(AvrSimulatorRun run, string pin)
    {
        var periods = new List<(int Hertz, int HighMicroseconds)>();
        foreach (var period in run.Pwm.Where(p => p.Pin == pin))
        {
            if (periods.Count == 0 || Math.Abs(period.Hertz - periods[^1].Hertz) > periods[^1].Hertz / 1000.0
                || Math.Abs(period.HighMicroseconds - periods[^1].HighMicroseconds) > 1)
            {
                periods.Add(((int)Math.Round(period.Hertz), (int)Math.Round(period.HighMicroseconds)));
            }
        }

        return periods;
    }

    public static TheoryData<string, string> Mistakes() => new()
    {
        {
            "Adc.Input(Value(2));",
            "System.ArgumentException: Pin 2 is not an analog input: on the Arduino Uno they are pins 14 to 19 (A0 to A5). " +
                "(Parameter 'pin')"
        },
        {
            "Adc.Read(14);",
            "System.InvalidOperationException: Pin 14 is not an analog input yet: call Adc.Input(14) first."
        },
        {
            "I2c.Open(0, 18, 19, 100_000); Adc.Input(18);",
            "System.InvalidOperationException: Pin 18 (A4) is the I2C bus's (I2c.Open: SDA on A4, SCL on A5), so it cannot " +
                "be an analog input. Use A0 to A3."
        },
        {
            "Pwm.Output(Value(10), 1000);",
            "System.ArgumentException: Pin 10 (D10) cannot be a PWM output: its timer output, OC1B, is Timer1's compare B, " +
                "which the timer event (Timing.Every..., After...) uses. The PWM pins are 3, 5, 6, 9 and 11. (Parameter 'pin')"
        },
        {
            "Pwm.Output(Value(4), 1000);",
            "System.ArgumentException: Pin 4 is not a PWM pin: on the Arduino Uno they are pins 3, 5, 6, 9 and 11 (D3, D5, " +
                "D6, D9, D11). (Parameter 'pin')"
        },
        {
            "Pwm.Output(5, 1000); Pwm.Output(6, Value(100));",
            "System.InvalidOperationException: Pin 6 shares Timer0 with pin 5, which runs at 1000 Hz, and a timer runs at " +
                "one frequency. Give both the same frequency, stop pin 5 first, or use a pin of another timer."
        },
        {
            "Pwm.Output(3, Value(20));",
            "System.ArgumentOutOfRangeException: The ATmega328P cannot make a PWM frequency of 20 Hz on pin 3: Timer2 makes " +
                "30 to 62500 Hz (pin 9, any from 1 to 10000 Hz). (Parameter 'frequency')"
        },
        {
            "Pwm.Output(9, Value(20_000));",
            "System.ArgumentOutOfRangeException: The ATmega328P cannot make a PWM frequency of 20000 Hz on pin 9: Timer1's " +
                "compare A makes 1 to 10000 Hz there. (Parameter 'frequency')"
        },
        {
            "Pwm.Duty(9, 0.5);",
            "System.InvalidOperationException: Pin 9 is not a PWM output: call Pwm.Output(9, frequency) first."
        },
        {
            "Pwm.Output(9, 50); Pwm.Duty(9, Value(2));",
            "System.ArgumentOutOfRangeException: A duty cycle is from 0 (always low) to 1 (always high). (Parameter 'duty')"
        },
        {
            "Pwm.Output(3, 1000); Pwm.PulseMicroseconds(3, Value(-1));",
            "System.ArgumentOutOfRangeException: A pulse of -1 microseconds is shorter than none. (Parameter 'microseconds')"
        },
        {
            "Spi.Open(0, 13, 11, -1, 1_000_000, 0); Pwm.Output(11, 1000);",
            "System.InvalidOperationException: Pin 11 (D11) is SPI0's TX (MOSI) while the bus is open, so it cannot be a " +
                "PWM output. Use pin 3, 5, 6 or 9."
        },
        {
            "Pwm.Output(11, 1000); Spi.Open(0, 13, 11, -1, 1_000_000, 0);",
            "System.InvalidOperationException: Pin 11 (D11) is a PWM output, and while SPI0 is open it is the bus's TX " +
                "(MOSI). Stop it first (Pwm.Stop(11)), or use another PWM pin: 3, 5, 6 or 9."
        },
    };

    [AvrSimulatorTheory]
    [MemberData(nameof(Mistakes))]
    public async Task A_mistake_the_chip_finds_stops_the_program_with_a_message(string code, string message)
    {
        var program = UnoProgram.BuildCSharp(_scratch, "Mistake", $$"""
            using System;
            using ChipSharp.Hardware;

            {{code}}

            partial class Program
            {
                static int Value(int value) => value;
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("the program hit a breakpoint", run.Stopped);
        Assert.Equal($"Unhandled exception. {message}\r\n", Encoding.UTF8.GetString(run.Usart0));
    }
}
