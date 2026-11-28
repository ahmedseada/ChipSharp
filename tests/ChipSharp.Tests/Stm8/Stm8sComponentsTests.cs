using System.Text;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Sdcc;

namespace ChipSharp.Tests.Stm8;

/// <summary>
/// ChipSharp.Components' drivers on the STM8S103 board, unchanged from the Pico's and the Uno's, in chipsim with its
/// devices: Lcd on the I2C bus (PB5, PB4) with the simulated LCD1602, Ds1307 with the simulated DS1307, and a 74HC595's
/// patterns over SPI (PC5, PC6) latched by a pin of the program's.
/// </summary>
public sealed class Stm8sComponentsTests : IDisposable
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
    public void The_Lcd_driver_shows_text_and_numbers_on_a_1602_on_the_boards_I2C_bus()
    {
        var program = Stm8sProgram.Build(_scratch, "Lcd", """
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 13, scl: 12, frequency: 100_000);
            if (Lcd.Open(0, 0x27, columns: 16, rows: 2))
            {
                Lcd.Line(0, "Hello, STM8!");
                Lcd.Write(0, 1, "count");
                for (var count = 0; count <= 2; count++)
                {
                    Lcd.Write(6, 1, count * 250);
                    Timing.DelayMilliseconds(100);
                }

                Lcd.Backlight(false);
            }

            Pin.Output(1);
            Pin.Write(1, Lcd.Open(0, 0x3F, 16, 2)); // nothing there: false at once
            """, PicoBuild.ComponentsAssembly);

        var run = program.Run(1000, "--i2c", "0:0x27=lcd1602");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        Assert.Empty(run.After("LCD 0x27 busy"));
        Assert.Equal(
            [
                "LCD 0x27: |Hello, STM8!    |", "LCD 0x27: |count 0         |",
                "LCD 0x27: |Hello, STM8!    |", "LCD 0x27: |count 250       |",
                "LCD 0x27: |Hello, STM8!    |", "LCD 0x27: |count 500       |",
            ],
            run.After("LCD 0x27: "));
        Assert.Equal(["LCD 0x27 backlight on", "LCD 0x27 backlight off"], run.After("LCD 0x27 backlight"));
        Assert.Equal(["I2C0 0x3f write: no answer"], run.After("I2C0 0x3f"));
        Assert.Equal(["I2C0 100000 Hz"], run.After("I2C0 1"));
        Assert.Equal("low", run.Pins.Last(p => p.Pin == "PA1").State);
        Assert.InRange(program.Memory.Code!.Used, 2000ul, 8 * 1024ul);
    }

    [SdccFact]
    public void The_Ds1307_driver_reads_the_clocks_date_sets_it_and_starts_a_halted_one()
    {
        var program = Stm8sProgram.Build(_scratch, "Ds1307", """
            using System;
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 13, scl: 12, frequency: 100_000);
            Console.Write(Ds1307.Open(0));
            Console.WriteLine(Ds1307.Running);
            Show(Ds1307.Read());
            if (!Ds1307.Running)
            {
                Console.WriteLine(Ds1307.Write(new DateTime(2026, 11, 28, 23, 59, 58)));
            }

            Timing.DelayMilliseconds(2500);
            Console.WriteLine(Ds1307.Running);
            Show(Ds1307.Read());

            static void Show(DateTime time)
            {
                Console.Write(time.Year);
                Console.Write('-');
                Console.Write(time.Month);
                Console.Write('-');
                Console.Write(time.Day);
                Console.Write(' ');
                Console.Write(time.Hour);
                Console.Write(':');
                Console.Write(time.Minute);
                Console.Write(':');
                Console.WriteLine(time.Second);
            }
            """, PicoBuild.ComponentsAssembly);

        var running = program.Run(4000, "--i2c", "0:0x68=ds1307@2026-11-27T18:00:00");
        var halted = program.Run(4000, "--i2c", "0:0x68=ds1307@halted");

        Assert.True(running.ExitCode == 0, running.ToString());
        Assert.Equal("TrueTrue\r\n2026-11-27 18:0:0\r\nTrue\r\n2026-11-27 18:0:2\r\n", Encoding.ASCII.GetString(running.Serial));
        Assert.True(halted.ExitCode == 0, halted.ToString());
        Assert.Equal("TrueFalse\r\n2000-1-1 0:0:0\r\nTrue\r\nTrue\r\n2026-11-29 0:0:0\r\n",
            Encoding.ASCII.GetString(halted.Serial));
        Assert.Contains("DS1307 0x68 set to 2026-11-28 23:59:58 (Saturday, day 7), running", halted.After("DS1307 0x68 set to"));
    }

    [SdccFact]
    public void A_74HC595_takes_a_pattern_over_SPI_and_shows_it_when_its_latch_goes_high()
    {
        var program = Stm8sProgram.Build(_scratch, "ShiftRegister", """
            using System;
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            const int Latch = 20; // PC4
            Pin.Output(Latch);
            Spi.Open(0, sck: 21, tx: 22, rx: -1, frequency: 1_000_000, mode: 0);
            Span<byte> pattern = stackalloc byte[1];
            pattern[0] = SevenSegment.Pattern(4);
            Spi.Write(0, pattern);
            Pin.Write(Latch, true);
            """, PicoBuild.ComponentsAssembly);

        var run = program.Run(200, "--spi", "0=74hc595@PC4");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["SPI0 mode 0, 1000000 Hz"], run.After("SPI0 mode"));
        // 4 is 0x66: B, C, F and G
        Assert.Equal(["SPI0 74HC595 Q1 high", "SPI0 74HC595 Q2 high", "SPI0 74HC595 Q5 high", "SPI0 74HC595 Q6 high"],
            run.After("SPI0 74HC595"));
    }
}
