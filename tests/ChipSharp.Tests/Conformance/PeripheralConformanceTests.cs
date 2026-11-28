using System.Globalization;

// a ChipTheory's cases, one per chip, are its discoverer's (ChipTheoryDiscoverer), not a data attribute's
#pragma warning disable xUnit1003

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The conformance suite's peripherals, with the simulators' shared devices: I2C to an MPU-6050 and to an address
/// nothing answers, SPI through a loopback, an analog input's readings of the voltages given, a PWM output's frequency
/// and pulses; and how a program that fails stops.
/// </summary>
public sealed class PeripheralConformanceTests : ConformanceTests
{
    [ChipTheory("i2c")]
    public async Task I2c_talks_to_a_device_and_says_when_nothing_answers(string target)
    {
        var chip = ChipSimulators.Get(target);

        var run = await RunAsync(chip, "I2cDevice", """
            Check.Start();
            I2c.Open(0, Sda, Scl, 100_000);
            Span<byte> register = stackalloc byte[1];
            Span<byte> value = stackalloc byte[1];
            register[0] = 0x75;
            Check.That(I2c.WriteRead(0, 0x68, register, value) && value[0] == 0x68);
            Check.That(!I2c.Write(0, 0x50, register));
            Check.That(I2c.Probe(0, 0x68) && !I2c.Probe(0, 0x69));
            """, new ChipStimulus(200) { Devices = [new("i2c", 0, "mpu6050", Address: 0x68)] });

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        AssertChecks(chip, run, 3);
        Assert.Contains("I2C0 0x68 write 75, read 68", run.Bus);
        Assert.Contains("I2C0 0x50 write: no answer", run.Bus);
        Assert.Contains("I2C0 0x69 read: no answer", run.Bus);
        Assert.Contains("I2C0 100000 Hz", run.Bus);
    }

    [ChipTheory("spi")]
    public async Task Spi_transfers_bytes_both_ways_in_the_mode_asked_for(string target)
    {
        var chip = ChipSimulators.Get(target);

        var run = await RunAsync(chip, "SpiLoopback", """
            Check.Start();
            Spi.Open(0, Sck, Mosi, Miso, 1_000_000, 0);
            Span<byte> sent = stackalloc byte[3];
            sent[0] = 0x12;
            sent[1] = 0x34;
            sent[2] = 0xA5;
            Span<byte> received = stackalloc byte[3];
            Spi.Transfer(0, sent, received);
            Check.That(received[0] == 0x12 && received[1] == 0x34 && received[2] == 0xA5);
            Spi.Read(0, received);
            Check.That(received[0] == 0 && received[1] == 0 && received[2] == 0);
            Spi.Write(0, sent);
            """, new ChipStimulus(200) { Devices = [new("spi", 0, "loopback")] });

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        AssertChecks(chip, run, 2);
        var mode = Assert.Single(run.Bus, line => line.StartsWith("SPI0 mode", StringComparison.Ordinal));
        Assert.StartsWith("SPI0 mode 0, ", mode);
        var hertz = double.Parse(mode["SPI0 mode 0, ".Length..^" Hz".Length], CultureInfo.InvariantCulture);
        AssertIn(hertz, 500_000, 1_000_000, "the SPI clock", run);
    }

    [ChipTheory("adc")]
    public async Task An_analog_input_reads_the_voltage_on_its_pin(string target)
    {
        var chip = ChipSimulators.Get(target);
        var adc = chip.Adc!;
        (int Low, int High) Reading(double volts) =>
            ((int)Math.Floor(volts / adc.ReferenceVolts * adc.FullScale) - 3,
                (int)Math.Ceiling(volts / adc.ReferenceVolts * adc.FullScale) + 3);
        var (low1, high1) = Reading(1.0);
        var (low2, high2) = Reading(2.0);

        var run = await RunAsync(chip, "AdcReadings", $$"""
            Check.Start();
            Adc.Input(AdcPin);
            var first = Adc.Read(AdcPin);
            Timing.DelayMilliseconds(20);
            var second = Adc.Read(AdcPin);
            Check.That(first >= {{low1}} && first <= {{high1}});
            Check.That(second >= {{low2}} && second <= {{high2}});
            """, new ChipStimulus(200)
            {
                Analog = [new(chip.Pins.Adc!.Value, 1.0), new(chip.Pins.Adc!.Value, 2.0, 10)],
            });

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        AssertChecks(chip, run, 2);
    }

    [ChipTheory("pwm")]
    public async Task A_PWM_output_makes_the_frequency_and_the_pulses_asked_for_until_Stop(string target)
    {
        var chip = ChipSimulators.Get(target);
        var pin = chip.Pins.Pwm!.Value;
        var tolerance = chip.Tolerances.PwmMicroseconds;

        var run = await RunAsync(chip, "PwmPulses", """
            Pwm.Output(PwmPin, 1000);
            Pwm.PulseMicroseconds(PwmPin, 250);
            Timing.DelayMilliseconds(20);
            Pwm.PulseMicroseconds(PwmPin, 600);
            Timing.DelayMilliseconds(20);
            Pwm.Stop(PwmPin);
            Timing.DelayMilliseconds(10);
            """, new ChipStimulus(200));

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        var periods = run.Pwm.Where(period => period.Pin == pin).ToList();
        var quarter = periods.FindIndex(period => Math.Abs(period.HighMicroseconds - 250) <= tolerance);
        var more = periods.FindIndex(period => Math.Abs(period.HighMicroseconds - 600) <= tolerance);
        Assert.True(quarter >= 0 && more > quarter, $"no 250 us pulse, then 600 us ones\n{run}");
        AssertIn(periods[quarter].Hertz, 980, 1020, "the PWM frequency", run);
        AssertIn(periods[more].Hertz, 980, 1020, "the PWM frequency", run);
        // Stop lets go of the pin, as after start-up: an input, with no period after
        var stopped = run.Pins.Last(change => change.Pin == pin);
        Assert.True(stopped.State.StartsWith("input", StringComparison.Ordinal), $"the pin is still driven after Stop\n{run}");
        Assert.DoesNotContain(periods, period => period.Milliseconds > stopped.Milliseconds);
    }

    [ChipTheory("chip")]
    public async Task A_program_that_fails_stops_as_a_failure_and_says_why_on_the_console(string target)
    {
        var chip = ChipSimulators.Get(target);

        var run = await RunAsync(chip, "Failure", """
            Pin.Output(Output);
            Pin.Write(Output, true);
            var values = new int[2];
            values[Program.Index] = 1;
            Pin.Write(Output, false);
            """, new ChipStimulus(200), "    static int Index = 2;");

        Assert.True(run.Stop == ChipStop.Failed, run.ToString());
        Assert.Equal(["low", "high"], run.Levels(chip.Pins.Output).Select(change => change.State));
        if (chip.Description.HasConsole)
        {
            Assert.Contains("Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array.",
                System.Text.Encoding.ASCII.GetString(run.SerialOf(chip.ConsolePort)));
        }
    }
}
