using System.Text;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Stm8;

/// <summary>
/// ChipSharp.Components' drivers on the STM8S103 board, unchanged from the Pico's and the Uno's, in chipsim with its
/// devices: Lcd on the I2C bus (PB5, PB4) with the simulated LCD1602, Ds1307 with the simulated DS1307 (what of it fits
/// in 8 KB), and a 74HC595's patterns over SPI (PC5, PC6) latched by a pin of the program's.
/// </summary>
public sealed class Stm8sComponentsTests : IDisposable
{
    private const string Finished = "the program has finished";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
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

            }
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
        Assert.Equal(["LCD 0x27 backlight on"], run.After("LCD 0x27 backlight"));
        Assert.Equal(["I2C0 100000 Hz"], run.After("I2C0 1"));

        // the driver, the bus and the numbers' text: most of the 8 KB of flash
        Assert.InRange(program.Memory.Code!.Used, 6 * 1024ul, 8 * 1024ul);
        Assert.InRange(program.Memory.Data!.Used, 1ul, 100ul);
    }

    [SdccFact]
    public void The_Ds1307_driver_finds_the_clock_and_tells_a_running_one_from_a_halted_one()
    {
        // its Read and Write, of a DateTime, do not fit: DateTime's code, as the c code generator writes it for SDCC,
        // takes 13 KB with them
        var program = Stm8sProgram.Build(_scratch, "Ds1307", """
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            Pin.Output(1);
            Pin.Output(2);
            I2c.Open(0, sda: 13, scl: 12, frequency: 100_000);
            Pin.Write(1, Ds1307.Open(0));
            Pin.Write(2, Ds1307.Running);
            """, PicoBuild.ComponentsAssembly);

        var running = program.Run(100, "--i2c", "0:0x68=ds1307@2026-11-27T18:00:00");
        var halted = program.Run(100, "--i2c", "0:0x68=ds1307@halted");
        var none = program.Run(100);

        Assert.True(running.ExitCode == 0, running.ToString());
        Assert.Equal(("high", "high"), (running.Pins.Last(p => p.Pin == "PA1").State, running.Pins.Last(p => p.Pin == "PA2").State));
        Assert.Equal(["I2C0 0x68 write 00", "I2C0 0x68 write 00, read 00"], running.After("I2C0 0x68"));
        Assert.Equal(("high", "low"), (halted.Pins.Last(p => p.Pin == "PA1").State, halted.Pins.Last(p => p.Pin == "PA2").State));
        Assert.Equal(["I2C0 0x68 write 00", "I2C0 0x68 write 00, read 80"], halted.After("I2C0 0x68"));
        Assert.Equal(("low", "low"), (none.Pins.Last(p => p.Pin == "PA1").State, none.Pins.Last(p => p.Pin == "PA2").State));
        Assert.Equal(["I2C0 0x68 write: no answer"], none.After("I2C0 0x68"));
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
