using System.Text;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// ChipSharp.Components' drivers on the simulated Uno, unchanged from the Pico's: Lcd on the I2C bus (A4, A5) with the
/// simulated LCD (tools/rp2040-sim/devices/lcd1602.mjs), Ds1307 with the simulated DS1307, SevenSegment on the Uno's
/// pins, a 74HC595 over SPI fed SevenSegment's patterns; with what each costs of the Uno's flash and RAM.
/// </summary>
public sealed class UnoComponentsSimulatorTests : IDisposable
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

    /// <summary>
    /// Why the tests of the drivers' string and DateTime methods wait: a string literal and DateTime's constructor (which
    /// throws for a date that does not exist) need the shared runtime's string descriptors and ChipSharp_Runtime_Throw,
    /// which the AVR runtime does not have yet (milestone M7's language coverage adds them).
    /// </summary>
    private const string WaitsForSharedRuntime = "Waits for the string descriptors and ChipSharp_Runtime_Throw in the AVR " +
        "runtime (M7's language coverage): then remove this Skip.";

    [AvrSimulatorFact]
    public async Task The_Lcd_driver_shows_chars_spans_and_numbers_on_a_1602_on_the_Unos_I2C_bus()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "UnoLcd", """
            using System;
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 18, scl: 19, frequency: 100_000);
            if (Lcd.Open(0, 0x27, columns: 16, rows: 2))
            {
                Span<char> text = stackalloc char[16];
                Lcd.Line(0, Ascii(Program.Hello, text));
                Lcd.Write(0, 1, Ascii(Program.Count, text));
                Lcd.Write(10, 0, '!');
                for (var count = 0; count <= 3; count++)
                {
                    Lcd.Write(6, 1, count * 250);
                    Timing.DelayMilliseconds(100);
                }

                Lcd.Backlight(false);
            }

            Pin.Output(13);
            Pin.Write(13, Lcd.Open(0, 0x3F, 16, 2)); // nothing there: false at once

            static ReadOnlySpan<char> Ascii(ReadOnlySpan<byte> bytes, Span<char> chars)
            {
                for (var i = 0; i < bytes.Length; i++)
                {
                    chars[i] = (char)bytes[i];
                }

                return chars[..bytes.Length];
            }

            partial class Program
            {
                public static ReadOnlySpan<byte> Hello => "Hello, Uno"u8;

                public static ReadOnlySpan<byte> Count => "count"u8;
            }
            """, PicoBuild.ComponentsAssembly);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000, "--i2c", "0:0x27=lcd1602");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        Assert.Empty(run.After("LCD 0x27 busy"));
        var screens = run.After("LCD 0x27: ");
        Assert.Equal(
            [
                "LCD 0x27: |Hello, Uno!     |", "LCD 0x27: |count 0         |",
                "LCD 0x27: |Hello, Uno!     |", "LCD 0x27: |count 250       |",
                "LCD 0x27: |Hello, Uno!     |", "LCD 0x27: |count 500       |",
                "LCD 0x27: |Hello, Uno!     |", "LCD 0x27: |count 750       |",
            ],
            screens);
        Assert.Equal(["LCD 0x27 backlight on", "LCD 0x27 backlight off"], run.After("LCD 0x27 backlight"));
        Assert.Equal(["I2C0 0x3f write: no answer"], run.After("I2C0 0x3f"));
        Assert.Equal("low", run.Pins.Last(p => p.Pin == "D13").State);

        // the driver and the bus on the Uno: within its 32 KB of flash and 2 KB of RAM with room to spare
        Assert.InRange(program.Memory.Code!.Used, 2000ul, 12 * 1024ul);
        Assert.InRange(program.Memory.Data!.Used, 1ul, 200ul);
    }

    [AvrSimulatorFact(Skip = WaitsForSharedRuntime)]
    public async Task The_Lcd_driver_shows_strings_on_the_Uno_as_on_the_Pico()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "UnoLcdText", """
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 18, scl: 19, frequency: 400_000);
            if (Lcd.Open(0, 0x27, columns: 16, rows: 2))
            {
                Lcd.Line(0, "Hello, Uno!");
                Lcd.Write(0, 1, "count");
                Lcd.Write(6, 1, 42);
            }
            """, PicoBuild.ComponentsAssembly);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000, "--i2c", "0:0x27=lcd1602");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["LCD 0x27: |Hello, Uno!     |", "LCD 0x27: |count 42        |"], run.After("LCD 0x27: "));
    }

    [AvrSimulatorFact(Skip = WaitsForSharedRuntime)]
    public async Task The_Ds1307_driver_reads_the_clocks_date_sets_it_and_starts_a_halted_one_on_the_Uno()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "UnoDs1307", """
            using System;
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            Say.Open();
            I2c.Open(0, sda: 18, scl: 19, frequency: 100_000);
            Say.Bool(Ds1307.Open(0));
            Say.Bool(Ds1307.Running);
            Say.Line();
            Show(Ds1307.Read());
            if (!Ds1307.Running)
            {
                Say.Bool(Ds1307.Write(new DateTime(2026, 11, 28, 23, 59, 58)));
                Say.Line();
            }

            Timing.DelayMilliseconds(2500);
            Say.Bool(Ds1307.Running);
            Say.Line();
            Show(Ds1307.Read());

            static void Show(DateTime time)
            {
                Say.Number(time.Year);
                Say.Char('-');
                Say.Number(time.Month);
                Say.Char('-');
                Say.Number(time.Day);
                Say.Space();
                Say.Number(time.Hour);
                Say.Char(':');
                Say.Number(time.Minute);
                Say.Char(':');
                Say.Number(time.Second);
                Say.Space();
                Say.Number((int)time.DayOfWeek);
                Say.Line();
            }
            """ + UnoSay.Source, PicoBuild.ComponentsAssembly);

        var running = await AvrSimulator.RunAsync(program.Hex, 4000, "--i2c", "0:0x68=ds1307@2026-11-27T18:00:00");
        var halted = await AvrSimulator.RunAsync(program.Hex, 4000, "--i2c", "0:0x68=ds1307@halted");

        Assert.True(running.ExitCode == 0, running.ToString());
        Assert.Equal("TT\n2026-11-27 18:0:0 5\nT\n2026-11-27 18:0:2 5\n", Encoding.ASCII.GetString(running.Usart0));
        Assert.True(halted.ExitCode == 0, halted.ToString());
        Assert.Equal("TF\n2000-1-1 0:0:0 6\nT\nT\n2026-11-29 0:0:0 0\n", Encoding.ASCII.GetString(halted.Usart0));
        Assert.Contains("DS1307 0x68 set to 2026-11-28 23:59:58 (Saturday, day 7), running",
            halted.After("DS1307 0x68 set to"));
    }

    [AvrSimulatorFact]
    public async Task SevenSegment_lights_the_Unos_pins_and_its_patterns_go_to_a_74HC595_over_SPI()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "UnoSevenSegment", """
            using System;
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            var display = new SevenSegment(firstPin: 2);   // A to G on D2 to D8
            display.Show(7);

            const int Latch = 10;
            Pin.Output(Latch);
            Spi.Open(0, sck: 13, tx: 11, rx: -1, frequency: 1_000_000, mode: 0);
            Span<byte> pattern = stackalloc byte[1];
            pattern[0] = SevenSegment.Pattern(4);
            Spi.Write(0, pattern);
            Pin.Write(Latch, true);
            """, PicoBuild.ComponentsAssembly);

        var run = await AvrSimulator.RunAsync(program.Hex, 200, "--spi", "0=74hc595@D10");

        Assert.True(run.ExitCode == 0, run.ToString());

        // 7 is A, B and C: D2, D3 and D4 high, D5 to D8 low
        string[] segments = ["D2", "D3", "D4", "D5", "D6", "D7", "D8"];
        Assert.Equal(["high", "high", "high", "low", "low", "low", "low"],
            segments.Select(pin => run.Pins.Last(p => p.Pin == pin).State));

        // 4 is 0x66: B, C, F and G
        Assert.Equal(["SPI0 74HC595 Q1 high", "SPI0 74HC595 Q2 high", "SPI0 74HC595 Q5 high", "SPI0 74HC595 Q6 high"],
            run.After("SPI0 74HC595"));
    }
}
