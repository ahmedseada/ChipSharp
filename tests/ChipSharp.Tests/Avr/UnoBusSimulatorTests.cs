using System.Text;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// ChipSharp.Hardware's I2c and Spi on the simulated Uno, the same C# as on the Pico, with the Pico simulator's devices
/// (tools/rp2040-sim/devices, which tools/avr-sim shares): writes, reads and reads after a repeated start on the TWI (A4,
/// A5), bytes from flash, a device that is not there or holds the clock low, the clocks the dividers make; the SPI
/// controller (D13, D11, D12) in each mode, a chip select, a 74HC595; and the mistakes that stop the program.
/// </summary>
public sealed class UnoBusSimulatorTests : IDisposable
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
    public async Task I2c_writes_reads_and_reads_after_a_repeated_start_and_a_missing_device_says_no_at_once()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "I2cTransfers", """
            using System;
            using ChipSharp.Hardware;

            Say.Open();
            I2c.Open(0, sda: 18, scl: 19, frequency: 100_000);
            Span<byte> register = stackalloc byte[1];
            Span<byte> value = stackalloc byte[1];

            // the MPU-6050's WHO_AM_I, in one transfer: its number, a repeated start, its value
            register[0] = 0x75;
            Say.Bool(I2c.WriteRead(0, 0x68, register, value));
            Say.Space();
            Say.Hex(value[0]);
            Say.Line();

            // its ACCEL_CONFIG written from flash (a constant span), then read back in two transfers
            var set = Program.AccelConfig;
            register[0] = 0x1C;
            value[0] = 0;
            Say.Bool(I2c.Write(0, 0x68, set));
            Say.Bool(I2c.Write(0, 0x68, register));
            Say.Bool(I2c.Read(0, 0x68, value));
            Say.Space();
            Say.Number(value[0]);
            Say.Line();

            // nothing at 0x50; empty spans send nothing
            Say.Bool(I2c.WriteRead(0, 0x50, register, value));
            Say.Bool(I2c.Write(0, 0x50, set));
            Say.Bool(I2c.Read(0, 0x50, value));
            Say.Bool(I2c.Probe(0, 0x68));
            Say.Bool(I2c.Probe(0, 0x69));
            Say.Bool(I2c.Write(0, 0x68, ReadOnlySpan<byte>.Empty));
            Say.Bool(I2c.Read(0, 0x68, Span<byte>.Empty));
            Say.Line();

            // the BMP280's chip id at 400 kHz
            I2c.Open(0, 18, 19, 400_000);
            register[0] = 0xD0;
            Say.Bool(I2c.WriteRead(0, 0x76, register, value));
            Say.Space();
            Say.Hex(value[0]);
            Say.Line();

            // what answers; a scan stops when its span is full
            Span<byte> found = stackalloc byte[4];
            var count = I2c.Scan(0, found);
            Say.Number(count);
            for (var i = 0; i < count; i++)
            {
                Say.Space();
                Say.Hex(found[i]);
            }

            Say.Space();
            Say.Number(I2c.Scan(0, found[..1]));
            Say.Line();

            partial class Program
            {
                public static ReadOnlySpan<byte> AccelConfig => [0x1C, 0x08];
            }
            """ + UnoSay.Source);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000, "--i2c", "0:0x68=mpu6050", "--i2c", "0:0x76=bmp280");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        Assert.Equal("T 68\nTTT 8\nFFFTFFF\nT 58\n2 68 76 1\n", Encoding.ASCII.GetString(run.Usart0));

        // the transfers on the bus, each from its start to its stop, after the clocks TWBR makes: 72 and 12, exactly
        var transfers = run.After("I2C0");
        Assert.Equal(
            [
                "I2C0 100000 Hz", "I2C0 0x68 write 75, read 68", "I2C0 0x68 write 1c 08", "I2C0 0x68 write 1c",
                "I2C0 0x68 read 08", "I2C0 0x50 write: no answer", "I2C0 0x50 write: no answer", "I2C0 0x50 read: no answer",
                "I2C0 0x68 read 00", "I2C0 0x69 read: no answer", "I2C0 400000 Hz", "I2C0 0x76 write d0, read 58",
            ],
            transfers.Take(12));

        // the scans try 0x08 to 0x77, and the second stops at the first that answers
        var scans = transfers.Skip(12).ToList();
        Assert.Equal(112 + (0x68 - 0x08 + 1), scans.Count);
        Assert.Equal(["I2C0 0x68 read 00", "I2C0 0x76 read 00", "I2C0 0x68 read 00"],
            scans.Where(line => !line.EndsWith("no answer", StringComparison.Ordinal)));

        // SDA and SCL are the TWI's, with the pins' pull-ups on
        Assert.Contains(run.Pins, p => p.Pin == "A4" && p.State == "input pull-up");
        Assert.Contains(run.Pins, p => p.Pin == "A5" && p.State == "input pull-up");
    }

    [AvrSimulatorFact]
    public async Task A_device_that_holds_the_clock_low_makes_the_transfer_give_up_after_its_time_limit()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "Stuck", """
            using System;
            using ChipSharp.Hardware;

            Say.Open();
            I2c.Open(0, sda: 18, scl: 19, frequency: 100_000);
            Span<byte> data = stackalloc byte[2];
            Say.Bool(I2c.Write(0, 0x50, data));
            Say.Bool(I2c.Read(0, 0x50, data));
            Say.Line();
            """ + UnoSay.Source);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000, "--i2c", "0:0x50=stuck");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("FF\n", Encoding.ASCII.GetString(run.Usart0));
        Assert.Equal(["I2C0 0x50 write: the stuck device holds the clock low"], run.After("I2C0 0x50"));

        // each gives up after a byte's limit at 100 kHz, 1.5 ms (the second finds the TWI still held)
        Assert.InRange(run.Time("USART0: FF") - run.Time("I2C0 0x50 write"), 3.0, 3.6);
        Assert.Contains(Finished, run.Stopped);
    }

    [AvrSimulatorFact]
    public async Task Spi_transfers_in_each_mode_at_the_clocks_asked_for_with_and_without_a_chip_select()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "SpiTransfers", """
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Say.Open();
                    Span<byte> sent = stackalloc byte[3];
                    sent[0] = 0x12;
                    sent[1] = 0xA5;
                    sent[2] = 0xFF;
                    Span<byte> received = stackalloc byte[3];

                    // RX wired to TX while D9 is low: what comes back is what was sent; at 1 to 4 MHz asked, the
                    // fastest 16 MHz / 2^n at or below each
                    Pin.Output(9);
                    Pin.Write(9, false);
                    for (var mode = 0; mode < 4; mode++)
                    {
                        Spi.Open(0, sck: 13, tx: 11, rx: 12, frequency: 1_000_000 * (mode + 1), mode: mode);
                        Spi.Transfer(0, sent, received);
                        Show(received);
                    }

                    // reading sends zeros; a transfer into the bytes it sends; constant bytes, from flash
                    Spi.Read(0, received);
                    Show(received);
                    Spi.Write(0, sent);
                    Spi.Transfer(0, sent, sent);
                    Show(sent);
                    Spi.Transfer(0, [0x01, 0x02, 0x03], received);
                    Show(received);

                    // the device not selected: nothing comes back; at the slowest clock, 125 kHz
                    Pin.Write(9, true);
                    Spi.Open(0, 13, 11, 12, 125_000, 0);
                    Spi.Transfer(0, sent, received);
                    Show(received);
                }

                static void Show(ReadOnlySpan<byte> bytes)
                {
                    Say.Hex(bytes[0]);
                    Say.Space();
                    Say.Hex(bytes[1]);
                    Say.Space();
                    Say.Hex(bytes[2]);
                    Say.Line();
                }
            }
            """ + UnoSay.Source);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000, "--spi", "0=loopback@D9");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("""
            12 A5 FF
            12 A5 FF
            12 A5 FF
            12 A5 FF
            00 00 00
            12 A5 FF
            01 02 03
            00 00 00

            """.ReplaceLineEndings("\n"), Encoding.ASCII.GetString(run.Usart0));
        Assert.Equal(
            ["SPI0 mode 0, 1000000 Hz", "SPI0 mode 1, 2000000 Hz", "SPI0 mode 2, 2000000 Hz", "SPI0 mode 3, 4000000 Hz",
                "SPI0 mode 0, 125000 Hz"],
            run.After("SPI0"));

        // D10 (SS) an output, high, so that the controller stays the bus's; SCK and TX outputs
        Assert.Contains(run.Pins, p => p.Pin == "D10" && p.State == "high");
        Assert.Contains(run.Pins, p => p.Pin == "D13" && p.State == "low");
        Assert.Contains(run.Pins, p => p.Pin == "D11" && p.State == "low");
    }

    [AvrSimulatorFact]
    public async Task A_74HC595_on_the_SPI_bus_shows_each_byte_when_its_latch_goes_high()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "ShiftRegister", """
            using System;
            using ChipSharp.Hardware;

            const int Latch = 10;
            Pin.Output(Latch);
            Spi.Open(0, sck: 13, tx: 11, rx: -1, frequency: 8_000_000, mode: 0);
            Span<byte> bits = stackalloc byte[1];
            for (var i = 0; i < 3; i++)
            {
                bits[0] = (byte)(1 << i | 0x80);
                Spi.Write(0, bits);
                Pin.Write(Latch, true);
                Pin.Write(Latch, false);
                Timing.DelayMilliseconds(10);
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000, "--spi", "0=74hc595@10");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(
            ["SPI0 mode 0, 8000000 Hz", "SPI0 74HC595 Q0 high", "SPI0 74HC595 Q7 high", "SPI0 74HC595 Q0 low",
                "SPI0 74HC595 Q1 high", "SPI0 74HC595 Q1 low", "SPI0 74HC595 Q2 high"],
            run.After("SPI0"));
        Assert.InRange(run.Time("SPI0 74HC595 Q2 high") - run.Time("SPI0 74HC595 Q1 high"), 10.0, 10.1);
    }

    public static TheoryData<string, string> Mistakes() => new()
    {
        {
            "I2c.Open(0, Value(4), 19, 100_000);",
            "System.ArgumentException: Pin 4 is not the SDA pin of I2C0: on the Arduino Uno it is pin 18 (A4). " +
                "(Parameter 'sda')"
        },
        {
            "I2c.Open(Value(1), 18, 19, 100_000);",
            "System.ArgumentException: I2C bus 1 does not exist: the ATmega328P has bus 0 (I2C0, its TWI, on A4 and A5). " +
                "(Parameter 'bus')"
        },
        {
            "I2c.Probe(0, 0x10);",
            "System.InvalidOperationException: I2C0 is not open: call I2c.Open(0, 18, 19, frequency) first (SDA on A4, SCL " +
                "on A5)."
        },
        {
            "I2c.Open(0, 18, 19, Value(1_000_000));",
            "System.ArgumentException: An I2C clock of 1000000 Hz is not one the ATmega328P makes: it is from 490 to 400000 " +
                "Hz (100000 is standard mode, 400000 fast mode). (Parameter 'frequency')"
        },
        {
            "I2c.Open(0, 18, 19, 100_000); I2c.Probe(0, Value(200));",
            "System.ArgumentException: I2C address 200 does not exist: a 7-bit address is from 0 to 127 (0x7F). " +
                "(Parameter 'address')"
        },
        {
            "Spi.Open(0, 13, Value(4), -1, 1_000_000, 0);",
            "System.ArgumentException: Pin 4 is not the TX pin of SPI0: on the Arduino Uno it is pin 11 (D11, MOSI), or -1 " +
                "for none. (Parameter 'tx')"
        },
        {
            "Spi.Open(0, 13, 11, 12, Value(100_000), 0);",
            "System.ArgumentException: An SPI clock of 100000 Hz is not one the ATmega328P makes: it is 16 MHz / 2 to 128, " +
                "from 125000 to 8000000 Hz. (Parameter 'frequency')"
        },
        {
            "Spi.Open(0, 13, 11, 12, 1_000_000, Value(4));",
            "System.ArgumentException: SPI mode 4 does not exist: the mode is 0, 1, 2 or 3. (Parameter 'mode')"
        },
        {
            "Spi.Open(0, 13, 11, 12, 1_000_000, 0); Spi.Transfer(0, stackalloc byte[3], stackalloc byte[2]);",
            "System.ArgumentException: Spi.Transfer receives a byte for each byte it sends: write has 3 bytes and read 2. " +
                "(Parameter 'read')"
        },
        {
            "Spi.Write(0, stackalloc byte[1]);",
            "System.InvalidOperationException: SPI0 is not open: call Spi.Open(0, 13, 11, 12, frequency, mode) first."
        },
    };

    [AvrSimulatorTheory]
    [MemberData(nameof(Mistakes))]
    public async Task A_mistake_the_chip_finds_stops_the_program_with_a_message(string code, string message)
    {
        // Value(n) is n, known only when the program runs: chipsharp check holds constants to the target's tables itself
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
