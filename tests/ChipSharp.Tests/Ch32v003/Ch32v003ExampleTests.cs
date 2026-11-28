using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ChipSharp.Simulators;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;
using PinChange = ChipSharp.Simulators.PinChange;

namespace ChipSharp.Tests.Ch32v003;

/// <summary>
/// The example projects for WCH's CH32V003 evaluation board (examples/ch32v003-*), built the way their READMEs say
/// (dotnet build of the project, then <c>chipsharp build -t ch32v003-evt</c> of its .dll) and run in chipsim's ch32v003
/// with what their circuit gives them (a button pressed, commands typed): each pin's timeline, what each sends over
/// USART1, and what each takes of the chip's 16 KB and 2 KB, in the test's output and its messages. They use the
/// portable API alone (Pin, Timing, [Interrupt], Console, Clock, Stopwatch and ChipSharp.Components' SevenSegment): no
/// Registers, nothing of the CH32V003's own.
/// </summary>
public sealed partial class Ch32v003ExampleTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The segments each digit lights, bit 0 = A.</summary>
    private static readonly byte[] Digits = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];

    /// <summary>The traffic light's display: the pins of its segments A to G (the STM8's: the two chips share a pinout).</summary>
    private static readonly string[] Segments = ["PC3", "PC4", "PC5", "PC6", "PC7", "PD2", "PD3"];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    /// <summary>PD0 driven low (lit) by Pin.Output, high by the first toggle, then the other way every 500 ms.</summary>
    [PicoFact]
    [RunsLong]
    public async Task The_blink_toggles_PD0_every_half_second_in_little_of_the_chips_memory()
    {
        var example = await BuildAsync("ch32v003-blink");

        var run = Run(example, 1600);

        var led = run.Pins.Where(p => p.Pin == "PD0").ToList();
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "high", "low", "high", "low"]), $"{example}\n{run}");
        Assert.InRange(led[1].Milliseconds - led[0].Milliseconds, 0, 0.1);
        for (var i = 2; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds - led[i - 1].Milliseconds, 500, 500.05);
        }

        Assert.True(example.Flash <= 3 * 1024 && example.Ram <= 64, $"{example}");
    }

    /// <summary>
    /// The button on PD3 (its pull-up on: Pull.Up) pressed at 200 ms and again at 600 ms, released between: the pin event
    /// on its falling edges toggles PD0 within a few microseconds of each press, and a release does nothing.
    /// </summary>
    [PicoFact]
    [RunsLong]
    public async Task The_button_toggles_the_LED_through_the_pin_event_on_each_press()
    {
        var example = await BuildAsync("ch32v003-button");

        var run = Run(example, 1000, "--input", "PD3=low@200ms", "--input", "PD3=high@400ms", "--input", "PD3=low@600ms",
            "--input", "PD3=high@800ms");

        Assert.Contains(run.Pins, p => p.Pin == "PD3" && p.State == "input pull-up");
        var led = run.Pins.Where(p => p.Pin == "PD0").ToList();
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "high", "low"]), $"{example}\n{run}");
        Assert.InRange(led[1].Milliseconds, 200, 200.02);
        Assert.InRange(led[2].Milliseconds, 600, 600.02);
    }

    /// <summary>
    /// The timer event toggles PD0 every 500 ms exactly while Main, in its own time, writes the uptime once a second
    /// over USART1 at 115200 baud.
    /// </summary>
    [PicoFact]
    [RunsLong]
    public async Task The_timer_blink_toggles_from_the_timer_event_while_Main_prints_the_uptime()
    {
        var example = await BuildAsync("ch32v003-timer-blink");

        var run = Run(example, 2600);

        var toggles = run.Pins.Where(p => p.Pin == "PD0").Skip(1).Select(p => p.Milliseconds).ToList();
        Assert.True(toggles.Count == 5, $"{example}\n{run}");
        for (var i = 0; i < toggles.Count; i++)
        {
            Assert.InRange(toggles[i] - 500 * (i + 1), 0, 0.05);
        }

        var lines = Encoding.ASCII.GetString(run.SerialOf("USART1")).Split("\r\n");
        Assert.Equal("ChipSharp on a CH32V003: the LED blinks from the timer event", lines[0]);
        Assert.Matches(@"^uptime 100\d ms, 2 toggles$", lines[1]);
        Assert.Matches(@"^uptime 20\d\d ms, 4 toggles$", lines[2]);
        Assert.Equal(["USART1 baud 115108"], run.After("USART1 baud"));
    }

    /// <summary>
    /// The prompt: on, status, off, a line with a backspace and status again, ended as terminals end them ("\r\n",
    /// "\r", "\n"): each char comes back as it is typed (the echo), each command is answered, and PD0 (the LED, lit while
    /// low) follows.
    /// </summary>
    [PicoFact]
    [RunsLong]
    public async Task The_serial_prompt_echoes_each_char_switches_the_LED_and_answers_each_command()
    {
        var example = await BuildAsync("ch32v003-serial");

        var run = Run(example, 300, "--send", @"on\r\n@20ms", "--send", @"status\r@60ms", "--send", @"off\n@100ms",
            "--send", @"hello\x08!\r\n@140ms", "--send", @"status\r\n@200ms");

        Assert.Equal(
            "ChipSharp on a CH32V003 board. Commands: on, off, status\r\n" +
            "> on\r\nLED on\r\n" +
            "> status\r\nLED on, 2 commands\r\n" +
            "> off\r\nLED off\r\n" +
            "> hello\b \b!\r\nyou said: hell!\r\n" +
            "> status\r\nLED off, 5 commands\r\n" +
            "> ",
            Encoding.ASCII.GetString(run.SerialOf("USART1")));

        // dark from the start (high), lit by "on" (20 ms), dark again by "off" (100 ms)
        var led = run.Pins.Where(p => p.Pin == "PD0").ToList();
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "high", "low", "high"]), $"{example}\n{run}");
        Assert.InRange(led[2].Milliseconds, 20, 21);
        Assert.InRange(led[3].Milliseconds, 100, 101);
        Assert.True(example.Flash <= 6 * 1024 && example.Ram <= 128, $"{example}");
    }

    /// <summary>
    /// The traffic light, its button pressed at 2.5 s (PD4 to GND, held high by its pull-up), during lane 1's green, for
    /// 16 s: over USART1 exactly what the Pico's, the Uno's and the STM8's traffic lights print; which light is on and
    /// what the turn display shows at the times the STM8's test looks; the green the press ends; the heartbeat on PD0
    /// from the timer event; and what it takes of the chip's 16 KB and 2 KB.
    /// </summary>
    [PicoFact]
    [RunsLong]
    public async Task The_traffic_light_prints_what_the_other_chips_print_and_its_lights_display_button_and_heartbeat_work()
    {
        var example = await BuildAsync("ch32v003-traffic-light");

        var run = Run(example, 16_000, "--input", "PD4=low@2500ms", "--input", "PD4=high@2700ms");
        var pins = run.Pins;

        Assert.Equal(TrafficLightExampleTests.Expected, Encoding.ASCII.GetString(run.SerialOf("USART1")));
        Assert.Equal(["USART1 baud 115108"], run.After("USART1 baud"));

        // which light is on (red PC0, yellow PC1, green PC2) and what the display shows: the STM8's turn, its seconds
        // counted with 10 ms delays, a few ms later each second
        (double At, string Light, int Turn)[] expected =
        [
            (500, "PC2", 8), (2400, "PC2", 6), (2600, "PC1", 4), (4000, "PC0", 3), (6000, "PC0", 1),
            (7000, "PC2", 8), (11000, "PC1", 4), (13000, "PC0", 2), (15000, "PC2", 8),
        ];
        foreach (var (at, light, turn) in expected)
        {
            var on = string.Join(' ', new[] { "PC0", "PC1", "PC2" }.Where(pin => IsHigh(pins, pin, at)));
            Assert.Equal((at, light, turn), (at, on, Shown(pins, at)));
        }

        // the press ends the green within one 10 ms wait; each second a few ms long
        var greenOff = pins.First(p => p.Pin == "PC2" && p.State == "low" && p.Milliseconds > 1000).Milliseconds;
        Assert.InRange(greenOff, 2500, 2512);
        var greenAgain = pins.First(p => p.Pin == "PC2" && p.State == "high" && p.Milliseconds > 3000).Milliseconds;
        Assert.InRange(greenAgain, 6500, 6560);

        // the button's pin with its pull-up on (Pull.Up: no resistor); PD0 changes every 500 ms
        Assert.Contains(pins, p => p.Pin == "PD4" && p.State == "input pull-up");
        var heartbeat = pins.Where(p => p.Pin == "PD0").Skip(1).ToList();
        Assert.InRange(heartbeat.Count, 30, 32);
        for (var i = 1; i < heartbeat.Count; i++)
        {
            Assert.InRange(heartbeat[i].Milliseconds - heartbeat[i - 1].Milliseconds, 499.95, 500.05);
        }

        Assert.True(example.Flash <= 8 * 1024 && example.Ram <= 128, $"{example}");
    }

    /// <summary>The clock from Clock.Start: a line a second, the seconds counting, each as the second changes.</summary>
    [PicoFact]
    [RunsLong]
    public async Task The_clock_writes_the_time_once_a_second_from_the_date_it_was_given()
    {
        var example = await BuildAsync("ch32v003-clock");

        var run = Run(example, 3500);

        var lines = Encoding.ASCII.GetString(run.SerialOf("USART1")).Split("\r\n");
        Assert.Equal("12:00:00 (0 ms since start-up)", lines[0]);
        Assert.Matches(@"^12:00:01 \(100\d ms since start-up\)$", lines[1]);
        Assert.Matches(@"^12:00:02 \(200\d ms since start-up\)$", lines[2]);
        Assert.Matches(@"^12:00:03 \(300\d ms since start-up\)$", lines[3]);
    }

    [Fact]
    public void There_are_examples_for_the_CH32V003_board_each_with_a_README_and_the_portable_API_alone()
    {
        Assert.Equal(
            [
                "ch32v003-blink", "ch32v003-button", "ch32v003-clock", "ch32v003-serial", "ch32v003-timer-blink",
                "ch32v003-traffic-light",
            ],
            Examples());
        foreach (var example in Examples())
        {
            var folder = Path.Combine(Repository.Examples, example);
            var readme = File.ReadAllText(Path.Combine(folder, "README.md"));
            Assert.Contains("chipsharp build", readme, StringComparison.Ordinal);
            Assert.Contains("-t ch32v003-evt", readme, StringComparison.Ordinal);
            Assert.Contains("chipsharp flash", readme, StringComparison.Ordinal);
            Assert.Contains("chipsim ch32v003", readme, StringComparison.Ordinal);

            // nothing of the CH32V003's own: no calls of Registers, only ChipSharp.Hardware
            var program = File.ReadAllText(Path.Combine(folder, "Program.cs"));
            Assert.DoesNotMatch(@"Registers\.\w+\(", program);
            Assert.Contains(@"..\..\src\ChipSharp.Hardware\ChipSharp.Hardware.csproj",
                File.ReadAllText(ExampleProjects.Project(example)), StringComparison.Ordinal);
        }
    }

    /// <summary>What a build of an example printed: the .bin, and what it takes of the chip.</summary>
    private sealed record BuiltExample(string Name, string Bin, string Size, double Flash, int Ram)
    {
        public override string ToString() => $"{Name}: {Size}";
    }

    /// <summary>
    /// Builds examples/<paramref name="example"/> with dotnet build and chipsharp build -t ch32v003-evt; returns its
    /// .bin and the memory report, which the test's output shows. (A program bigger than the board's memory fails the
    /// build, saying by how much.)
    /// </summary>
    private async Task<BuiltExample> BuildAsync(string example)
    {
        var project = ExampleProjects.Project(example);
        var dll = await ExampleProjects.DotnetBuildAsync(project, _scratch);

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "ch32v003-evt", "-o",
            Path.Combine(_scratch, "ch32v003-evt"));

        Assert.True(exitCode == 0, error);
        var lines = printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(Path.GetFileNameWithoutExtension(project) + ".bin", Path.GetFileName(lines[0]));
        var size = MemoryReport().Match(lines[1]);
        Assert.True(size.Success, lines[1]);
        var flash = double.Parse(size.Groups["flash"].Value, CultureInfo.InvariantCulture)
            * (size.Groups["unit"].Value == "KB" ? 1024 : 1);
        var built = new BuiltExample(example, lines[0], lines[1], flash,
            int.Parse(size.Groups["ram"].Value, CultureInfo.InvariantCulture));
        output.WriteLine(built.ToString());
        return built;
    }

    /// <summary>
    /// Runs the example in chipsim's CH32V003 with the command line's options; it must still be running when the time
    /// is up (each loops forever), never stopped by a failure.
    /// </summary>
    private static SimulationResult Run(BuiltExample example, double milliseconds, params string[] options)
    {
        var run = Simulation.Run(SimulationOptions.Parse("ch32v003",
            [example.Bin, "--ms", milliseconds.ToString(CultureInfo.InvariantCulture), .. options]));
        Assert.True(run.ExitCode == 0, $"{example}\n{run}");
        Assert.True(run.Stopped.EndsWith(" ms of simulated time have passed", StringComparison.Ordinal),
            $"{example}\n{run}");
        return run;
    }

    private static bool IsHigh(IReadOnlyList<PinChange> pins, string pin, double at) =>
        pins.LastOrDefault(p => p.Pin == pin && p.Milliseconds <= at && p.State is "low" or "high")?.State == "high";

    /// <summary>The digit the traffic light's display shows at <paramref name="at"/> ms, or -1.</summary>
    private static int Shown(IReadOnlyList<PinChange> pins, double at)
    {
        var segments = Enumerable.Range(0, 7).Select(i => IsHigh(pins, Segments[i], at) ? 1 << i : 0).Sum();
        return Array.IndexOf(Digits, (byte)segments);
    }

    private static string[] Examples() =>
    [
        .. Directory.GetDirectories(Repository.Examples, "ch32v003-*").Select(folder => Path.GetFileName(folder)).Order(),
    ];

    [GeneratedRegex(@"^flash (?<flash>\d+(\.\d)?) (?<unit>bytes|KB) of 16 KB \(\d+\.\d%\), SRAM (?<ram>\d+) bytes of 2 KB \(stack 512 bytes reserved\)$")]
    private static partial Regex MemoryReport();
}
