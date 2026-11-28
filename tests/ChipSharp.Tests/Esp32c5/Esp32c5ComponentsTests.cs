using System.Globalization;
using System.Text;
using ChipSharp.Build;
using ChipSharp.Simulators;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// ChipSharp.Components' drivers on the simulated ESP32-C5-DevKitC-1, unchanged from the Pico's, the Uno's and the
/// STM8's but for the pins: Lcd on I2C0 (SDA GPIO4, SCL GPIO5) with the simulated 16x2 LCD at 0x27, Ds1307 with the
/// simulated DS1307, SevenSegment on GPIO4-GPIO10 and its patterns to a 74HC595 over SPI (SCK GPIO0, MOSI GPIO1, latch
/// GPIO6), an analog input and a PWM output, in chipsim's esp32c5.
/// </summary>
public sealed class Esp32c5ComponentsTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [PicoFact]
    public void The_Lcd_driver_shows_chars_and_numbers_on_a_1602_on_the_C5s_I2C_bus()
    {
        var image = Build("C5Lcd", """
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 4, scl: 5, frequency: 100_000);
            if (Lcd.Open(0, 0x27, columns: 16, rows: 2))
            {
                Lcd.Line(0, "Hello, C5!");
                Lcd.Write(0, 1, "count");
                for (var count = 0; count <= 3; count++)
                {
                    Lcd.Write(6, 1, count * 250);
                    Timing.DelayMilliseconds(100);
                }
            }

            Pin.Output(6);
            Pin.Write(6, Lcd.Open(0, 0x3F, 16, 2)); // nothing there: false at once
            """);

        var run = Run(image, 1000, "--i2c", "0:0x27=lcd1602");

        Assert.Contains("the program has finished", run.Stopped);
        Assert.Equal(
            [
                "LCD 0x27: |Hello, C5!      |", "LCD 0x27: |count 0         |",
                "LCD 0x27: |Hello, C5!      |", "LCD 0x27: |count 250       |",
                "LCD 0x27: |Hello, C5!      |", "LCD 0x27: |count 500       |",
                "LCD 0x27: |Hello, C5!      |", "LCD 0x27: |count 750       |",
            ],
            run.After("LCD 0x27: "));
        Assert.Equal(["I2C0 0x3f write: no answer"], run.After("I2C0 0x3f"));
        Assert.Contains("I2C0 100000 Hz", run.After("I2C0 100000"));
    }

    [PicoFact]
    public void The_Ds1307_driver_reads_the_clocks_date_sets_it_and_starts_a_halted_one()
    {
        var image = Build("C5Ds1307", """
            using System;
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 4, scl: 5, frequency: 400_000);
            Console.WriteLine(Ds1307.Open(0) ? "open" : "no clock");
            Console.WriteLine(Ds1307.Running ? "running" : "halted");
            Show(Ds1307.Read());
            if (!Ds1307.Running)
            {
                Ds1307.Write(new DateTime(2026, 11, 28, 23, 59, 58));
            }

            Timing.DelayMilliseconds(2500);
            Show(Ds1307.Read());

            static void Show(DateTime time) => Console.WriteLine($"{time.Year}-{time.Month}-{time.Day} {time.Hour}:{time.Minute}:{time.Second}");
            """);

        var running = Run(image, 4000, "--i2c", "0:0x68=ds1307@2026-11-27T18:00:00");
        var halted = Run(image, 4000, "--i2c", "0:0x68=ds1307@halted");

        Assert.Equal("open\r\nrunning\r\n2026-11-27 18:0:0\r\n2026-11-27 18:0:2\r\n", Text(running));
        Assert.Equal("open\r\nhalted\r\n2000-1-1 0:0:0\r\n2026-11-29 0:0:0\r\n", Text(halted));
    }

    [PicoFact]
    public void SevenSegment_lights_the_pins_and_its_patterns_go_to_a_74HC595_over_SPI()
    {
        var image = Build("C5SevenSegment", """
            using System;
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            var display = new SevenSegment(firstPin: 4);   // A to G on GPIO4 to GPIO10
            display.Show(7);

            const int Latch = 23;
            Pin.Output(Latch);
            Spi.Open(0, sck: 0, tx: 1, rx: -1, frequency: 1_000_000, mode: 0);
            Span<byte> pattern = stackalloc byte[1];
            pattern[0] = SevenSegment.Pattern(4);
            Spi.Write(0, pattern);
            Pin.Write(Latch, true);
            """);

        var run = Run(image, 200, "--spi", "0=74hc595@GPIO23");

        // 7 is A, B and C: GPIO4, 5 and 6 high, GPIO7 to GPIO10 low
        string[] segments = ["GPIO4", "GPIO5", "GPIO6", "GPIO7", "GPIO8", "GPIO9", "GPIO10"];
        Assert.Equal(["high", "high", "high", "low", "low", "low", "low"],
            segments.Select(pin => run.Pins.Last(p => p.Pin == pin).State));

        // 4 is 0x66: B, C, F and G
        Assert.Equal(["SPI0 74HC595 Q1 high", "SPI0 74HC595 Q2 high", "SPI0 74HC595 Q5 high", "SPI0 74HC595 Q6 high"],
            run.After("SPI0 74HC595"));
    }

    [PicoFact]
    public void The_ADC_reads_the_voltage_given_and_the_temperature_and_PWM_runs_at_the_duty_asked_for()
    {
        var image = Build("C5Analog", """
            using System;
            using ChipSharp.Hardware;

            Adc.Input(5);
            Console.WriteLine(Adc.Read(5));
            Console.WriteLine((int)(Adc.Volts(5) * 100));
            var temperature = Adc.Temperature();
            Console.WriteLine(temperature > 0 && temperature < 60 ? "temperature ok" : "temperature wrong");

            Pwm.Output(4, 2000);
            Pwm.Duty(4, 0.25);
            Timing.DelayMilliseconds(20);
            Pwm.Duty(4, 0.75);
            Timing.DelayMilliseconds(20);
            """);

        var run = Run(image, 200, "--adc", "GPIO5=1.65V");

        Assert.Equal("2048\r\n165\r\ntemperature ok\r\n", Text(run));
        var periods = run.After("GPIO4 PWM");
        Assert.Contains(periods, line => line.Contains("2000.00 Hz, duty 25.00%", StringComparison.Ordinal));
        Assert.Contains(periods, line => line.Contains("2000.00 Hz, duty 75.00%", StringComparison.Ordinal));
    }

    [PicoFact]
    public void The_check_refuses_pins_the_board_does_not_leave_a_program_and_clocks_the_chip_does_not_make()
    {
        var pins = Assert.ThrowsAny<Exception>(() => Build("C5BadPins", """
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 16, scl: 5, frequency: 100_000);   // GPIO16 is the module's flash
            Adc.Input(8);                                       // no ADC channel on GPIO8
            Pwm.Output(15, 1000);                               // GPIO15 is the PSRAM's
            Spi.Open(0, sck: 0, tx: 1, rx: 24, frequency: 40_000_000, mode: 0);
            """));

        Assert.Contains("GPIO 16 is not an SDA pin of I2C bus 0", pins.Message, StringComparison.Ordinal);
        Assert.Contains("GPIO 8 cannot be an analog input", pins.Message, StringComparison.Ordinal);
        Assert.Contains("GPIO 15 cannot be a PWM output", pins.Message, StringComparison.Ordinal);
        Assert.Contains("an SPI clock of 40000000 Hz", pins.Message, StringComparison.Ordinal);
    }

    /// <summary>Builds C# (with ChipSharp.Components) for the board; the image's path.</summary>
    private string Build(string name, string source)
    {
        Directory.CreateDirectory(_scratch);
        var components = PicoBuild.ComponentsAssembly;
        var dll = Path.Combine(_scratch, name + ".dll");
        File.WriteAllBytes(dll, CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly, components], name));
        File.Copy(components, Path.Combine(_scratch, Path.GetFileName(components)), overwrite: true);
        return ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = "esp32c5-devkitc",
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = Path.Combine(_scratch, name),
            UseCache = false,
        }).OutputPath;
    }

    private static SimulationResult Run(string image, double milliseconds, params string[] options)
    {
        var run = Simulation.Run(SimulationOptions.Parse("esp32c5",
            [image, "--ms", milliseconds.ToString(CultureInfo.InvariantCulture), .. options]));
        Assert.True(run.ExitCode == 0, run.ToString());
        return run;
    }

    private static string Text(SimulationResult run) => Encoding.UTF8.GetString(run.SerialOf("USB"));
}
