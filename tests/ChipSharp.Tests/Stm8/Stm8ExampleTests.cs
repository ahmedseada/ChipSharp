using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ChipSharp.Simulators;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;
using PinChange = ChipSharp.Simulators.PinChange;

namespace ChipSharp.Tests.Stm8;

/// <summary>
/// The example projects for the blue STM8S103 board (examples/stm8-*), built the way their READMEs say (dotnet build of
/// the project, then <c>chipsharp build -t stm8s103-board</c> of its .dll) and run in chipsim with what their circuit
/// gives them (commands typed, a button pressed, an LCD on the I2C bus, a knob turned): each pin's timeline, what each
/// sends over UART1 and shows on the LCD, and what each takes of the chip's 8 KB and 1 KB, in the test's output and its
/// messages. They use the portable API alone (Pin, Timing, [Interrupt], Console, Adc, I2c, Pwm and
/// ChipSharp.Components): no Registers, nothing of the STM8's own.
/// </summary>
public sealed partial class Stm8ExampleTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The segments each digit lights, bit 0 = A.</summary>
    private static readonly byte[] Digits = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];

    /// <summary>The traffic light's display: the pins of its segments A to G.</summary>
    private static readonly string[] Segments = ["PC3", "PC4", "PC5", "PC6", "PC7", "PD2", "PD3"];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>The board's LED on PB5, lit while low: Pin.Output lights it, each toggle lets it go or lights it again.</summary>
    [SdccFact]
    [RunsLong]
    public async Task The_blink_toggles_the_boards_LED_on_PB5_every_half_second()
    {
        var example = await BuildAsync("stm8-blink");

        var run = Run(example, 1600);

        // PB5 is true open-drain: "input" is the pin let go (the LED dark, its resistor to 3.3 V)
        var led = run.Pins.Where(p => p.Pin == "PB5").ToList();
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "input", "low", "input", "low"]), $"{example}\n{run}");
        Assert.InRange(led[1].Milliseconds - led[0].Milliseconds, 0, 0.1); // Pin.Output, then the first toggle at once
        for (var i = 2; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds - led[i - 1].Milliseconds, 499.99, 500.1);
        }

        Assert.True(example.Ram <= 32, $"{example}");
    }

    /// <summary>
    /// The prompt: on, status, off, a line with a backspace and status again, ended as terminals end them ("\r\n", "\r",
    /// "\n"): each char comes back as it is typed, each command is answered, and PB5 (the LED, lit while low) follows.
    /// </summary>
    [SdccFact]
    [RunsLong]
    public async Task The_serial_prompt_switches_the_LED_and_answers_each_command()
    {
        var example = await BuildAsync("stm8-serial");

        var run = Run(example, 300, "--send", @"on\r\n@20ms", "--send", @"status\r@60ms", "--send", @"off\n@100ms",
            "--send", @"hello\x08!\r\n@140ms", "--send", @"status\r\n@200ms");

        Assert.Equal(
            "ChipSharp on an STM8S103 board. Commands: on, off, status\r\n" +
            "> on\r\nLED on\r\n" +
            "> status\r\nLED on, 2 commands\r\n" +
            "> off\r\nLED off\r\n" +
            "> hello\b \b!\r\nyou said: hell!\r\n" +
            "> status\r\nLED off, 5 commands\r\n" +
            "> ",
            Encoding.ASCII.GetString(run.Serial));
        Assert.Equal(["UART1 baud 115108"], run.After("UART1 baud"));

        // dark from the start (let go), lit by "on" (20 ms), dark again by "off" (100 ms)
        var led = run.Pins.Where(p => p.Pin == "PB5").ToList();
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "input", "low", "input"]), $"{example}\n{run}");
        Assert.InRange(led[1].Milliseconds, 0, 1);
        Assert.InRange(led[2].Milliseconds, 20, 21);
        Assert.InRange(led[3].Milliseconds, 100, 101);
    }

    /// <summary>
    /// The traffic light, its button pressed at 2.5 s (PD4 to 3V3), during lane 1's green, for 16 s: over UART1 exactly
    /// what the Pico's and the Uno's traffic lights print; which light is on and what the turn display shows at the
    /// times the Uno's test looks; the green the press ends; the heartbeat on PB5 from the timer event.
    /// </summary>
    [SdccFact]
    [RunsLong]
    public async Task The_traffic_light_prints_what_the_Picos_prints_and_its_lights_display_button_and_heartbeat_work()
    {
        var example = await BuildAsync("stm8-traffic-light");

        var run = Run(example, 16_000, "--input", "PD4=high@2500ms", "--input", "PD4=low@2700ms");
        var pins = run.Pins;

        Assert.Equal(TrafficLightExampleTests.Expected, Encoding.ASCII.GetString(run.Serial));

        // which light is on (red PA1, yellow PA2, green PA3) and what the display shows: the Uno's turn, its seconds
        // counted with 10 ms delays, a few ms later each second
        (double At, string Light, int Turn)[] expected =
        [
            (500, "PA3", 8), (2400, "PA3", 6), (2600, "PA2", 4), (4000, "PA1", 3), (6000, "PA1", 1),
            (7000, "PA3", 8), (11000, "PA2", 4), (13000, "PA1", 2), (15000, "PA3", 8),
        ];
        foreach (var (at, light, turn) in expected)
        {
            var on = string.Join(' ', new[] { "PA1", "PA2", "PA3" }.Where(pin => IsHigh(pins, pin, at)));
            Assert.Equal((at, light, turn), (at, on, Shown(pins, at)));
        }

        // the press ends the green within one 10 ms wait; each second a few ms long
        var greenOff = pins.First(p => p.Pin == "PA3" && p.State == "low" && p.Milliseconds > 1000).Milliseconds;
        Assert.InRange(greenOff, 2500, 2512);
        var greenAgain = pins.First(p => p.Pin == "PA3" && p.State == "high" && p.Milliseconds > 3000).Milliseconds;
        Assert.InRange(greenAgain, 6500, 6560);

        // the button's pin with its pull-up off (the resistor to GND holds it low); PB5 changes every 500 ms
        Assert.DoesNotContain(pins, p => p.Pin == "PD4" && p.State == "input pull-up");
        var heartbeat = pins.Where(p => p.Pin == "PB5").Skip(1).ToList();
        Assert.InRange(heartbeat.Count, 30, 32);
        for (var i = 1; i < heartbeat.Count; i++)
        {
            Assert.InRange(heartbeat[i].Milliseconds - heartbeat[i - 1].Milliseconds, 499.95, 500.05);
        }
    }

    /// <summary>
    /// The LCD at 0x27 on the board's I2C bus (PB5, PB4) shows the knob on PD3: 0 V, 1.65 V and 3.3 V are the readings 0,
    /// 512 and 1023, written over the row's digits as the knob turns.
    /// </summary>
    [SdccFact]
    [RunsLong]
    public async Task The_LCD_shows_the_knobs_reading_and_voltage()
    {
        var example = await BuildAsync("stm8-lcd");

        var run = Run(example, 1200, "--i2c", "0:0x27=lcd1602", "--adc", "PD3=0V", "--adc", "PD3=1.65V@400ms",
            "--adc", "PD3=3.3V@800ms");

        Assert.Equal(
            [
                "LCD 0x27: |Knob on PD3     |", "LCD 0x27: |0000 = 0.000 V  |",
                "LCD 0x27: |Knob on PD3     |", "LCD 0x27: |0512 = 1.650 V  |",
                "LCD 0x27: |Knob on PD3     |", "LCD 0x27: |1023 = 3.296 V  |",
            ],
            run.After("LCD 0x27: "));
        Assert.Empty(run.After("LCD 0x27 busy"));
        Assert.Equal(["I2C0 100000 Hz"], run.After("I2C0 1"));

        // a new reading on the LCD within a tenth of a second and the row's writes
        var rows = run.Times("LCD 0x27: |0");
        Assert.InRange(rows[^1], 400, 520);
        var full = run.Times("LCD 0x27: |1");
        Assert.InRange(Assert.Single(full), 800, 920);
    }

    /// <summary>
    /// The fade: PD4 a PWM output at 1000 Hz, its pulse 20 us longer every 20 ms up to the whole period (steady high),
    /// then shorter down to none (steady low), and again.
    /// </summary>
    [SdccFact]
    [RunsLong]
    public async Task The_fade_makes_PD4s_pulse_grow_to_the_whole_period_and_shrink_back()
    {
        var example = await BuildAsync("stm8-pwm-fade");

        var run = Run(example, 2100);

        var periods = run.After("PD4 PWM ").Select(line => line["PD4 PWM ".Length..]).ToList();
        var top = periods.IndexOf("steady high");
        var bottom = periods.IndexOf("steady low");
        Assert.True(top == 49 && bottom == top + 50, $"{example}\n{run}");
        var pulses = periods.Select(Pulse).ToList();
        Assert.Equal(Enumerable.Range(1, 49).Select(i => 20.0 * i), pulses.Take(top));
        Assert.Equal(Enumerable.Range(1, 49).Select(i => 1000 - 20.0 * i), pulses.Skip(top + 1).Take(49));
        Assert.Equal(20.0, pulses[bottom + 1]);
        var times = run.Times("PD4 PWM steady");
        Assert.InRange(times[0], 1000, 1010);
        Assert.InRange(times[1], 2000, 2010);
    }

    [Fact]
    public void There_are_examples_for_the_STM8S103_board_each_with_a_README_and_the_portable_API_alone()
    {
        Assert.Equal(["stm8-blink", "stm8-lcd", "stm8-pwm-fade", "stm8-serial", "stm8-traffic-light"], Examples());
        foreach (var example in Examples())
        {
            var folder = Path.Combine(Repository.Examples, example);
            var readme = File.ReadAllText(Path.Combine(folder, "README.md"));
            Assert.Contains("chipsharp build", readme, StringComparison.Ordinal);
            Assert.Contains("-t stm8s103-board", readme, StringComparison.Ordinal);
            Assert.Contains("chipsharp flash", readme, StringComparison.Ordinal);
            Assert.Contains("chipsim stm8s103", readme, StringComparison.Ordinal);

            // nothing of the STM8's own: no calls of Registers, only ChipSharp.Hardware and ChipSharp.Components
            var program = File.ReadAllText(Path.Combine(folder, "Program.cs"));
            Assert.DoesNotMatch(@"Registers\.\w+\(", program);
            Assert.Subset(new HashSet<string> { "ChipSharp.Hardware", "ChipSharp.Components" },
                ProjectReferences(ExampleProjects.Project(example)).ToHashSet());
        }
    }

    /// <summary>What a build of an example printed: the .hex, and what it takes of the chip.</summary>
    private sealed record BuiltExample(string Name, string Hex, string Size, int Ram)
    {
        public override string ToString() => $"{Name}: {Size}";
    }

    /// <summary>
    /// Builds examples/<paramref name="example"/> with dotnet build and chipsharp build -t stm8s103-board; returns its
    /// .hex and the memory report, which the test's output shows. (A program bigger than the board's memory fails
    /// the build, saying by how much.)
    /// </summary>
    private async Task<BuiltExample> BuildAsync(string example)
    {
        var project = ExampleProjects.Project(example);
        var dll = await ExampleProjects.DotnetBuildAsync(project, _scratch);

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "stm8s103-board", "-o",
            Path.Combine(_scratch, "stm8s103-board"));

        Assert.True(exitCode == 0, error);
        var lines = printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(Path.GetFileNameWithoutExtension(project) + ".hex", Path.GetFileName(lines[0]));
        var size = MemoryReport().Match(lines[1]);
        Assert.True(size.Success, lines[1]);
        var built = new BuiltExample(example, lines[0], lines[1],
            int.Parse(size.Groups["ram"].Value, CultureInfo.InvariantCulture));
        output.WriteLine(built.ToString());
        return built;
    }

    /// <summary>
    /// Runs the example in chipsim's STM8S103 with the command line's options; it must still be running when the time
    /// is up (each loops forever), never stopped by a failure.
    /// </summary>
    private static SimulationResult Run(BuiltExample example, double milliseconds, params string[] options)
    {
        var run = Simulation.Run(SimulationOptions.Parse("stm8s103",
            [example.Hex, "--ms", milliseconds.ToString(CultureInfo.InvariantCulture), .. options]));
        Assert.True(run.ExitCode == 0, $"{example}\n{run}");
        Assert.True(run.Stopped.EndsWith(" ms of simulated time have passed", StringComparison.Ordinal), $"{example}\n{run}");
        return run;
    }

    /// <summary>True when the last high or low of <paramref name="pin"/> at or before <paramref name="at"/> ms was high.</summary>
    private static bool IsHigh(IReadOnlyList<PinChange> pins, string pin, double at) =>
        pins.LastOrDefault(p => p.Pin == pin && p.Milliseconds <= at && p.State is "low" or "high")?.State == "high";

    /// <summary>The digit the traffic light's display shows at <paramref name="at"/> ms, or -1.</summary>
    private static int Shown(IReadOnlyList<PinChange> pins, double at)
    {
        var segments = Enumerable.Range(0, 7).Select(i => IsHigh(pins, Segments[i], at) ? 1 << i : 0).Sum();
        return Array.IndexOf(Digits, (byte)segments);
    }

    /// <summary>A PWM period's pulse in microseconds ("1000.00 Hz, duty 2.00% (20.00 us high)"), or -1 for a steady pin.</summary>
    private static double Pulse(string period)
    {
        var match = PulseText().Match(period);
        return match.Success ? double.Parse(match.Groups["us"].Value, CultureInfo.InvariantCulture) : -1;
    }

    private static string[] Examples() =>
    [
        .. Directory.GetDirectories(Repository.Examples, "stm8-*").Select(folder => Path.GetFileName(folder)).Order(),
    ];

    private static string[] ProjectReferences(string project) =>
    [
        .. ProjectReference().Matches(File.ReadAllText(project)).Select(match => match.Groups["name"].Value),
    ];

    [GeneratedRegex(@"^flash (\d+ bytes|\d\.\d KB) of 8 KB \(\d+\.\d%\), RAM (?<ram>\d+) bytes of 1 KB \(stack 256 bytes reserved\)$")]
    private static partial Regex MemoryReport();

    [GeneratedRegex(@"^1000\.00 Hz, duty [0-9.]+% \((?<us>[0-9.]+) us high\)$")]
    private static partial Regex PulseText();

    [GeneratedRegex(@"<ProjectReference Include=""[^""]*\\(?<name>[^\\""]+)\.csproj""")]
    private static partial Regex ProjectReference();
}
