using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Tests.Conformance;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Padauk;

/// <summary>
/// The example projects for the Padauk PFS154 (examples/pfs154-*), built the way their READMEs say (dotnet build of the
/// project, then <c>chipsharp build -t pfs154-bare</c> of its .dll) and run in chipsim with what their circuit gives
/// them (a button that the pin's pull-up holds high, pressed): each pin's timeline, and what each takes of the chip's 2K words
/// and 128 bytes, in the test's output and its messages. They use the portable API alone (Pin, Timing, [Interrupt],
/// Pwm): no Registers, nothing of the PFS154's own.
/// </summary>
public sealed partial class Pfs154ExampleTests(ITestOutputHelper output) : IDisposable
{
    private const int Led = 4;          // PA4
    private const int Button = 0;       // PA0

    /// <summary>The segments each digit lights, bit 0 = A.</summary>
    private static readonly byte[] Digits = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];

    /// <summary>The PFS154's pin names by number (port * 8 + bit), as the target file gives them.</summary>
    private static readonly string[] Names =
        ["PA0", "", "", "PA3", "PA4", "PA5", "PA6", "PA7", "PB0", "PB1", "PB2", "PB3", "PB4", "PB5", "PB6", "PB7"];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [SdccFact]
    [RunsLong]
    public async Task The_blink_toggles_PA4_every_half_second()
    {
        var example = await BuildAsync("pfs154-blink");

        var run = await RunAsync(example, new ChipStimulus(1600));

        var led = run.Levels(Led);
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "high", "low", "high", "low"]), $"{example}\n{run}");
        Assert.InRange(led[1].Milliseconds - led[0].Milliseconds, 0, 0.1); // Pin.Output, then the first toggle at once
        for (var i = 2; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds - led[i - 1].Milliseconds, 500, 500.2);
        }
    }

    [SdccFact]
    [RunsLong]
    public async Task The_timer_blink_toggles_PA4_from_the_timer_event_every_half_second()
    {
        var example = await BuildAsync("pfs154-timer-blink");

        var run = await RunAsync(example, new ChipStimulus(1600));

        var led = run.Levels(Led);
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "high", "low", "high"]), $"{example}\n{run}");
        for (var i = 1; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds - led[0].Milliseconds, 500 * i, 500 * i + 0.3);
        }
    }

    /// <summary>
    /// The button on PA0, to GND, with the pin's own pull-up (Pull.Up) that holds it high (the simulator reads it high
    /// from the start, undriven): pressed at 200, 500 and 800 ms, let go 100 ms or 50 ms later.
    /// </summary>
    [SdccFact]
    [RunsLong]
    public async Task The_button_on_PA0_toggles_PA4_on_each_press_with_the_pins_own_pull_up()
    {
        var example = await BuildAsync("pfs154-button");

        var run = await RunAsync(example, new ChipStimulus(1000)
        {
            Inputs =
            [
                new(Button, false, 200), new(Button, true, 300), new(Button, false, 500),
                new(Button, true, 600), new(Button, false, 800), new(Button, true, 850),
            ],
        });

        // the program turns PA0's own pull-up on: Pin.Input(Button, Pull.Up)
        Assert.Contains(run.Pins, p => p.Pin == Button && p.State == "input pull-up");
        var led = run.Levels(Led);
        Assert.True(led.Select(p => p.State).SequenceEqual(["low", "high", "low", "high"]), $"{example}\n{run}");
        double[] presses = [200, 500, 800];
        for (var i = 0; i < presses.Length; i++)
        {
            Assert.InRange(led[i + 1].Milliseconds, presses[i], presses[i] + 0.15); // the pin event, at once
        }
    }

    /// <summary>
    /// The traffic light, its button pressed at 2.5 s, during the first green, for 16 s: which light is on and what the
    /// turn display shows at the times the Uno's test looks, the green the press ends, the heartbeat; and room in RAM
    /// for its stack.
    /// </summary>
    [SdccFact]
    [RunsLong]
    public async Task The_traffic_lights_signal_display_heartbeat_and_button_work_as_on_the_Uno()
    {
        var example = await BuildAsync("pfs154-traffic-light");

        var run = await RunAsync(example, new ChipStimulus(16000)
        {
            Inputs = [new(Button, false, 2500), new(Button, true, 2700)],
        });
        var pins = run.Pins;

        // which light is on (red PA3, yellow PA6, green PA7) and what the turn display (PB1-PB7) shows: the Uno's turn,
        // its seconds counted with 10 ms delays, a few ms later each second
        (double At, string Light, int Turn)[] expected =
        [
            (500, "PA7", 8), (2400, "PA7", 6), (2600, "PA6", 4), (4000, "PA3", 3), (6000, "PA3", 1),
            (7000, "PA7", 8), (11000, "PA6", 4), (13000, "PA3", 2), (15000, "PA7", 8),
        ];
        foreach (var (at, light, turn) in expected)
        {
            var on = string.Join(' ', new[] { 3, 6, 7 }.Where(pin => IsHigh(pins, pin, at)).Select(pin => Names[pin]));
            Assert.Equal((at, light, turn), (at, on, Shown(pins, at)));
        }

        // the press ends the green within one 10 ms wait; each light's changes as the steps say, a second a few ms long
        var greenOff = pins.First(p => p.Pin == 7 && p.State == "low" && p.Milliseconds > 1000).Milliseconds;
        Assert.InRange(greenOff, 2500, 2511);
        double Green(string state, double after) =>
            pins.First(p => p.Pin == 7 && p.State == state && p.Milliseconds > after).Milliseconds;
        Assert.InRange(Green("high", 3000), 6500, 6560);
        Assert.InRange(Green("low", 7000), 10500, 10600);

        // the button's pin with its pull-up on (no resistor); PA4 changes every 500 ms from the timer
        Assert.Contains(pins, p => p.Pin == Button && p.State == "input pull-up");
        var heartbeat = run.Levels(Led).Skip(1).ToList();
        Assert.InRange(heartbeat.Count, 30, 32);
        for (var i = 1; i < heartbeat.Count; i++)
        {
            Assert.InRange(heartbeat[i].Milliseconds - heartbeat[i - 1].Milliseconds, 499.9, 500.1);
        }

        // the stack grows up from the variables' end to the end of RAM: at its deepest (the timer event while
        // TurnDisplay writes a pin) it takes about 48 bytes, which chipsim sees only when the two meet
        Assert.True(128 - example.Ram >= 48, $"{example}: too little RAM left for the stack");
    }

    /// <summary>
    /// The fade: PB5 a PWM output at 1008 Hz (the nearest TM3 makes to 1000), its pulse 20 us longer every 20 ms up to
    /// the whole period (steady high), then shorter down to none (steady low), and again.
    /// </summary>
    [SdccFact]
    [RunsLong]
    public async Task The_fade_makes_PB5s_pulse_grow_to_the_whole_period_and_shrink_back()
    {
        var example = await BuildAsync("pfs154-pwm-fade");

        var run = await RunAsync(example, new ChipStimulus(2100));

        const int Pb5 = 13;
        var periods = run.Pwm.Where(period => period.Pin == Pb5).ToList();
        Assert.All(periods.Where(period => period.Hertz > 0), period => Assert.InRange(period.Hertz, 1007, 1009));
        Assert.Contains("PB5 PWM steady high", run.Output, StringComparison.Ordinal);
        // up to the steady high at about 1 s, down to the steady low at about 2 s: 6-bit PWM, 15.5 us a count
        var top = periods.FindIndex(period => period.Hertz == 0 && period.Milliseconds > 900);
        var bottom = periods.FindIndex(period => period.Hertz == 0 && period.Milliseconds > 1900);
        Assert.True(top > 40 && bottom > top + 40, $"{example}\n{run}");
        Assert.InRange(periods[top].Milliseconds, 1000, 1040);
        Assert.InRange(periods[bottom].Milliseconds, 2000, 2080);
        var up = periods.Take(top).Where(period => period.Hertz > 0).Select(period => period.HighMicroseconds).ToList();
        var down = periods.Take(bottom).Skip(top + 1).Select(period => period.HighMicroseconds).ToList();
        Assert.Equal(up.Order(), up);
        Assert.Equal(down.OrderDescending(), down);
        Assert.InRange(up[0], 15, 16);
        Assert.InRange(up[^1], 975, 978);
        Assert.InRange(down[^1], 15, 16);
    }

    [Fact]
    public void There_are_examples_for_the_PFS154_each_with_a_README_and_the_portable_API_alone()
    {
        Assert.Equal(["pfs154-blink", "pfs154-button", "pfs154-pwm-fade", "pfs154-timer-blink", "pfs154-traffic-light"],
            Examples());
        foreach (var example in Examples())
        {
            var folder = Path.Combine(Repository.Examples, example);
            var readme = File.ReadAllText(Path.Combine(folder, "README.md"));
            Assert.Contains("chipsharp build", readme, StringComparison.Ordinal);
            Assert.Contains("-t pfs154-bare", readme, StringComparison.Ordinal);
            Assert.Contains("chipsharp flash", readme, StringComparison.Ordinal);
            Assert.Contains("chipsim pfs154", readme, StringComparison.Ordinal);

            // nothing of the PFS154's own: no calls of Registers, only ChipSharp.Hardware
            var program = File.ReadAllText(Path.Combine(folder, "Program.cs"));
            Assert.DoesNotMatch(@"Registers\.\w+\(", program);
            Assert.Equal(["ChipSharp.Hardware"], ProjectReferences(ExampleProjects.Project(example)));
        }
    }

    /// <summary>What a build of an example printed: the .hex, and what it takes of the chip.</summary>
    private sealed record BuiltExample(string Name, string Hex, string Size, int Words, int Ram)
    {
        public override string ToString() => $"{Name}: {Size}";
    }

    /// <summary>
    /// Builds examples/<paramref name="example"/> with dotnet build and chipsharp build -t pfs154-bare; returns its .hex
    /// and the memory report, which the test's output shows.
    /// </summary>
    private async Task<BuiltExample> BuildAsync(string example)
    {
        var project = ExampleProjects.Project(example);
        var dll = await ExampleProjects.DotnetBuildAsync(project, _scratch);

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "pfs154-bare", "-o",
            Path.Combine(_scratch, "pfs154-bare"));

        Assert.True(exitCode == 0, error);
        var lines = printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(Path.GetFileNameWithoutExtension(project) + ".hex", Path.GetFileName(lines[0]));
        var size = MemoryReport().Match(lines[1]);
        Assert.True(size.Success, lines[1]);
        var built = new BuiltExample(example, lines[0], lines[1], Number(size.Groups["words"].Value),
            Number(size.Groups["ram"].Value));
        output.WriteLine(built.ToString());
        return built;
    }

    /// <summary>
    /// Runs the example in chipsim; it must still be running when the time is up (each loops forever), never stopped
    /// by a failure or a stack grown past the end of RAM.
    /// </summary>
    private async Task<ChipRun> RunAsync(BuiltExample example, ChipStimulus stimulus)
    {
        var run = await ChipSimulators.Get("pfs154-bare").RunAsync(example.Hex, stimulus);
        Assert.True(run.Stop == ChipStop.TimeUp, $"{example}\n{run}");
        return run;
    }

    private static int Number(string text) => int.Parse(text, CultureInfo.InvariantCulture);

    private static bool IsHigh(IReadOnlyList<ChipPinChange> pins, int pin, double at) =>
        pins.LastOrDefault(p => p.Pin == pin && p.Milliseconds <= at && p.State is "low" or "high")?.State == "high";

    /// <summary>The digit the turn display shows at <paramref name="at"/> (A to G on PB1 to PB7), or -1.</summary>
    private static int Shown(IReadOnlyList<ChipPinChange> pins, double at)
    {
        var segments = Enumerable.Range(0, 7).Select(i => IsHigh(pins, 9 + i, at) ? 1 << i : 0).Sum();
        return Array.IndexOf(Digits, (byte)segments);
    }

    private static string[] Examples() =>
    [
        .. Directory.GetDirectories(Repository.Examples, "pfs154-*").Select(folder => Path.GetFileName(folder)).Order(),
    ];

    private static string[] ProjectReferences(string project) =>
    [
        .. ProjectReference().Matches(File.ReadAllText(project)).Select(match => match.Groups["name"].Value),
    ];

    [GeneratedRegex(@"^program (?<words>\d+) words of 2048 words \(\d+\.\d%\), RAM (?<ram>\d+) bytes of 128 bytes \(stack 48 bytes reserved\)$")]
    private static partial Regex MemoryReport();

    [GeneratedRegex(@"<ProjectReference Include=""[^""]*\\(?<name>[^\\""]+)\.csproj""")]
    private static partial Regex ProjectReference();
}
