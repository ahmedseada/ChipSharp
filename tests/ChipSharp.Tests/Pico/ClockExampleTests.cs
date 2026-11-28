using System.Globalization;
using System.Text;
using System.Text.Json;
using ChipSharp.Image.Uf2;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// examples/pico-clock, the date and time on a 16x2 LCD, set from a DS1307 at start-up and kept by the Pico's clock,
/// built the way its Program.cs says (dotnet build of the project, then <c>chipsharp build -t pico</c>) and run in the
/// simulator with the simulated LCD and DS1307: the screen once a second, with no heap; a missing DS1307; and its Wokwi
/// diagram, the LCD and the DS1307 on I2C1 by direct wires.
/// </summary>
public sealed class ClockExampleTests : IDisposable
{
    private const string Example = "pico-clock";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [SimulatorFact]
    [RunsLong]
    public async Task The_example_shows_the_DS1307s_date_and_time_once_a_second_with_no_heap()
    {
        var uf2 = await BuildAsync();

        var run = await Rp2040Simulator.RunAsync(uf2, 3_500, "--i2c", "1:0x27=lcd1602", "--i2c",
            "1:0x68=ds1307@2026-12-31T23:59:58");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("The time from the DS1307: 2026-12-31 23:59:58\r\n", Encoding.UTF8.GetString(run.Uart0));
        var screens = Screens(run);
        Assert.Equal(
            [
                ["Thu 31 Dec 2026", "23:59:58"],
                ["Thu 31 Dec 2026", "23:59:59"],
                ["Fri 01 Jan 2027", "00:00:00"],
                ["Fri 01 Jan 2027", "00:00:01"],
            ],
            screens.Select(screen => screen.Rows));
        Assert.All(Enumerable.Range(1, 3), i => Assert.InRange(screens[i].Milliseconds - screens[i - 1].Milliseconds, 999, 1001));

        // the DS1307 read once, at start-up (Running, then the time); then the Pico's clock alone
        Assert.Equal(2, run.Output.Split('\n').Count(line => line.Contains("I2C1 0x68 write 00, read", StringComparison.Ordinal)));
        var elf = ElfFile.Read(Path.ChangeExtension(uf2, ".elf"));
        Assert.DoesNotContain("chipsharp_new_object", elf.Symbols.Keys);
    }

    [SimulatorFact]
    [RunsLong]
    public async Task Without_a_DS1307_the_clock_starts_at_its_date()
    {
        var uf2 = await BuildAsync();

        var run = await Rp2040Simulator.RunAsync(uf2, 1_500, "--i2c", "1:0x27=lcd1602");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("No DS1307 running: the clock starts at 2026-11-27 18:00:00\r\n", Encoding.UTF8.GetString(run.Uart0));
        Assert.Equal([["Fri 27 Nov 2026", "18:00:00"], ["Fri 27 Nov 2026", "18:00:01"]], Screens(run).Select(screen => screen.Rows));
    }

    [Fact]
    public void The_examples_Wokwi_diagram_wires_the_LCD_and_the_DS1307_to_I2C1_by_direct_wires()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Examples, Example, "diagram.json")));
        var parts = json.RootElement.GetProperty("parts").EnumerateArray()
            .ToDictionary(part => part.GetProperty("id").GetString()!, part => part.Clone());
        var wires = json.RootElement.GetProperty("connections").EnumerateArray()
            .Select(wire => (From: wire[0].GetString()!, To: wire[1].GetString()!, Steps: wire[3].GetArrayLength())).ToList();

        // the Pico at 0, 0, the LCD in I2C mode and the DS1307 to its right
        Assert.Equal(["wokwi-ds1307", "wokwi-lcd1602", "wokwi-pi-pico"],
            parts.Values.Select(part => part.GetProperty("type").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal((0.0, 0.0), (parts["pico"].GetProperty("top").GetDouble(), parts["pico"].GetProperty("left").GetDouble()));
        Assert.Equal("i2c", parts["lcd"].GetProperty("attrs").GetProperty("pins").GetString());
        Assert.All(["lcd", "rtc"], id => Assert.True(parts[id].GetProperty("left").GetDouble() > 100));

        // each pin of both by a wire of its own from the Pico, straight (no steps); UART0 on the serial monitor
        Assert.Equal(
            [
                ("pico:3V3", "lcd:VCC"), ("pico:3V3", "rtc:5V"), ("pico:GND.7", "rtc:GND"), ("pico:GND.8", "lcd:GND"),
                ("pico:GP0", "$serialMonitor:RX"), ("pico:GP1", "$serialMonitor:TX"), ("pico:GP26", "lcd:SDA"),
                ("pico:GP26", "rtc:SDA"), ("pico:GP27", "lcd:SCL"), ("pico:GP27", "rtc:SCL"),
            ],
            wires.Select(wire => (wire.From, wire.To)).Order());
        Assert.All(wires, wire => Assert.Equal(0, wire.Steps));
    }

    private async Task<string> BuildAsync()
    {
        var project = ExampleProjects.Project(Example);
        var dll = await ExampleProjects.DotnetBuildAsync(project, _scratch);
        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "pico", "-o",
            Path.Combine(_scratch, "pico"), "--keep");
        Assert.True(exitCode == 0, error);
        var lines = printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("PicoClock.uf2", Path.GetFileName(lines[0]));
        Assert.Matches(@"^flash \d+(\.\d)? KB of 2048 KB \(\d+\.\d%\), RAM \d+ bytes of 264 KB \(stack 8 KB reserved\)$",
            lines[^1]);
        return lines[0];
    }

    /// <summary>What the simulated LCD showed, when it settled: its two rows, without the spaces after them.</summary>
    private static List<(double Milliseconds, string[] Rows)> Screens(SimulatorRun run) =>
    [
        .. run.Output.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 16 && line[16..].StartsWith("LCD 0x27: |", StringComparison.Ordinal))
            .Chunk(2).Select(rows => (double.Parse(rows[0][1..11], CultureInfo.InvariantCulture),
                rows.Select(row => row[27..^1].TrimEnd()).ToArray())),
    ];
}
