using System.Text;
using System.Text.Json;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The bus examples, examples/pico-i2c-mpu6050 (an MPU-6050 read over I2C) and examples/pico-spi-shift-register (a
/// 74HC595 driving a 7-segment digit over SPI), built the way their Program.cs says (dotnet build of the project, then
/// <c>chipsharp build -t pico</c>) and run in the simulator with the simulated device on the bus; and their Wokwi
/// diagrams, which wire what the programs use.
/// </summary>
public sealed class BusExampleTests : IDisposable
{
    /// <summary>The segments each digit lights, bit 0 = A: the example's own table.</summary>
    private static readonly byte[] Digits = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [SimulatorFact]
    [RunsLong]
    public async Task The_MPU_6050_example_reads_who_it_is_then_its_measurements_twice_a_second_with_no_heap()
    {
        var (report, run) = await BuildAndRunAsync("pico-i2c-mpu6050", 1200, "--i2c", "0:0x68=mpu6050");

        Assert.Matches(@"^flash \d+(\.\d)? KB of 2048 KB \(\d+\.\d%\), RAM \d+ bytes of 264 KB \(stack 8 KB reserved\)$",
            report);
        Assert.True(run.ExitCode == 0, run.ToString());
        const string Measured = "acceleration (mg): x 500, y -250, z 1000; rotation (degrees/s): x 10.0, y -5.0, z 0.0; " +
            "temperature 24.00 C\r\n";
        Assert.Equal("MPU-6050 at 0x68, WHO_AM_I 0x68\r\n" + Measured + Measured + Measured,
            Encoding.UTF8.GetString(run.Uart0));

        // WHO_AM_I and the measurements with a repeated start each, the wake-up in one write, at 400 kHz on GPIO 4 and 5
        Assert.Contains("I2C0 0x68 write 75, read 68", run.Output);
        Assert.Contains("I2C0 0x68 write 6b 00", run.Output);
        Assert.Contains("I2C0 0x68 write 3b, read 20 00 f0 00 40 00 ef 5c 05 1e fd 71 00 00", run.Output);
        Assert.Contains("I2C0 399361 Hz", run.Output);
        Assert.Contains(run.Pins, p => p.Pin == 4 && p.State == "input pull-up");
        Assert.Contains(run.Pins, p => p.Pin == 5 && p.State == "input pull-up");
    }

    [SimulatorFact]
    [RunsLong]
    public async Task The_MPU_6050_example_says_so_when_nothing_answers()
    {
        var (_, run) = await BuildAndRunAsync("pico-i2c-mpu6050", 200);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("No MPU-6050 answers at 0x68: check its wiring.\r\n", Encoding.UTF8.GetString(run.Uart0));
        Assert.EndsWith("the program has finished: the core sleeps and nothing is scheduled to wake it", run.Stopped);
    }

    [SimulatorFact]
    [RunsLong]
    public async Task The_shift_register_example_counts_on_the_74HC595s_outputs_one_digit_every_half_second()
    {
        var (report, run) = await BuildAndRunAsync("pico-spi-shift-register", 5600, "--spi", "0=74hc595@17");

        Assert.Matches(@"^flash \d+(\.\d)? KB of 2048 KB \(\d+\.\d%\), RAM \d+ bytes of 264 KB \(stack 8 KB reserved\)$",
            report);
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(string.Concat(Enumerable.Range(0, 12).Select(n => $"digit {n % 10}\r\n")),
            Encoding.UTF8.GetString(run.Uart0));
        Assert.Contains("SPI0 mode 0, 992063 Hz", run.Output);

        // what the outputs show after each latch (the latch high, then low again): the digit, never anything between two
        var outputs = 0;
        var latched = false;
        var shown = new List<int>();
        foreach (var line in run.Output.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.EndsWith(" GPIO17 high", StringComparison.Ordinal))
            {
                latched = true;
            }
            else if (line.EndsWith(" GPIO17 low", StringComparison.Ordinal) && latched)
            {
                shown.Add(outputs);
                latched = false;
            }
            else if (line.IndexOf("SPI0 74HC595 Q", StringComparison.Ordinal) is > 0 and var at)
            {
                var bit = 1 << (line[at + "SPI0 74HC595 Q".Length] - '0');
                outputs = line.EndsWith(" high", StringComparison.Ordinal) ? outputs | bit : outputs & ~bit;
            }
        }

        Assert.Equal(Enumerable.Range(0, 12).Select(n => (int)Digits[n % 10]), shown);

        // the latch: low from Pin.Output, then a short high after each byte
        Assert.Equal(25, run.Pins.Count(p => p.Pin == 17 && p.State is "low" or "high"));
    }

    [Fact]
    public void The_MPU_6050_examples_Wokwi_diagram_wires_it_to_I2C0_on_GPIO_4_and_5()
    {
        var (parts, nets) = Diagram("pico-i2c-mpu6050");

        Assert.Equal("wokwi-pi-pico", parts["pico"]);
        Assert.Equal("wokwi-mpu6050", parts["imu"]);
        Assert.True(nets.Joined("pico:GP4", "imu:SDA"));
        Assert.True(nets.Joined("pico:GP5", "imu:SCL"));
        Assert.True(nets.Joined("pico:3V3", "imu:VCC"));
        Assert.True(Grounded(nets, "imu:GND"));
        Assert.True(nets.Joined("pico:GP0", "$serialMonitor:RX") && nets.Joined("pico:GP1", "$serialMonitor:TX"));
    }

    [Fact]
    public void The_shift_register_examples_Wokwi_diagram_wires_SPI0_the_latch_and_the_segments()
    {
        var (parts, nets) = Diagram("pico-spi-shift-register");

        Assert.Equal("wokwi-74hc595", parts["sr"]);
        Assert.Equal("wokwi-7segment", parts["digit"]);
        Assert.True(nets.Joined("pico:GP19", "sr:DS"));      // SPI0 TX
        Assert.True(nets.Joined("pico:GP18", "sr:SHCP"));    // SPI0 SCK
        Assert.True(nets.Joined("pico:GP17", "sr:STCP"));    // the latch
        Assert.True(Grounded(nets, "sr:OE") && Grounded(nets, "sr:GND"));
        Assert.True(nets.Joined("sr:MR", "pico:3V3") && nets.Joined("sr:VCC", "pico:3V3"));

        // Q0..Q6 to the segments A..G and Q7 to the decimal point, by a wire (Wokwi needs no resistors) or through one
        // resistor
        string[] segments = ["A", "B", "C", "D", "E", "F", "G", "DP"];
        for (var q = 0; q < 8; q++)
        {
            var (from, to) = ($"sr:Q{q}", $"digit:{segments[q]}");
            Assert.True(nets.Joined(from, to) || parts.Keys.Any(id => parts[id] == "wokwi-resistor" &&
                (nets.Joined(from, $"{id}:1") && nets.Joined(to, $"{id}:2") ||
                    nets.Joined(from, $"{id}:2") && nets.Joined(to, $"{id}:1"))),
                $"Q{q} is not wired to segment {segments[q]}");
        }

        // COM.1 and COM.2 are the same common pin: one of them to GND is enough
        Assert.True(Grounded(nets, "digit:COM.1") || Grounded(nets, "digit:COM.2"));
        Assert.True(nets.Joined("pico:GP0", "$serialMonitor:RX") && nets.Joined("pico:GP1", "$serialMonitor:TX"));
    }

    private static bool Grounded(Nets nets, string pin) => Enumerable.Range(1, 8).Any(n => nets.Joined(pin, $"pico:GND.{n}"));

    /// <summary>The type of each part of the example's diagram.json, by its id, and the nets its wires make.</summary>
    private static (Dictionary<string, string> Parts, Nets Nets) Diagram(string example)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Examples, example, "diagram.json")));
        var parts = json.RootElement.GetProperty("parts").EnumerateArray()
            .ToDictionary(part => part.GetProperty("id").GetString()!, part => part.GetProperty("type").GetString()!);
        var nets = new Nets(json.RootElement.GetProperty("connections").EnumerateArray()
            .Select(wire => (wire[0].GetString()!, wire[1].GetString()!)));
        return (parts, nets);
    }

    /// <summary>Builds the example as its Program.cs says and runs it; returns what chipsharp build said it takes, and the run.</summary>
    private async Task<(string Report, SimulatorRun Run)> BuildAndRunAsync(string example, int milliseconds,
        params string[] options)
    {
        var scratch = Path.Combine(_scratch, example);
        var project = ExampleProjects.Project(example);
        var dll = await ExampleProjects.DotnetBuildAsync(project, scratch);
        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "pico", "-o",
            Path.Combine(scratch, "pico"));
        Assert.True(exitCode == 0, error);
        var lines = printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Path.GetFileNameWithoutExtension(project) + ".uf2", Path.GetFileName(lines[0]));

        return (lines[1], await Rp2040Simulator.RunAsync(lines[0], milliseconds, options));
    }

    /// <summary>The pins a diagram's wires join, each set of joined pins one net.</summary>
    private sealed class Nets
    {
        private readonly Dictionary<string, string> _parent = [];

        public Nets(IEnumerable<(string From, string To)> wires)
        {
            foreach (var (from, to) in wires)
            {
                _parent[Find(from)] = Find(to);
            }
        }

        public bool Joined(string a, string b) => Find(a) == Find(b);

        private string Find(string pin)
        {
            while (_parent.TryGetValue(pin, out var parent) && parent != pin)
            {
                pin = parent;
            }

            return pin;
        }
    }
}
