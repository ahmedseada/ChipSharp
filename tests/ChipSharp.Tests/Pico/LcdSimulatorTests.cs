using System.Globalization;
using System.Text;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// ChipSharp.Hardware's Lcd on the simulated Pico, with run.mjs's simulated LCD (tools/rp2040-sim/devices/lcd1602.mjs,
/// a PCF8574 and an HD44780): the bytes it writes to the backpack, byte for byte, and what the HD44780 takes from them
/// at each fall of E, with the datasheet's waits between; what the display shows (rows, padding, numbers, chars it
/// does not have, text cut off at a row's end), on a Pico without a heap; a 20x4 display; a display that is not there;
/// and the model itself, which loses what comes while the HD44780 is busy.
/// </summary>
public sealed class LcdSimulatorTests : IDisposable
{
    private const string Backpack = "I2C1 0x27 write";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [SimulatorFact]
    public async Task Open_sets_the_HD44780_up_the_datasheets_way_and_each_byte_is_one_transfer_of_five()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            I2c.Open(1, sda: 26, scl: 27, frequency: 400_000);
            Console.WriteLine(Lcd.Open(1, 0x27, 16, 2));
            Lcd.Write(3, 1, "Hi");
            Lcd.Clear();
            Lcd.Backlight(false);
            Lcd.Write(0, 0, 'A');
            Lcd.Backlight(true);
            Console.WriteLine("done");
            """, "LcdBytes", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000, "--i2c", "1:0x27=lcd1602");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("True\r\ndone\r\n", Encoding.UTF8.GetString(run.Uart0));

        // P0 RS, P1 RW (never), P2 E, P3 the backlight, P4-P7 D4-D7: E high and low around each half of a byte, RS and
        // the upper half set before E rises
        var transfers = Transfers(run);
        Assert.Equal(
        [
            "08",                                                         // the backlight on, E low: the backpack answers
            "38 3c 38", "38 3c 38", "38 3c 38",                           // function set, 8-bit data: three times
            "28 2c 28",                                                   // function set, 4-bit data
            "28 2c 28 8c 88",                                             // 0x28: 4-bit, two lines, 5x8 dots
            "08 0c 08 8c 88",                                             // 0x08: display off
            "08 0c 08 1c 18",                                             // 0x01: clear
            "08 0c 08 6c 68",                                             // 0x06: entry mode, the address going up
            "08 0c 08 cc c8",                                             // 0x0C: display on, no cursor
            "c8 cc c8 3c 38", "49 4d 49 8d 89", "69 6d 69 9d 99",         // DDRAM 0x43 (column 3 of row 1), 'H', 'i'
            "08 0c 08 1c 18",                                             // Clear
            "00",                                                         // Backlight(false)
            "80 84 80 04 00", "41 45 41 15 11",                           // DDRAM 0x00, 'A', the backlight off
            "08",                                                         // Backlight(true)
        ],
            transfers.Select(transfer => transfer.Bytes));
        Assert.Equal(
            ["8-bit 3", "8-bit 3", "8-bit 3", "8-bit 2", "28", "08", "01", "06", "0C", "C3", "'H'", "'i'", "01", "80", "'A'"],
            Decode(transfers.Select(transfer => transfer.Bytes)));

        // the waits: 50 ms for the power-up (40), 4.1 ms and 100 us after the first two function sets, 37 us after each
        // instruction and character (the simulated bus takes no time), 1.52 ms after a clear; none after the backlight
        // alone, which the HD44780 does not see
        var at = transfers.Select(transfer => transfer.Milliseconds).ToList();
        Assert.InRange(at[1] - at[0], 50.0, 50.5);
        Assert.InRange(at[2] - at[1], 4.1, 4.7);
        Assert.InRange(at[3] - at[2], 0.1, 0.25);
        Assert.All(Enumerable.Range(4, at.Count - 4).Where(i => transfers[i - 1].Bytes.Length > 2),
            i => Assert.True(at[i] - at[i - 1] >= 0.037, $"transfer {i}"));
        Assert.True(at[8] - at[7] >= 1.52 && at[14] - at[13] >= 1.52);

        // the simulated HD44780 lost nothing; it shows what was written last, the cleared text gone
        Assert.DoesNotContain("busy", run.Output);
        Assert.Equal([["A               ", "                "]], Screens(run, 2));
        Assert.Equal(["LCD 0x27 backlight on", "LCD 0x27 display off"], Lines(run, "LCD 0x27 "));
    }

    [SimulatorFact]
    public async Task On_a_Pico_without_a_heap_it_shows_lines_numbers_and_text_cut_off_at_the_rows_end()
    {
        var noHeap = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    I2c.Open(1, sda: 26, scl: 27, frequency: 100_000);
                    Lcd.Open(1, 0x27, columns: 16, rows: 2);

                    // whole lines, then a shorter one: what was there goes; Write leaves the rest
                    Lcd.Line(0, "Road 1 green");
                    Lcd.Line(1, "waiting 12");
                    Settle();
                    Lcd.Line(0, "Road 2");
                    Lcd.Write(8, 1, 7);
                    Settle();

                    // numbers, digit by digit
                    Lcd.Line(0, "");
                    Lcd.Write(0, 0, -120);
                    Lcd.Write(5, 0, int.MaxValue);
                    Lcd.Write(0, 1, int.MinValue);
                    Lcd.Write(14, 1, 0);
                    Settle();

                    // chars the display does not have
                    Lcd.Line(0, "Grüße 20°C");
                    Lcd.Line(1, "tab\there €");
                    Lcd.Write(15, 1, 'é');
                    Settle();

                    // cut off at the row's end, before column 0, or not on the display at all
                    Lcd.Clear();
                    Lcd.Write(12, 0, "overflow");
                    Lcd.Write(-3, 1, "cutoff");
                    Lcd.Write(0, 2, "no row 2");
                    Lcd.Write(16, 0, "x");
                    Lcd.Write(-1, 0, 'x');
                    Lcd.Write(16, 1, 'x');
                    Lcd.Line(2, "x");
                    Lcd.Line(-1, "x");
                    Settle();

                    // spans of chars, and null
                    Span<char> letters = stackalloc char[3];
                    letters[0] = 'a';
                    letters[1] = 'b';
                    letters[2] = 'c';
                    Lcd.Line(0, (string)null!);
                    Lcd.Write(0, 0, (string)null!);
                    Lcd.Line(1, letters);
                    Lcd.Write(14, 1, letters[1..]);
                    Settle();

                    Lcd.Backlight(false);
                    Settle();
                    Lcd.Backlight(true);
                    Console.WriteLine("done");
                }

                static void Settle() => Timing.DelayMilliseconds(20);
            }
            """, "LcdText", _scratch, noHeap);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000, "--i2c", "1:0x27=lcd1602");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("done\r\n", Encoding.UTF8.GetString(run.Uart0));
        Assert.Equal(
        [
            ["Road 1 green    ", "waiting 12      "],
            ["Road 2          ", "waiting 72      "],
            ["-120 2147483647 ", "-2147483648   0 "],
            ["Gr??e 20?C      ", "tab?here ?     ?"],
            ["            over", "off             "],
            ["                ", "abc           bc"],
        ],
            Screens(run, 2));
        Assert.Equal(["LCD 0x27 backlight on", "LCD 0x27 display off", "LCD 0x27 backlight off", "LCD 0x27 backlight on"],
            Lines(run, "LCD 0x27 "));
        Assert.DoesNotContain("busy", run.Output);
        Assert.DoesNotContain("chipsharp_new_object", ElfFile.Read(program.Elf).Symbols.Keys);
    }

    [SimulatorFact]
    public async Task On_a_20x4_display_rows_2_and_3_follow_rows_0_and_1_and_text_never_runs_on_into_them()
    {
        var program = PicoBuild.Build("""
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 4, scl: 5, frequency: 400_000);
            Lcd.Open(0, 0x3F, 20, 4);
            for (var row = 0; row < 4; row++)
            {
                Lcd.Line(row, "row");
                Lcd.Write(4, row, row);
            }

            Lcd.Write(15, 0, "spills over");
            Lcd.Write(15, 1, "spills over");
            Lcd.Write(18, 3, 99);
            """, "Lcd2004", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000, "--i2c", "0:0x3f=lcd2004");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(
            [["row 0          spill", "row 1          spill", "row 2               ", "row 3             99"]],
            Screens(run, 4, "LCD 0x3f"));

        // rows 2 and 3 start at 0x14 and 0x54, after the 20 columns of rows 0 and 1
        Assert.Contains("I2C0 0x3f write 98 9c 98 4c 48", run.Output);
        Assert.Contains("I2C0 0x3f write d8 dc d8 4c 48", run.Output);
    }

    [SimulatorFact]
    public async Task A_display_that_is_not_there_or_of_a_size_no_HD44780_has_says_false_at_once_and_the_rest_do_nothing()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            I2c.Open(1, sda: 26, scl: 27, frequency: 100_000);
            Lcd.Line(0, "not open yet");
            Console.WriteLine(Lcd.Open(1, 0x27, Value(41), 1));
            Console.WriteLine(Lcd.Open(1, 0x27, Value(21), 4));
            Console.WriteLine(Lcd.Open(1, 0x27, 16, Value(0)));
            Console.WriteLine(Lcd.Open(1, 0x26, 16, 2));
            Lcd.Line(0, "nothing");
            Lcd.Write(0, 1, 42);
            Lcd.Write(0, 1, 'x');
            Lcd.Clear();
            Lcd.Backlight(false);
            Console.WriteLine("done");

            partial class Program
            {
                static int Value(int value) => value;
            }
            """, "LcdMissing", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000, "--i2c", "1:0x27=lcd1602");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("False\r\nFalse\r\nFalse\r\nFalse\r\ndone\r\n", Encoding.UTF8.GetString(run.Uart0));
        Assert.Equal(["I2C1 0x26 write: no answer"], Lines(run, "I2C1 0x"));
        Assert.DoesNotContain("LCD 0x27", run.Output);
        Assert.True(Time(run, "UART0: done") < 5, run.Output);
        Assert.EndsWith("the program has finished: the core sleeps and nothing is scheduled to wake it", run.Stopped);
    }

    [SimulatorFact]
    public async Task The_simulated_HD44780_loses_what_comes_while_it_is_busy_and_says_so()
    {
        // the bytes of Lcd's own initialisation, without its waits
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            I2c.Open(1, sda: 26, scl: 27, frequency: 100_000);
            Span<byte> eightBits = stackalloc byte[3];
            eightBits[0] = 0x38;
            eightBits[1] = 0x3C;
            eightBits[2] = 0x38;
            I2c.Write(1, 0x27, eightBits);
            Timing.DelayMilliseconds(45);
            I2c.Write(1, 0x27, eightBits);
            I2c.Write(1, 0x27, eightBits);
            Timing.DelayMilliseconds(5);
            I2c.Write(1, 0x27, eightBits);
            """, "LcdBusy", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000, "--i2c", "1:0x27=lcd1602");

        Assert.True(run.ExitCode == 0, run.ToString());
        var lost = Lines(run, "LCD 0x27 busy: ");
        Assert.Equal(2, lost.Count);
        Assert.Matches(@"^LCD 0x27 busy: lost a write 0\.\d{3} ms into power-up \(40\.000 ms\)$", lost[0]);
        Assert.Matches(@"^LCD 0x27 busy: lost a write 0\.0\d{2} ms into function set \(4\.100 ms\)$", lost[1]);
    }

    /// <summary>The transfers to the backpack at 0x27 on I2C1: when, and their bytes in hex.</summary>
    private static List<(double Milliseconds, string Bytes)> Transfers(SimulatorRun run) =>
    [
        .. run.Output.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 16 && line[16..].StartsWith(Backpack + " ", StringComparison.Ordinal))
            .Select(line => (Milliseconds(line), line[(16 + Backpack.Length + 1)..])),
    ];

    /// <summary>
    /// What the HD44780 behind the PCF8574 takes from <paramref name="transfers"/>: at each fall of E (P2), RS (P0) and
    /// D4-D7 (P4-P7) as they were while E was high, when RW (P1) was low (the backpack starts with every output high:
    /// a read); four bits alone ("8-bit 3") until the function set of 4-bit data,
    /// then a byte for each two falls, an instruction in hex or a character in quotes.
    /// </summary>
    private static List<string> Decode(IEnumerable<string> transfers)
    {
        var taken = new List<string>();
        var before = 0xFF;
        var eightBits = true;
        int? upper = null;
        foreach (var value in transfers.SelectMany(bytes => bytes.Split(' ')).Select(b => Convert.ToInt32(b, 16)))
        {
            Assert.Equal(0, value & 0x02); // RW: always a write
            if ((before & 0x04) != 0 && (value & 0x04) == 0 && (before & 0x02) == 0)
            {
                var bits = before >> 4;
                if (eightBits)
                {
                    taken.Add($"8-bit {bits:X}");
                    eightBits = bits != 0x2;
                }
                else if (upper is null)
                {
                    upper = bits;
                }
                else
                {
                    var data = (upper.Value << 4) | bits;
                    taken.Add((before & 0x01) != 0 ? $"'{(char)data}'" : $"{data:X2}");
                    upper = null;
                }
            }

            before = value;
        }

        return taken;
    }

    /// <summary>Each screen the simulated LCD printed: its rows, between the bars.</summary>
    private static List<string[]> Screens(SimulatorRun run, int rows, string lcd = "LCD 0x27") =>
    [
        .. Lines(run, lcd + ": |").Select(line => line[(lcd.Length + 3)..^1]).Chunk(rows),
    ];

    /// <summary>The lines run.mjs printed that start with <paramref name="prefix"/> after their time, without the time.</summary>
    private static List<string> Lines(SimulatorRun run, string prefix) =>
    [
        .. run.Output.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 16 && line[0] == '['
            && line[16..].StartsWith(prefix, StringComparison.Ordinal)).Select(line => line[16..]),
    ];

    /// <summary>The time, in milliseconds, of the first line that starts with <paramref name="prefix"/> after its time.</summary>
    private static double Time(SimulatorRun run, string prefix) => Milliseconds(
        run.Output.Split('\n').First(line => line.Length > 16 && line[16..].StartsWith(prefix, StringComparison.Ordinal)));

    private static double Milliseconds(string line) => double.Parse(line[1..11], CultureInfo.InvariantCulture);
}
