using System.Text;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// ChipSharp.Hardware's I2c and Spi on the simulated Pico, with run.mjs's simulated devices (tools/rp2040-sim/devices):
/// writes, reads and reads after a repeated start, a device that is not there or holds the clock low, both buses of
/// each kind, the SPI modes and clocks, a chip select, and the mistakes that stop the program.
/// </summary>
public sealed class BusSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [SimulatorFact]
    public async Task I2c_writes_reads_and_reads_after_a_repeated_start_and_a_missing_device_says_no_at_once()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 4, scl: 5, frequency: 100_000);
            I2c.Open(1, sda: 26, scl: 27, frequency: 400_000);
            Span<byte> register = stackalloc byte[1];
            Span<byte> value = stackalloc byte[1];

            // the MPU-6050's WHO_AM_I, in one transfer: its number, a repeated start, its value
            register[0] = 0x75;
            Console.WriteLine($"{I2c.WriteRead(0, 0x68, register, value)} 0x{value[0]:X2}");

            // its ACCEL_CONFIG written, then read back in two transfers
            Span<byte> set = stackalloc byte[2];
            set[0] = 0x1C;
            set[1] = 0x08;
            register[0] = 0x1C;
            value[0] = 0;
            Console.WriteLine($"{I2c.Write(0, 0x68, set)} {I2c.Write(0, 0x68, register)} {I2c.Read(0, 0x68, value)} {value[0]}");

            // nothing at 0x50, nor at 0x68 on the other bus; empty spans send nothing
            Console.WriteLine($"{I2c.WriteRead(0, 0x50, register, value)} {I2c.Write(0, 0x50, set)} {I2c.Read(0, 0x50, value)}");
            Console.WriteLine($"{I2c.Probe(0, 0x68)} {I2c.Probe(0, 0x69)} {I2c.Probe(1, 0x68)} {I2c.Probe(1, 0x76)}");
            Console.WriteLine($"{I2c.Write(0, 0x68, ReadOnlySpan<byte>.Empty)} {I2c.Read(0, 0x68, Span<byte>.Empty)}");

            // the BMP280's chip id on bus 1
            register[0] = 0xD0;
            Console.WriteLine($"{I2c.WriteRead(1, 0x76, register, value)} 0x{value[0]:X2}");

            // what answers on each bus
            Span<byte> found = stackalloc byte[4];
            for (var bus = 0; bus < 2; bus++)
            {
                var count = I2c.Scan(bus, found);
                Console.Write($"I2C{bus}: {count}");
                for (var i = 0; i < count; i++)
                {
                    Console.Write($" 0x{found[i]:X2}");
                }

                Console.WriteLine();
            }

            // a scan stops when its span is full
            Console.WriteLine(I2c.Scan(1, found[..0]));
            """, "I2cTransfers", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000, "--i2c", "0:0x68=mpu6050", "--i2c", "1:0x76=bmp280");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.EndsWith("the program has finished: the core sleeps and nothing is scheduled to wake it", run.Stopped);
        Assert.Equal("""
            True 0x68
            True True True 8
            False False False
            True False False True
            False False
            True 0x58
            I2C0: 1 0x68
            I2C1: 1 0x76
            0

            """.ReplaceLineEndings("\r\n"), Encoding.UTF8.GetString(run.Uart0));

        // the transfers on the buses, each from its start to its stop: the first two after the clocks the program set
        var transfers = Lines(run, "I2C");
        Assert.Equal(
            [
                "I2C0 100000 Hz", "I2C0 0x68 write 75, read 68", "I2C0 0x68 write 1c 08", "I2C0 0x68 write 1c",
                "I2C0 0x68 read 08", "I2C0 0x50 write: no answer", "I2C0 0x50 write: no answer", "I2C0 0x50 read: no answer",
                "I2C0 0x68 read 00", "I2C0 0x69 read: no answer", "I2C1 399361 Hz", "I2C1 0x68 read: no answer",
                "I2C1 0x76 read 00", "I2C1 0x76 write d0, read 58",
            ],
            transfers.Take(14));

        // the scans try 0x08 to 0x77 on each bus, and only the device's own address answers
        var scans = transfers.Skip(14).ToList();
        Assert.Equal(2 * 112, scans.Count);
        Assert.Equal(["I2C0 0x68 read 00", "I2C1 0x76 read 00"], scans.Where(line => !line.EndsWith("no answer")));
        Assert.Equal("I2C0 0x08 read: no answer", scans[0]);
        Assert.Equal("I2C1 0x77 read: no answer", scans[^1]);

        // the pins are the controllers' with their pull-ups on (and a missing device costs no time in the simulator)
        Assert.All(new[] { 4, 5, 26, 27 }, pin => Assert.Contains(run.Pins, p => p.Pin == pin && p.State == "input pull-up"));
    }

    [SimulatorFact]
    public async Task A_device_that_holds_the_clock_low_makes_the_transfer_give_up_after_its_time_limit()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 4, scl: 5, frequency: 100_000);
            Span<byte> data = stackalloc byte[2];
            Console.WriteLine(I2c.Write(0, 0x50, data));
            Console.WriteLine(I2c.Read(0, 0x50, data));
            Console.WriteLine("done");
            """, "Stuck", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000, "--i2c", "0:0x50=stuck");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("False\r\nFalse\r\ndone\r\n", Encoding.UTF8.GetString(run.Uart0));
        Assert.Equal("I2C0 0x50 write: the stuck device holds the clock low", Lines(run, "I2C0 0x50").Single());

        // each gives up after a byte's limit at 100 kHz, 1.5 ms (the second finds the bus still held)
        var finished = Time(run, "UART0: done");
        Assert.InRange(finished - Time(run, "I2C0 0x50 write"), 3.0, 3.5);
        Assert.EndsWith("the program has finished: the core sleeps and nothing is scheduled to wake it", run.Stopped);
    }

    [SimulatorFact]
    public async Task Spi_transfers_in_each_mode_at_the_clocks_asked_for_with_and_without_a_chip_select()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Span<byte> sent = stackalloc byte[3];
                    sent[0] = 0x12;
                    sent[1] = 0xA5;
                    sent[2] = 0xFF;
                    Span<byte> received = stackalloc byte[3];

                    // SPI0 with RX wired to TX: what comes back is what was sent
                    for (var mode = 0; mode < 4; mode++)
                    {
                        Spi.Open(0, sck: 18, tx: 19, rx: 16, frequency: 1_000_000 * (mode + 1), mode: mode);
                        Spi.Transfer(0, sent, received);
                        Show(received);
                    }

                    // reading sends zeros; a transfer into the bytes it sends
                    Spi.Read(0, received);
                    Show(received);
                    Spi.Write(0, sent);
                    Spi.Transfer(0, sent, sent);
                    Show(sent);

                    // SPI1, its device selected by GPIO 13 low
                    Pin.Output(13);
                    Pin.Write(13, true);
                    Spi.Open(1, sck: 10, tx: 11, rx: 12, frequency: 8_000_000, mode: 0);
                    Spi.Transfer(1, sent, received);
                    Show(received);
                    Pin.Write(13, false);
                    Spi.Transfer(1, sent, received);
                    Pin.Write(13, true);
                    Show(received);

                    // more bytes than the FIFO holds
                    Span<byte> many = stackalloc byte[20];
                    for (var i = 0; i < many.Length; i++)
                    {
                        many[i] = (byte)(i * 7);
                    }

                    Span<byte> back = stackalloc byte[20];
                    Spi.Transfer(0, many, back);
                    Console.WriteLine(back.SequenceEqual(many));
                }

                static void Show(ReadOnlySpan<byte> bytes) => Console.WriteLine($"{bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2}");
            }
            """, "SpiTransfers", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000, "--spi", "0=loopback", "--spi", "1=loopback@13");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("""
            12 A5 FF
            12 A5 FF
            12 A5 FF
            12 A5 FF
            00 00 00
            12 A5 FF
            00 00 00
            12 A5 FF
            True

            """.ReplaceLineEndings("\r\n"), Encoding.UTF8.GetString(run.Uart0));

        // the largest clock at or below each frequency that 125 MHz / (prescale x divisor) makes, as pico-sdk finds it
        Assert.Equal(
            ["SPI0 mode 0, 992063 Hz", "SPI0 mode 1, 1953125 Hz", "SPI0 mode 2, 2976190 Hz", "SPI0 mode 3, 3906250 Hz",
                "SPI1 mode 0, 7812500 Hz"],
            Lines(run, "SPI"));
    }

    [SimulatorFact]
    public async Task On_a_Pico_without_a_heap_a_BMP280_driver_works_out_the_datasheets_temperature_and_pressure()
    {
        var noHeap = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");
        var program = PicoBuild.Build("""
            using System;
            using System.Buffers.Binary;
            using ChipSharp.Hardware;

            class Program
            {
                const int Bus = 0;
                const int Address = 0x76;

                static void Main()
                {
                    I2c.Open(Bus, sda: 8, scl: 9, frequency: 400_000);
                    Span<byte> register = stackalloc byte[1];
                    Span<byte> id = stackalloc byte[1];
                    register[0] = 0xD0;
                    I2c.WriteRead(Bus, Address, register, id);
                    Console.WriteLine($"chip id 0x{id[0]:X2}");

                    // before a measurement: 0x80000
                    Span<byte> raw = stackalloc byte[6];
                    register[0] = 0xF7;
                    I2c.WriteRead(Bus, Address, register, raw);
                    Console.WriteLine($"asleep: {Raw(raw[3..])}");

                    Span<byte> calibration = stackalloc byte[24];
                    register[0] = 0x88;
                    I2c.WriteRead(Bus, Address, register, calibration);

                    // normal mode, both measured once (ctrl_meas: osrs_t 1, osrs_p 1, mode 3)
                    Span<byte> control = stackalloc byte[2];
                    control[0] = 0xF4;
                    control[1] = 0x27;
                    I2c.Write(Bus, Address, control);
                    register[0] = 0xF7;
                    I2c.WriteRead(Bus, Address, register, raw);
                    int adcP = Raw(raw);
                    int adcT = Raw(raw[3..]);
                    Console.WriteLine($"adc_T {adcT}, adc_P {adcP}");

                    int fine = Fine(adcT, calibration);
                    int hundredths = (fine * 5 + 128) >> 8;
                    Console.WriteLine($"t_fine {fine}, temperature {hundredths / 100}.{hundredths % 100:D2} C");
                    Console.WriteLine($"pressure {Pressure32(adcP, fine, calibration)} Pa (32-bit)");
                    long q24 = Pressure64(adcP, fine, calibration);
                    Console.WriteLine($"pressure {q24} / 256 = {q24 / 256}.{q24 % 256 * 100 / 256:D2} Pa (64-bit)");
                }

                // 20 bits, most significant byte first
                static int Raw(ReadOnlySpan<byte> bytes) => (bytes[0] << 12) | (bytes[1] << 4) | (bytes[2] >> 4);

                static int T(ReadOnlySpan<byte> c, int i) => i == 1
                    ? BinaryPrimitives.ReadUInt16LittleEndian(c)
                    : BinaryPrimitives.ReadInt16LittleEndian(c[(2 * i - 2)..]);

                static int P(ReadOnlySpan<byte> c, int i) => i == 1
                    ? BinaryPrimitives.ReadUInt16LittleEndian(c[6..])
                    : BinaryPrimitives.ReadInt16LittleEndian(c[(6 + 2 * i - 2)..]);

                // the datasheet's bmp280_compensate_T_int32, up to t_fine
                static int Fine(int adcT, ReadOnlySpan<byte> c)
                {
                    int var1 = (((adcT >> 3) - (T(c, 1) << 1)) * T(c, 2)) >> 11;
                    int var2 = (((((adcT >> 4) - T(c, 1)) * ((adcT >> 4) - T(c, 1))) >> 12) * T(c, 3)) >> 14;
                    return var1 + var2;
                }

                // bmp280_compensate_P_int32
                static uint Pressure32(int adcP, int fine, ReadOnlySpan<byte> c)
                {
                    int var1 = (fine >> 1) - 64000;
                    int var2 = (((var1 >> 2) * (var1 >> 2)) >> 11) * P(c, 6);
                    var2 = var2 + ((var1 * P(c, 5)) << 1);
                    var2 = (var2 >> 2) + (P(c, 4) << 16);
                    var1 = (((P(c, 3) * (((var1 >> 2) * (var1 >> 2)) >> 13)) >> 3) + ((P(c, 2) * var1) >> 1)) >> 18;
                    var1 = ((32768 + var1) * P(c, 1)) >> 15;
                    if (var1 == 0)
                    {
                        return 0;
                    }

                    uint p = ((uint)(1048576 - adcP) - (uint)(var2 >> 12)) * 3125;
                    p = p < 0x80000000 ? (p << 1) / (uint)var1 : p / (uint)var1 * 2;
                    var1 = (P(c, 9) * (int)(((p >> 3) * (p >> 3)) >> 13)) >> 12;
                    var2 = ((int)(p >> 2) * P(c, 8)) >> 13;
                    return (uint)((int)p + ((var1 + var2 + P(c, 7)) >> 4));
                }

                // bmp280_compensate_P_int64: Pa in Q24.8
                static long Pressure64(int adcP, int fine, ReadOnlySpan<byte> c)
                {
                    long var1 = (long)fine - 128000;
                    long var2 = var1 * var1 * P(c, 6);
                    var2 = var2 + ((var1 * P(c, 5)) << 17);
                    var2 = var2 + ((long)P(c, 4) << 35);
                    var1 = ((var1 * var1 * P(c, 3)) >> 8) + ((var1 * P(c, 2)) << 12);
                    var1 = ((1L << 47) + var1) * P(c, 1) >> 33;
                    if (var1 == 0)
                    {
                        return 0;
                    }

                    long p = 1048576 - adcP;
                    p = ((p << 31) - var2) * 3125 / var1;
                    var1 = (P(c, 9) * (p >> 13) * (p >> 13)) >> 25;
                    var2 = (P(c, 8) * p) >> 19;
                    return ((p + var1 + var2) >> 8) + ((long)P(c, 7) << 4);
                }
            }
            """, "Bmp280", _scratch, noHeap);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000, "--i2c", "0:0x76=bmp280");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("""
            chip id 0x58
            asleep: 524288
            adc_T 519888, adc_P 415148
            t_fine 128422, temperature 25.08 C
            pressure 100656 Pa (32-bit)
            pressure 25767233 / 256 = 100653.25 Pa (64-bit)

            """.ReplaceLineEndings("\r\n"), Encoding.UTF8.GetString(run.Uart0));
        Assert.DoesNotContain("chipsharp_new_object", ElfFile.Read(program.Elf).Symbols.Keys);
    }

    public static TheoryData<string, string> Mistakes() => new()
    {
        {
            "I2c.Open(0, Value(6), 5, 100_000);",
            "System.ArgumentException: GPIO 6 is not an SDA pin of I2C0: its SDA pins are GPIO 0, 4, 8, 12, 16, 20, 24 " +
                "and 28. (Parameter 'sda')"
        },
        {
            "I2c.Open(Value(2), 4, 5, 100_000);",
            "System.ArgumentException: I2C bus 2 does not exist: the RP2040 has bus 0 and bus 1 (I2C0 and I2C1). " +
                "(Parameter 'bus')"
        },
        {
            "I2c.Probe(1, 0x10);",
            "System.InvalidOperationException: I2C1 is not open: call I2c.Open(1, sda, scl, frequency) first."
        },
        {
            "I2c.Open(0, 4, 5, Value(5_000_000));",
            "System.ArgumentException: An I2C clock of 5000000 Hz is not one the RP2040 makes: it is from 2000 to 1000000 " +
                "Hz (100000 is standard mode, 400000 fast mode, 1000000 fast mode plus). (Parameter 'frequency')"
        },
        {
            "I2c.Open(0, 4, 5, 100_000); I2c.Probe(0, Value(200));",
            "System.ArgumentException: I2C address 200 does not exist: a 7-bit address is from 0 to 127 (0x7F). " +
                "(Parameter 'address')"
        },
        {
            "Spi.Open(0, 18, Value(4), -1, 1_000_000, 0);",
            "System.ArgumentException: GPIO 4 is not a TX pin of SPI0: its TX pins are GPIO 3, 7, 19 and 23 (-1 for none). " +
                "(Parameter 'tx')"
        },
        {
            "Spi.Open(1, 10, 11, 12, 1_000_000, Value(-1));",
            "System.ArgumentException: SPI mode -1 does not exist: the mode is 0, 1, 2 or 3. (Parameter 'mode')"
        },
        {
            "Spi.Open(1, 10, 11, 12, 1_000_000, 0); Spi.Transfer(1, stackalloc byte[3], stackalloc byte[2]);",
            "System.ArgumentException: Spi.Transfer receives a byte for each byte it sends: write has 3 bytes and read 2. " +
                "(Parameter 'read')"
        },
        {
            "Spi.Write(0, stackalloc byte[1]);",
            "System.InvalidOperationException: SPI0 is not open: call Spi.Open(0, sck, tx, rx, frequency, mode) first."
        },
    };

    [SimulatorTheory]
    [MemberData(nameof(Mistakes))]
    public async Task A_mistake_the_chip_finds_stops_the_program_with_a_message(string code, string message)
    {
        // Value(n) is n, known only when the program runs: chipsharp check holds constants to the target's tables itself
        var program = PicoBuild.Build($$"""
            using System;
            using ChipSharp.Hardware;

            {{code}}

            partial class Program
            {
                static int Value(int value) => value;
            }
            """, "Mistake", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("the program hit a breakpoint", run.Stopped);
        Assert.Equal($"Unhandled exception. {message}\r\n", Encoding.UTF8.GetString(run.Uart0));
    }

    /// <summary>The lines run.mjs printed that start with <paramref name="prefix"/> after their time, without the time.</summary>
    private static List<string> Lines(SimulatorRun run, string prefix) =>
    [
        .. run.Output.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 16 && line[0] == '['
            && line[16..].StartsWith(prefix, StringComparison.Ordinal)).Select(line => line[16..]),
    ];

    /// <summary>The time, in milliseconds, of the first line that starts with <paramref name="prefix"/> after its time.</summary>
    private static double Time(SimulatorRun run, string prefix) => double.Parse(
        run.Output.Split('\n').First(line => line.Length > 16 && line[16..].StartsWith(prefix, StringComparison.Ordinal))[1..11],
        System.Globalization.CultureInfo.InvariantCulture);
}
