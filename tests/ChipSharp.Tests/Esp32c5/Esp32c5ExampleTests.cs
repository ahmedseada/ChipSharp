using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChipSharp.Simulators;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;
using PinChange = ChipSharp.Simulators.PinChange;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// The example projects for the ESP32-C5-DevKitC-1 (examples/esp32c5-*), built the way their READMEs say (dotnet build of
/// the project, then <c>chipsharp build -t esp32c5-devkitc</c> of its .dll) and run in chipsim's esp32c5 with what their
/// circuit gives them (a button pressed, commands typed on the USB console): each pin's timeline and what each prints
/// on the console, and what each takes of the chip's SRAM, in the test's output and its messages. They use the portable
/// API alone (Pin, Timing, [Interrupt], Console): no Registers, nothing of the C5's own, and only pins the board leaves
/// a program.
/// </summary>
public sealed partial class Esp32c5ExampleTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The pins the board file leaves a program: what <c>chipsharp check</c> accepts.</summary>
    private static readonly int[] FreePins = [0, 1, 4, 5, 6, 8, 9, 10, 23, 24];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    /// <summary>The external LED on GPIO 4: Pin.Output, the first toggle at once, then a change every 500 ms.</summary>
    [PicoFact]
    [RunsLong]
    public async Task The_blink_toggles_GPIO4_every_half_second()
    {
        var example = await BuildAsync("esp32c5-blink");

        var run = Run(example, 1600);

        var led = Led(run, 4);
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "high", "low", "high", "low"]), $"{example}\n{run}");
        Assert.InRange(led[1].Milliseconds - led[0].Milliseconds, 0, 0.1);
        for (var i = 2; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds - led[i - 1].Milliseconds, 499.9, 500.2);
        }

        Assert.Empty(Text(run));
    }

    /// <summary>The button to GND on GPIO 5, its pull-up on: each press (a fall) toggles GPIO 4, a release does not.</summary>
    [PicoFact]
    [RunsLong]
    public async Task The_button_on_GPIO5_toggles_GPIO4_on_each_press_with_the_pins_pull_up_on()
    {
        var example = await BuildAsync("esp32c5-button");

        var run = Run(example, 1600, "--input", "GPIO5=high", "--input", "GPIO5=low@200ms", "--input", "GPIO5=high@400ms",
            "--input", "GPIO5=low@600ms", "--input", "GPIO5=high@800ms", "--input", "GPIO5=low@1000ms");

        Assert.Contains(run.Pins, p => p is { Pin: "GPIO5", State: "input pull-up" });
        var led = Led(run, 4);
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "high", "low", "high"]), $"{example}\n{run}");
        Assert.InRange(led[0].Milliseconds, 0, 1);
        Assert.InRange(led[1].Milliseconds, 200, 200.1);
        Assert.InRange(led[2].Milliseconds, 600, 600.1);
        Assert.InRange(led[3].Milliseconds, 1000, 1000.1);
    }

    /// <summary>
    /// The prompt: on, a blink from the timer event, status, off, a bad blink, a command it does not know and status
    /// again: each line is answered, and GPIO 4 follows.
    /// </summary>
    [PicoFact]
    [RunsLong]
    public async Task The_serial_prompt_switches_and_blinks_the_LED_and_answers_each_command()
    {
        var example = await BuildAsync("esp32c5-serial");

        var run = Run(example, 1000, "--send", @"USB=on\n@100ms", "--send", @"USB=blink 200\n@200ms",
            "--send", @"USB=status\n@500ms", "--send", @"USB=off\n@600ms", "--send", @"USB=blink x\n@700ms",
            "--send", @"USB=hello world\n@800ms", "--send", @"USB=status\n@900ms");

        Assert.Equal(
            "ChipSharp on an ESP32-C5. Commands: on, off, blink <ms>, status\r\n" +
            "> LED on\r\n" +
            "> LED blinking every 200 ms\r\n" +
            "> LED blinking, 3 commands\r\n" +
            "> LED off\r\n" +
            "> blink needs a number of milliseconds: blink 200\r\n" +
            "> you said: hello world\r\n" +
            "> LED off, 7 commands\r\n" +
            "> ",
            Text(run));

        // low at start, on at 100 ms, toggled every 200 ms from 200 ms (400 low, 600: off stops the timer first)
        var led = Led(run, 4);
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "high", "low"]), $"{example}\n{run}");
        Assert.InRange(led[1].Milliseconds, 100, 100.1);
        Assert.InRange(led[2].Milliseconds, 400, 400.1);
    }

    /// <summary>The LED toggles every half second from the timer event and Main prints the uptime each second.</summary>
    [PicoFact]
    [RunsLong]
    public async Task The_timer_blink_toggles_from_the_timer_event_while_Main_prints_the_uptime()
    {
        var example = await BuildAsync("esp32c5-timer-blink");

        var run = Run(example, 3500);

        var led = Led(run, 4);
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "high", "low", "high", "low", "high", "low"]),
            $"{example}\n{run}");
        for (var i = 1; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds, 500.0 * i - 1, 500.0 * i + 1);
        }

        Assert.Equal(
            "ChipSharp on an ESP32-C5: the LED blinks from the timer event\r\n" +
            "uptime 1 s (Stopwatch), 1 s (DateTime), 2 toggles\r\n" +
            "uptime 2 s (Stopwatch), 2 s (DateTime), 4 toggles\r\n" +
            "uptime 3 s (Stopwatch), 3 s (DateTime), 6 toggles\r\n",
            Text(run));
    }

    /// <summary>
    /// The traffic light with classes, a record struct, an iterator and a list, its button pressed at 2.5 s during lane
    /// 1's green, for 16 s: the status lines, a second apart, the green the press cuts, which light is on at some times.
    /// </summary>
    [PicoFact]
    [RunsLong]
    public async Task The_classes_traffic_light_prints_its_turns_and_its_button_cuts_a_green()
    {
        var example = await BuildAsync("esp32c5-classes");

        var run = Run(example, 16_000, "--input", "GPIO9=high", "--input", "GPIO9=low@2500ms", "--input", "GPIO9=high@2700ms");

        Assert.Equal(
            "turn 1   signal: green 4\r\nturn 1   signal: green 3\r\nturn 1   signal: green 2\r\n" +
            "turn 1 : green cut short\r\n" +
            "turn 1   signal: yellow 1\r\nturn 1   signal: red 2\r\nturn 1   signal: red 1\r\n" +
            "turn 2   signal: green 4\r\nturn 2   signal: green 3\r\nturn 2   signal: green 2\r\nturn 2   signal: green 1\r\n" +
            "turn 2   signal: yellow 1\r\nturn 2   signal: red 2\r\nturn 2   signal: red 1\r\n" +
            "turn 3   signal: green 4\r\nturn 3   signal: green 3\r\nturn 3   signal: green 2\r\nturn 3   signal: green 1\r\n",
            Text(run));

        // the lines are a second apart (10 ms delays, a few microseconds each more), the cut one at the press
        var lines = run.ToString().Split('\n').Where(line => line.Contains("USB: turn", StringComparison.Ordinal)).ToList();
        var times = lines.Select(line => double.Parse(line[1..line.IndexOf(" ms]", StringComparison.Ordinal)].Trim(),
            CultureInfo.InvariantCulture)).ToList();
        Assert.InRange(times[1] - times[0], 999, 1002);
        Assert.InRange(times[3], 2500, 2503);      // the press at 2.5 s ends lane 1's green at once

        // which lights are on: a lane keeps red while the other has its turn (lane 1: red 4, yellow 5, green 6; lane 2:
        // red 10, yellow 23, green 24)
        (double At, string Lights)[] expected =
        [
            (500, "GPIO6 GPIO10"), (2400, "GPIO6 GPIO10"), (3000, "GPIO5 GPIO10"), (5000, "GPIO4 GPIO10"),
            (7000, "GPIO4 GPIO24"), (10000, "GPIO4 GPIO23"), (11000, "GPIO4 GPIO10"), (13000, "GPIO6 GPIO10"),
            (15000, "GPIO6 GPIO10"),
        ];
        foreach (var (at, lights) in expected)
        {
            var on = string.Join(' ', new[] { "GPIO4", "GPIO5", "GPIO6", "GPIO10", "GPIO23", "GPIO24" }
                .Where(pin => IsHigh(run.Pins, pin, at)));
            Assert.Equal((at, lights), (at, on));
        }
    }

    /// <summary>
    /// Each example has a README with the build, flash and chipsim commands, a Wokwi circuit that wires only the pins
    /// the program uses (directly: LEDs from their pin to GND), and a program of the portable API alone on pins the
    /// board leaves a program.
    /// </summary>
    [Fact]
    public void Each_example_has_a_README_a_circuit_and_a_program_of_the_portable_API_on_free_pins()
    {
        Assert.Equal(["esp32c5-blink", "esp32c5-button", "esp32c5-classes", "esp32c5-serial", "esp32c5-timer-blink"],
            Examples());
        foreach (var example in Examples())
        {
            var folder = Path.Combine(Repository.Examples, example);
            var readme = File.ReadAllText(Path.Combine(folder, "README.md"));
            Assert.Contains("chipsharp build", readme, StringComparison.Ordinal);
            Assert.Contains("-t esp32c5-devkitc", readme, StringComparison.Ordinal);
            Assert.Contains("chipsharp flash", readme, StringComparison.Ordinal);
            Assert.Contains("chipsim esp32c5", readme, StringComparison.Ordinal);
            Assert.Contains("not been tried in Wokwi", readme, StringComparison.Ordinal);

            // nothing of the C5's own: no calls of Registers, only ChipSharp.Hardware (and ChipSharp.Components)
            var program = File.ReadAllText(Path.Combine(folder, "Program.cs"));
            Assert.DoesNotMatch(@"Registers\.\w+\(", program);
            Assert.Subset(new HashSet<string> { "ChipSharp.Hardware", "ChipSharp.Components" },
                ProjectReferences(ExampleProjects.Project(example)).ToHashSet());

            // the pins it names (Pin.Output(4), "Red: 4", ...) are among those the board leaves a program
            var diagram = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "diagram.json")))!.AsObject();
            Assert.Equal("board-esp32-c5-devkitc-1", (string?)diagram["parts"]![0]!["type"]);
            var wires = diagram["connections"]!.AsArray().Select(wire => (From: (string)wire![0]!, To: (string)wire[1]!)).ToList();
            foreach (var (from, to) in wires)
            {
                foreach (var end in new[] { from, to }.Where(end => end.StartsWith("esp:", StringComparison.Ordinal)))
                {
                    var name = end["esp:".Length..];
                    Assert.True(name.StartsWith("GND.", StringComparison.Ordinal) ||
                        FreePins.Contains(int.Parse(name, CultureInfo.InvariantCulture)), $"{example}: {end}");
                }
            }

            // every LED has its cathode on a GND pin and its anode on a pin
            foreach (var led in diagram["parts"]!.AsArray().Where(part => (string?)part!["type"] == "wokwi-led"))
            {
                var id = (string)led!["id"]!;
                Assert.Contains(wires, wire => wire.From == $"{id}:C" && wire.To.StartsWith("esp:GND.", StringComparison.Ordinal));
                Assert.Contains(wires, wire => wire.From == $"esp:{(string)led["attrs"]!["label"]!}" && wire.To == $"{id}:A");
            }
        }
    }

    /// <summary>What a build of an example printed: the .bin, and what it takes of the chip.</summary>
    private sealed record BuiltExample(string Name, string Bin, string Size, long Bytes)
    {
        public override string ToString() => $"{Name}: {Size}, {Bytes} bytes of flash";
    }

    /// <summary>
    /// Builds examples/<paramref name="example"/> with dotnet build and chipsharp build -t esp32c5-devkitc; returns its
    /// .bin and the memory report, which the test's output shows.
    /// </summary>
    private async Task<BuiltExample> BuildAsync(string example)
    {
        var project = ExampleProjects.Project(example);
        var dll = await ExampleProjects.DotnetBuildAsync(project, _scratch);

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "esp32c5-devkitc", "-o",
            Path.Combine(_scratch, "esp32c5-devkitc"));

        Assert.True(exitCode == 0, error);
        var lines = printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(Path.GetFileNameWithoutExtension(project) + ".bin", Path.GetFileName(lines[0]));
        Assert.True(MemoryReport().IsMatch(lines[1]), lines[1]);
        var built = new BuiltExample(example, lines[0], lines[1], new FileInfo(lines[0]).Length);
        output.WriteLine(built.ToString());
        return built;
    }

    /// <summary>
    /// Runs the example in chipsim's ESP32-C5 with the command line's options; it must still be running when the time
    /// is up (each loops forever, or waits for a line), never stopped by a failure.
    /// </summary>
    private static SimulationResult Run(BuiltExample example, double milliseconds, params string[] options)
    {
        var run = Simulation.Run(SimulationOptions.Parse("esp32c5",
            [example.Bin, "--ms", milliseconds.ToString(CultureInfo.InvariantCulture), .. options]));
        Assert.True(run.ExitCode == 0, $"{example}\n{run}");
        Assert.DoesNotContain("failed", run.Stopped, StringComparison.Ordinal);
        return run;
    }

    /// <summary>The pin's highs and lows.</summary>
    private static List<PinChange> Led(SimulationResult run, int gpio) =>
        [.. run.Pins.Where(p => p.Pin == "GPIO" + gpio.ToString(CultureInfo.InvariantCulture) && p.State is "low" or "high")];

    /// <summary>What the program printed on the USB console.</summary>
    private static string Text(SimulationResult run) => Encoding.ASCII.GetString(run.SerialOf("USB"));

    /// <summary>True when the pin's last high or low at or before <paramref name="at"/> ms was high.</summary>
    private static bool IsHigh(IReadOnlyList<PinChange> pins, string pin, double at) =>
        pins.LastOrDefault(p => p.Pin == pin && p.Milliseconds <= at && p.State is "low" or "high")?.State == "high";

    private static string[] Examples() =>
    [
        .. Directory.GetDirectories(Repository.Examples, "esp32c5-*").Select(folder => Path.GetFileName(folder)).Order(),
    ];

    private static string[] ProjectReferences(string project) =>
    [
        .. ProjectReference().Matches(File.ReadAllText(project)).Select(match => match.Groups["name"].Value),
    ];

    [GeneratedRegex(@"^SRAM \d+(\.\d)? KB( \+ heap \d+ KB)? of 369\.4 KB \(\d+\.\d%\) \(stack 16 KB reserved\)$")]
    private static partial Regex MemoryReport();

    [GeneratedRegex(@"<ProjectReference Include=""[^""]*\\(?<name>[^\\""]+)\.csproj""")]
    private static partial Regex ProjectReference();
}
