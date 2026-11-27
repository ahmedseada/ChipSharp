using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ChipSharp.Image.Uf2;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The two traffic lights, examples/pico-traffic-light (no heap: static methods, arrays passed down, a ref parameter)
/// and examples/pico-traffic-light-m3 (classes, a list, an enum, a table of steps, an event), built the way their
/// Program.cs says (dotnet build of the project, then <c>chipsharp build -t pico</c>) and run in the simulator for 16
/// seconds with the button on GPIO 22 pressed at 2.5 s, during lane 1's green: each counts down over UART0, on the
/// lights and on both displays, the press cuts the green short, the two do exactly the same, and only the M3 one has a
/// heap.
/// </summary>
public sealed class TrafficLightExampleTests(TrafficLightExampleTests.Builds builds)
    : IClassFixture<TrafficLightExampleTests.Builds>
{
    private const string HeapFree = "pico-traffic-light";
    private const string WithClasses = "pico-traffic-light-m3";
    private const int Milliseconds = 16000;

    /// <summary>The button pressed at 2.5 s, for 200 ms (the program reads it through a pin interrupt).</summary>
    private static readonly string[] Button = ["--input", "22=high@2500ms", "--input", "22=low@2700ms"];

    /// <summary>What both print over UART0 in 16 s, with the press at 2.5 s.</summary>
    private static readonly string Expected = string.Concat(new[]
    {
        "road : 1", "    green 4", "    green 3", "    green 2", "lane : 1 interrupted, move to lane 2",
        "    yellow 1", "    red 2", "    red 1",
        "road : 2", "    green 4", "    green 3", "    green 2", "    green 1", "    yellow 1", "    red 2", "    red 1",
        "road : 1", "    green 4", "    green 3",
    }.Select(line => line + "\r\n"));

    /// <summary>The lights: lane 1 red, yellow, green on GPIO 16 to 18, lane 2's on GPIO 19 to 21.</summary>
    private static readonly int[] Lights = [16, 17, 18, 19, 20, 21];

    /// <summary>The segments each digit lights, bit 0 = A: the examples' own table.</summary>
    private static readonly byte[] Digits = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];

    public static TheoryData<string> Examples() => new(HeapFree, WithClasses);

    [SimulatorTheory]
    [MemberData(nameof(Examples))]
    public async Task Each_traffic_light_builds_for_the_pico_and_counts_down_over_the_serial_port(string example)
    {
        var build = await builds.GetAsync(example);

        Assert.True(build.Run.ExitCode == 0, build.Run.ToString());
        Assert.EndsWith($"stopped: {Milliseconds} ms of simulated time have passed", build.Run.Stopped);
        Assert.Equal(Expected, Encoding.UTF8.GetString(build.Run.Uart0));
    }

    [SimulatorTheory]
    [MemberData(nameof(Examples))]
    public async Task Each_traffic_light_shows_the_lights_and_the_countdowns_and_the_button_cuts_the_green_short(string example)
    {
        var pins = (await builds.GetAsync(example)).Run.Pins;

        // which lights are on (lane 1 red 16, yellow 17, green 18; lane 2 red 19, yellow 20, green 21), and what the
        // step display (GPIO 2-8) and the turn display (GPIO 9-15) show; null is a blank display
        (double At, string On, int? Step, int? Turn)[] expected =
        [
            (500, "18 19", 4, 8),         // lane 1 green, its turn 8 s long
            (2400, "18 19", 2, 6),
            (2600, "17 19", 1, 4),        // the press at 2.5 s: yellow at once, and the turn has 4 s left
            (4000, "16 19", 2, 3),        // both red
            (6000, "16 19", null, 1),     // the pause before lane 2: the step display blank
            (7000, "16 21", 4, 8),        // lane 2 green, for all of its 4 s
            (11000, "16 20", 1, 4),
            (13000, "16 19", 1, 2),
            (15000, "18 19", 4, 8),       // lane 1 again
        ];
        foreach (var (at, on, step, turn) in expected)
        {
            var lightsOn = string.Join(' ', Lights.Where(light => IsHigh(pins, light, at)));
            Assert.Equal((at, on, step, turn), (at, lightsOn, Shown(pins, 2, at), Shown(pins, 9, at)));
        }

        // the green ends within one 10 ms wait of the press
        var greenOff = pins.First(p => p.Pin == 18 && p.State == "low" && p.Milliseconds > 1000).Milliseconds;
        Assert.InRange(greenOff, 2500, 2511);
    }

    [SimulatorFact]
    public async Task The_two_traffic_lights_print_exactly_the_same()
    {
        var heapFree = await builds.GetAsync(HeapFree);
        var withClasses = await builds.GetAsync(WithClasses);

        Assert.Equal(heapFree.Run.Uart0, withClasses.Run.Uart0);
    }

    [SimulatorFact]
    public async Task Only_the_M3_traffic_light_has_a_heap()
    {
        var heapFree = await builds.GetAsync(HeapFree);
        var withClasses = await builds.GetAsync(WithClasses);

        Assert.Matches(@"^flash \d+(\.\d)? KB of 2048 KB \(\d+\.\d%\), RAM \d+ bytes of 264 KB \(stack 8 KB reserved\)$",
            heapFree.Report);
        Assert.Matches(@"^flash \d+(\.\d)? KB of 2048 KB \(\d+\.\d%\), RAM \d+(\.\d)? (bytes|KB) \+ heap 224 KB of 264 KB " +
            @"\(stack 8 KB reserved\)$", withClasses.Report);

        // no heap region and none of the collector's code
        Assert.DoesNotContain("__chipsharp_heap_start", heapFree.Elf.Symbols.Keys);
        Assert.DoesNotContain("chipsharp_new_object", heapFree.Elf.Symbols.Keys);
        Assert.Contains("__chipsharp_heap_start", withClasses.Elf.Symbols.Keys);
        Assert.Contains("chipsharp_new_object", withClasses.Elf.Symbols.Keys);
    }

    [Fact]
    public void Each_traffic_lights_Wokwi_diagram_wires_the_pins_its_program_uses()
    {
        var diagram = File.ReadAllText(Path.Combine(Repository.Examples, HeapFree, "diagram.json"));
        Assert.Equal(diagram, File.ReadAllText(Path.Combine(Repository.Examples, WithClasses, "diagram.json")));

        using var json = JsonDocument.Parse(diagram);
        var parts = json.RootElement.GetProperty("parts").EnumerateArray()
            .ToDictionary(part => part.GetProperty("id").GetString()!, part => part.Clone());
        var nets = new Nets(json.RootElement.GetProperty("connections").EnumerateArray()
            .Select(wire => (wire[0].GetString()!, wire[1].GetString()!)));

        Assert.Equal("wokwi-pi-pico", Type("pico"));

        // a wire between the two pins (Wokwi needs no resistors), or a resistor of 220 ohms between them, either way
        // round (two-pin parts are symmetric)
        void Wired(string from, string to)
        {
            if (nets.Joined(from, to))
            {
                return;
            }

            var resistors = parts.Keys.Where(id => Type(id) == "wokwi-resistor" &&
                (nets.Joined(from, $"{id}:1") && nets.Joined(to, $"{id}:2") ||
                    nets.Joined(from, $"{id}:2") && nets.Joined(to, $"{id}:1"))).ToList();
            Assert.True(resistors.Count == 1, $"{from} does not reach {to} (by a wire or through one resistor)");
            Assert.Equal("220", parts[resistors[0]].GetProperty("attrs").GetProperty("value").GetString());
        }

        bool TryGrounded(string pin) => Enumerable.Range(1, 8).Any(n => nets.Joined(pin, $"pico:GND.{n}"));

        void Grounded(string pin) => Assert.True(TryGrounded(pin), $"{pin} is not connected to a GND of the Pico");

        // the displays: segments A..G of the step display on GP2-GP8, of the turn display on GP9-GP15, common cathode
        foreach (var (display, first) in new[] { ("step", 2), ("turn", 9) })
        {
            Assert.Equal("wokwi-7segment", Type(display));
            Assert.Equal("cathode", parts[display].GetProperty("attrs").GetProperty("common").GetString());
            for (var segment = 0; segment < 7; segment++)
            {
                Wired($"pico:GP{first + segment}", $"{display}:{(char)('A' + segment)}");
            }

            // COM.1 and COM.2 are the same common pin: one of them to GND is enough
            Assert.True(TryGrounded($"{display}:COM.1") || TryGrounded($"{display}:COM.2"),
                $"neither common pin of {display} is connected to a GND of the Pico");
        }

        // the lights: GPIO -> the LED's anode (directly or through a resistor), its cathode to GND; whichever LED a pin
        // reaches must have that pin's colour
        foreach (var (pin, color) in new[] { (16, "red"), (17, "yellow"), (18, "green"), (19, "red"), (20, "yellow"),
            (21, "green") })
        {
            var lights = parts.Keys.Where(id => Type(id) == "wokwi-led" && Reaches($"pico:GP{pin}", $"{id}:A")).ToList();
            Assert.True(lights.Count == 1, $"GP{pin} reaches no LED's anode (or more than one)");
            Assert.Contains(color, parts[lights[0]].GetProperty("attrs").GetProperty("color").GetString());
            Grounded($"{lights[0]}:C");
        }

        // the button between GP22 and 3V3, which does not bounce (a press is one interrupt)
        var button = Assert.Single(parts.Keys, id => Type(id) == "wokwi-pushbutton");
        Assert.Equal("0", parts[button].GetProperty("attrs").GetProperty("bounce").GetString());
        // (its two contacts are the same either way round)
        Assert.True(nets.Joined("pico:GP22", $"{button}:1.l") && nets.Joined("pico:3V3", $"{button}:2.l") ||
            nets.Joined("pico:GP22", $"{button}:2.l") && nets.Joined("pico:3V3", $"{button}:1.l"));

        bool Reaches(string from, string to) => nets.Joined(from, to) || parts.Keys.Any(id =>
            Type(id) == "wokwi-resistor" && (nets.Joined(from, $"{id}:1") && nets.Joined(to, $"{id}:2") ||
                nets.Joined(from, $"{id}:2") && nets.Joined(to, $"{id}:1")));

        // UART0, where Console.WriteLine goes, in Wokwi's serial monitor
        Assert.True(nets.Joined("pico:GP0", "$serialMonitor:RX") && nets.Joined("pico:GP1", "$serialMonitor:TX"));

        string? Type(string id) => parts.TryGetValue(id, out var part) ? part.GetProperty("type").GetString() : null;
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

    /// <summary>True when the last high or low of <paramref name="pin"/> at or before <paramref name="at"/> ms was high.</summary>
    private static bool IsHigh(IReadOnlyList<PinChange> pins, int pin, double at) =>
        pins.LastOrDefault(p => p.Pin == pin && p.Milliseconds <= at && p.State is "high" or "low")?.State == "high";

    /// <summary>The digit a display whose segments A..G are on GPIO first to first + 6 shows; null when it is blank.</summary>
    private static int? Shown(IReadOnlyList<PinChange> pins, int first, double at)
    {
        var pattern = Enumerable.Range(0, 7).Where(segment => IsHigh(pins, first + segment, at))
            .Aggregate(0, (bits, segment) => bits | 1 << segment);
        if (pattern == 0)
        {
            return null;
        }

        var digit = Array.IndexOf(Digits, (byte)pattern);
        Assert.True(digit >= 0, $"GPIO {first} to {first + 6} show 0x{pattern:X2} at {at} ms, which is no digit");
        return digit;
    }

    /// <summary>One traffic light built for the Pico and run in the simulator.</summary>
    internal sealed record Build(string Report, ElfFile Elf, SimulatorRun Run);

    /// <summary>
    /// Builds and runs each example once for every test of the class (the first test that asks for it does), in a
    /// scratch folder deleted after the last.
    /// </summary>
    public sealed class Builds : IDisposable
    {
        private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
        private readonly ConcurrentDictionary<string, Lazy<Task<Build>>> _builds = new();

        public void Dispose()
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }

        internal Task<Build> GetAsync(string example) =>
            _builds.GetOrAdd(example, name => new Lazy<Task<Build>>(() => BuildAndRunAsync(name))).Value;

        private async Task<Build> BuildAndRunAsync(string example)
        {
            var scratch = Path.Combine(_scratch, example);
            var project = ExampleProjects.Project(example);
            var dll = await ExampleProjects.DotnetBuildAsync(project, scratch);

            // --keep: the .elf too, for its symbols
            var output = Path.Combine(scratch, "pico");
            var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "pico", "-o", output, "--keep");
            Assert.True(exitCode == 0, error);
            var lines = printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var name = Path.GetFileNameWithoutExtension(project);
            Assert.Equal(name + ".uf2", Path.GetFileName(lines[0]));

            var run = await Rp2040Simulator.RunAsync(lines[0], Milliseconds, Button);
            return new Build(lines[^1], ElfFile.Read(Path.Combine(output, name + ".elf")), run);
        }
    }
}
