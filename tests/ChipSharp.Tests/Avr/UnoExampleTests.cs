using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The example projects for the Arduino Uno (examples/uno-*), built the way the README says (dotnet build of the
/// project, then <c>chipsharp build -t uno</c> of its .dll) and run in the avr8js simulator with what their Wokwi
/// circuit would give them (a button pressed, a knob turned, an LCD and a real-time clock on the I2C bus); and their
/// circuits (diagram.json): the Uno's own pins wired directly to the parts, on the pins the program uses, with no wire
/// across a part. The projects that show whole circuits (the traffic light, the clock, the servo) are in
/// UnoExampleTests.Projects.cs.
/// </summary>
public sealed partial class UnoExampleTests : IDisposable
{
    private const int Milliseconds = 1600;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [AvrSimulatorFact]
    [RunsLong]
    public async Task The_blink_toggles_D13_every_half_second()
    {
        var hex = await BuildAsync("uno-blink");

        var run = await AvrSimulator.RunAsync(hex, Milliseconds);

        var led = Led(run);
        Assert.Equal(["low", "high", "low", "high", "low"], led.Select(p => p.State));
        Assert.InRange(led[1].Milliseconds, 0, 0.1); // Pin.Output, then the first toggle at once
        for (var i = 2; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds - led[i - 1].Milliseconds, 500, 500.1);
        }
    }

    [AvrSimulatorFact]
    [RunsLong]
    public async Task The_timer_blink_toggles_D13_from_the_timer_event_every_half_second()
    {
        var hex = await BuildAsync("uno-timer-blink");

        var run = await AvrSimulator.RunAsync(hex, Milliseconds);

        var led = Led(run);
        Assert.Equal(["low", "high", "low", "high"], led.Select(p => p.State));
        for (var i = 1; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds, 500 * i, 500 * i + 0.1);
        }
    }

    [AvrSimulatorFact]
    [RunsLong]
    public async Task The_button_on_D2_toggles_D13_on_each_press_with_the_pins_pull_up_on()
    {
        var hex = await BuildAsync("uno-button");

        // pressed (D2 to GND) at 200, 500 and 800 ms, let go 100 ms or 50 ms later
        var run = await AvrSimulator.RunAsync(hex, 1000,
            "--input", "D2=low@200ms", "--input", "D2=high@300ms", "--input", "D2=low@500ms", "--input", "D2=high@600ms",
            "--input", "D2=low@800ms", "--input", "D2=high@850ms");

        var d2 = Assert.Single(run.Pins, p => p.Pin == "D2"); // the program's own change: input, pull-up on
        Assert.Equal("input pull-up", d2.State);
        var led = Led(run);
        Assert.Equal(["low", "high", "low", "high"], led.Select(p => p.State));
        double[] presses = [200, 500, 800];
        for (var i = 0; i < presses.Length; i++)
        {
            Assert.InRange(led[i + 1].Milliseconds, presses[i], presses[i] + 0.05); // INT0, at once
        }
    }

    /// <summary>
    /// The serial example: its first line and a count each second on USART0 at 115200 baud (U2X: 117647 really), and
    /// what is typed (sent to D0) comes straight back, between two counts.
    /// </summary>
    [AvrSimulatorFact]
    [RunsLong]
    public async Task The_serial_example_counts_every_second_and_sends_back_what_is_typed()
    {
        var hex = await BuildAsync("uno-serial");

        var run = await AvrSimulator.RunAsync(hex, 2500, "--send", @"hi Uno\r\n@1300ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("ChipSharp on an Arduino Uno: a count every second; type, and it comes back\r\n" +
            "count 0\r\ncount 1\r\nhi Uno\r\ncount 2\r\n", System.Text.Encoding.ASCII.GetString(run.Usart0));
        Assert.InRange(run.Usart0BaudRates[^1], 115_200 * 0.97, 115_200 * 1.03);
        var counts = run.Usart0Lines.Where(line => line.StartsWith("count", StringComparison.Ordinal)).ToList();
        Assert.Equal(["count 0", "count 1", "count 2"], counts);
    }

    [Fact]
    public void There_are_examples_for_the_Uno_each_with_a_Wokwi_circuit()
    {
        Assert.Equal(
            ["uno-blink", "uno-button", "uno-lcd-clock", "uno-serial", "uno-servo", "uno-timer-blink", "uno-traffic-light"],
            Examples());
        Assert.All(Examples(), example => Assert.True(File.Exists(Path.Combine(Repository.Examples, example, "diagram.json"))));
    }

    [Fact]
    public void Each_circuit_wires_the_pins_its_program_uses_directly_and_no_wire_crosses_a_part()
    {
        foreach (var example in Examples())
        {
            var diagram = Diagram.Read(Path.Combine(Repository.Examples, example, "diagram.json"));

            // the Uno at the top left; parts whose pins this test knows, not turned; no resistors (Wokwi needs none)
            var uno = Assert.Single(diagram.Parts, part => part.Type == "wokwi-arduino-uno");
            Assert.Equal(("uno", 0.0, 0.0), (uno.Id, uno.Left, uno.Top));
            Assert.All(diagram.Parts, part => Assert.True(Geometry.ContainsKey(part.Type), $"{example}: {part.Type}"));
            Assert.All(diagram.Parts, part => Assert.Equal(0, part.Rotate));

            // the Uno's pins the program uses are wired, and only those (its LED "L", D13, is on the board)
            var used = PinsUsed(example);
            var wired = diagram.Wires.SelectMany(wire => new[] { wire.From, wire.To })
                .Where(end => end.StartsWith("uno:", StringComparison.Ordinal) && UnoSignal().IsMatch(end[4..]))
                .Select(end => end[4..]).ToHashSet();
            Assert.Subset(used, wired);
            Assert.Subset(wired.Union(["13"]).ToHashSet(), used.Union(["13"]).ToHashSet());

            foreach (var wire in diagram.Wires)
            {
                CheckWire(example, diagram, wire);
            }
        }

        var blink = Diagram.Read(Path.Combine(Repository.Examples, "uno-blink", "diagram.json"));
        Assert.True(blink.Joined("led:A", "uno:13"));
        Assert.True(blink.Joined("led:C", "uno:GND.1"));
        Assert.Equal(File.ReadAllText(Path.Combine(Repository.Examples, "uno-blink", "diagram.json")),
            File.ReadAllText(Path.Combine(Repository.Examples, "uno-timer-blink", "diagram.json")));

        // the button: one side (contact 2) on D2, the other (contact 1) on GND; pressing joins them; no bounce
        var button = Diagram.Read(Path.Combine(Repository.Examples, "uno-button", "diagram.json"));
        Assert.True(button.Joined("btn:2.r", "uno:2"));
        Assert.True(button.Joined("btn:1.l", "uno:GND.1"));
        Assert.Equal("0", button.Parts.Single(part => part.Id == "btn").Attributes["bounce"]);
    }

    /// <summary>
    /// A wire goes from its first pin by its instructions (h and v: across and down, in pixels), straight to where the
    /// instructions after "*" (taken back from its second pin) start; that straight piece must be across or down, so
    /// that Wokwi draws it as given. Each end leaves its pin the way the pin points (the Uno's top header up, an LED's
    /// legs down, a button's left pins left); no piece of it may cross a part, but for the first piece in the part it
    /// starts from and the last in the part it ends at.
    /// </summary>
    private static void CheckWire(string example, Diagram diagram, Wire wire)
    {
        var (from, to) = (diagram.Pin(wire.From), diagram.Pin(wire.To));
        var star = Array.IndexOf(wire.Path, "*");
        var sourceMoves = star < 0 ? wire.Path : wire.Path[..star];
        var targetMoves = star < 0 ? [] : wire.Path[(star + 1)..];
        var points = new List<(double X, double Y)> { from.At };
        foreach (var move in sourceMoves)
        {
            points.Add(Moved(points[^1], move, +1));
        }

        var back = new List<(double X, double Y)> { to.At };
        foreach (var move in Enumerable.Reverse(targetMoves))
        {
            back.Add(Moved(back[^1], move, -1));
        }

        var (a, b) = (points[^1], back[^1]);
        Assert.True(Math.Abs(a.X - b.X) <= 1 || Math.Abs(a.Y - b.Y) <= 1,
            $"{example}: {wire.From} to {wire.To}: Wokwi would choose how to draw ({a.X}, {a.Y}) to ({b.X}, {b.Y})");
        points.AddRange(Enumerable.Reverse(back));
        points = [.. points.Where((point, i) => i == 0 || point != points[i - 1])];

        Assert.True(Leaves(from, points[0], points[1]), $"{example}: {wire.From} must leave {from.Exit}");
        Assert.True(Leaves(to, points[^1], points[^2]), $"{example}: {wire.To} must be reached from {to.Exit}");
        for (var i = 1; i < points.Count; i++)
        {
            foreach (var part in diagram.Parts)
            {
                var own = (i == 1 && part.Id == from.Part) || (i == points.Count - 1 && part.Id == to.Part);
                Assert.False(!own && Crosses(points[i - 1], points[i], diagram.Box(part)),
                    $"{example}: the wire {wire.From} to {wire.To} crosses {part.Id}");
            }
        }
    }

    private static (double X, double Y) Moved((double X, double Y) at, string move, int sign)
    {
        var distance = sign * double.Parse(move[1..], CultureInfo.InvariantCulture);
        return move[0] switch
        {
            'h' => (at.X + distance, at.Y),
            'v' => (at.X, at.Y + distance),
            _ => throw new InvalidOperationException($"Unknown wire instruction '{move}'."),
        };
    }

    private static bool Leaves(PinAt pin, (double X, double Y) at, (double X, double Y) next) => pin.Exit switch
    {
        "up" => next.Y < at.Y && Math.Abs(next.X - at.X) <= 1,
        "down" => next.Y > at.Y && Math.Abs(next.X - at.X) <= 1,
        "left" => next.X < at.X && Math.Abs(next.Y - at.Y) <= 1,
        _ => next.X > at.X && Math.Abs(next.Y - at.Y) <= 1,
    };

    /// <summary>True when the piece from a to b (across or down) runs inside the box, its edges left out.</summary>
    private static bool Crosses((double X, double Y) a, (double X, double Y) b, (double Left, double Top, double Right, double Bottom) box) =>
        Math.Min(a.X, b.X) < box.Right - 1 && Math.Max(a.X, b.X) > box.Left + 1
        && Math.Min(a.Y, b.Y) < box.Bottom - 1 && Math.Max(a.Y, b.Y) > box.Top + 1;

    /// <summary>
    /// The Uno's pins each example's program uses, by their header names (13, A0, ...), but D0 and D1, the serial port's,
    /// which Wokwi joins to its serial monitor itself.
    /// </summary>
    private static readonly Dictionary<string, string[]> UsedPins = new()
    {
        ["uno-blink"] = ["13"],
        ["uno-button"] = ["2", "13"],
        ["uno-lcd-clock"] = ["A4", "A5"],
        ["uno-serial"] = [],
        ["uno-servo"] = ["9", "A0"],
        ["uno-timer-blink"] = ["13"],
        ["uno-traffic-light"] = ["2", "3", "4", "7", "8", "9", "10", "11", "13", "A0", "A1", "A2", "A4", "A5"],
    };

    /// <summary>
    /// The pins <see cref="UsedPins"/> says the example uses, after checking that its program has each pin's number
    /// (A0 to A5 are 14 to 19), and that each constant it gives Pin's methods is one of them.
    /// </summary>
    private static HashSet<string> PinsUsed(string example)
    {
        var program = File.ReadAllText(Path.Combine(Repository.Examples, example, "Program.cs"));
        var used = UsedPins[example].ToHashSet();
        foreach (var pin in used)
        {
            var number = pin.StartsWith('A') ? 14 + (pin[1] - '0') : int.Parse(pin, CultureInfo.InvariantCulture);
            Assert.Matches($@"\b{number}\b", program);
        }

        Assert.Subset(used, PinCall().Matches(program).Select(match => match.Groups[1].Value).ToHashSet());
        return used;
    }

    private static string[] Examples() =>
    [
        .. Directory.GetDirectories(Repository.Examples, "uno-*").Select(folder => Path.GetFileName(folder)).Order(),
    ];

    /// <summary>
    /// Builds examples/<paramref name="example"/> with dotnet build and chipsharp build -t uno; returns the .hex, after
    /// checking what build printed: the file, and a few KB of the flash the bootloader leaves.
    /// </summary>
    private async Task<string> BuildAsync(string example)
    {
        var project = ExampleProjects.Project(example);
        var dll = await ExampleProjects.DotnetBuildAsync(project, _scratch);

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "uno", "-o", Path.Combine(_scratch, "uno"));

        Assert.True(exitCode == 0, error);
        var lines = printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(Path.GetFileNameWithoutExtension(project) + ".hex", Path.GetFileName(lines[0]));
        Assert.Matches(@"^flash [1-9]\d?\.\d KB of 31\.5 KB \(\d+\.\d%\), RAM \d{2,3} bytes of 2 KB \(stack 512 bytes reserved\)$",
            lines[1]);
        return lines[0];
    }

    /// <summary>D13's changes in a run that went on for the whole time, as each example does (it loops forever).</summary>
    private static List<AvrPinChange> Led(AvrSimulatorRun run)
    {
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.EndsWith(" ms of simulated time have passed", run.Stopped);
        return [.. run.Pins.Where(p => p.Pin == "D13")];
    }

    /// <summary>
    /// Where the pins of the parts the examples use are, from the part's top left corner, and how big each part is, in
    /// Wokwi's pixels (wokwi-elements: arduino-uno-element.ts, led-element.ts, pushbutton-element.ts,
    /// 7segment-element.ts of one digit with its pins drawn from the top, lcd1602-element.ts with "pins": "i2c",
    /// ds1307-element.ts, servo-element.ts, potentiometer-element.ts; 3.78 pixels a mm); and the way each pin points: a
    /// wire leaves it that way.
    /// </summary>
    private static readonly Dictionary<string, (double Width, double Height, Dictionary<string, (double X, double Y, string Exit)> Pins)>
        Geometry = new()
        {
            ["wokwi-arduino-uno"] = (274.3, 201.6, UnoPins()),
            ["wokwi-led"] = (40, 50, new() { ["A"] = (25, 42, "down"), ["C"] = (15, 42, "down") }),
            ["wokwi-pushbutton"] = (67.3, 45.4, new()
            {
                ["1.l"] = (0, 13, "left"), ["2.l"] = (0, 32, "left"), ["1.r"] = (67, 13, "right"), ["2.r"] = (67, 32, "right"),
            }),
            ["wokwi-7segment"] = (47.43, 83.15, SevenSegmentPins()),
            ["wokwi-lcd1602"] = (302.36, 136.06, new()
            {
                ["GND"] = (4, 32, "left"), ["VCC"] = (4, 41.5, "left"), ["SDA"] = (4, 51, "left"), ["SCL"] = (4, 60.5, "left"),
            }),
            ["wokwi-ds1307"] = (97.51, 83.95, new()
            {
                ["GND"] = (9.5, 15, "left"), ["5V"] = (9.5, 25, "left"), ["SDA"] = (9.5, 34.5, "left"),
                ["SCL"] = (9.5, 44, "left"), ["SQW"] = (9.5, 54, "left"),
            }),
            ["wokwi-servo"] = (170.08, 119.55, new()
            {
                ["GND"] = (0, 50, "left"), ["V+"] = (0, 59.5, "left"), ["PWM"] = (0, 69, "left"),
            }),
            ["wokwi-potentiometer"] = (75.59, 75.59, new()
            {
                ["GND"] = (29, 68.5, "down"), ["SIG"] = (39, 68.5, "down"), ["VCC"] = (49, 68.5, "down"),
            }),
        };

    /// <summary>
    /// A one-digit 7-segment display's pins, as a real one has them: G, F, COM, A and B along the top, pointing up; E,
    /// D, COM, C and DP along the bottom, pointing down; 2.54 mm apart.
    /// </summary>
    private static Dictionary<string, (double X, double Y, string Exit)> SevenSegmentPins()
    {
        static double X(int column) => Math.Round((-0.075 + 1.27 + column * 2.54) * 3.78, 2);
        string[] top = ["G", "F", "COM.2", "A", "B"];
        string[] bottom = ["E", "D", "COM.1", "C", "DP"];
        var pins = new Dictionary<string, (double X, double Y, string Exit)>();
        for (var column = 0; column < 5; column++)
        {
            pins[top[column]] = (X(column), 3.78, "up");
            pins[bottom[column]] = (X(column), 71.82, "down");
        }

        return pins;
    }

    private static Dictionary<string, (double X, double Y, string Exit)> UnoPins()
    {
        var pins = new Dictionary<string, (double X, double Y, string Exit)>();
        (string Name, double X)[] top =
        [
            ("AREF", 106), ("GND.1", 115.5), ("13", 125), ("12", 134.5), ("11", 144), ("10", 153.5), ("9", 163), ("8", 173),
            ("7", 189), ("6", 198.5), ("5", 208), ("4", 217.5), ("3", 227), ("2", 236.5), ("1", 246), ("0", 255.5),
        ];
        (string Name, double X)[] bottom =
        [
            ("IOREF", 131), ("RESET", 140.5), ("3.3V", 150), ("5V", 160), ("GND.2", 169.5), ("GND.3", 179), ("VIN", 188.5),
            ("A0", 208), ("A1", 217.5), ("A2", 227), ("A3", 236.5), ("A4", 246), ("A5", 255.5),
        ];
        foreach (var (name, x) in top)
        {
            pins[name] = (x, 9, "up");
        }

        foreach (var (name, x) in bottom)
        {
            pins[name] = (x, 191.5, "down");
        }

        return pins;
    }

    private sealed record Part(string Type, string Id, double Left, double Top, int Rotate, Dictionary<string, string> Attributes);

    private sealed record Wire(string From, string To, string[] Path);

    private sealed record PinAt(string Part, (double X, double Y) At, string Exit);

    private sealed class Diagram(List<Part> parts, List<Wire> wires)
    {
        public List<Part> Parts { get; } = parts;

        public List<Wire> Wires { get; } = wires;

        public static Diagram Read(string path)
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            static double Number(JsonNode? node) => node?.GetValue<double>() ?? 0;
            return new Diagram(
                [
                    .. json["parts"]!.AsArray().Select(part => new Part(part!["type"]!.GetValue<string>(),
                        part["id"]!.GetValue<string>(), Number(part["left"]), Number(part["top"]), (int)Number(part["rotate"]),
                        part["attrs"]!.AsObject().ToDictionary(attribute => attribute.Key, attribute => attribute.Value!.ToString()))),
                ],
                [
                    .. json["connections"]!.AsArray().Select(wire => new Wire(wire![0]!.GetValue<string>(),
                        wire[1]!.GetValue<string>(), [.. wire[3]!.AsArray().Select(move => move!.GetValue<string>())])),
                ]);
        }

        /// <summary>True when a wire joins the two pins, either way round.</summary>
        public bool Joined(string a, string b) => Wires.Any(wire => (wire.From, wire.To) == (a, b) || (wire.From, wire.To) == (b, a));

        /// <summary>
        /// True when the two pins are on one net: joined by a wire, through other pins (a chain of wires), or as the Uno's
        /// three GND pins are, inside the board.
        /// </summary>
        public bool Connected(string a, string b)
        {
            var parent = new Dictionary<string, string>();
            string Find(string pin)
            {
                while (parent.TryGetValue(pin, out var up) && up != pin)
                {
                    pin = up;
                }

                return pin;
            }

            (string, string)[] board = [("uno:GND.1", "uno:GND.2"), ("uno:GND.2", "uno:GND.3")];
            foreach (var (from, to) in Wires.Select(wire => (wire.From, wire.To)).Concat(board))
            {
                parent[Find(from)] = Find(to);
            }

            return Find(a) == Find(b);
        }

        /// <summary>True when the pin is on the net of the Uno's GND pins.</summary>
        public bool Grounded(string pin) => Connected(pin, "uno:GND.1");

        public PinAt Pin(string end)
        {
            var (id, name) = (end[..end.IndexOf(':')], end[(end.IndexOf(':') + 1)..]);
            var part = Parts.Single(part => part.Id == id);
            Assert.True(Geometry[part.Type].Pins.TryGetValue(name, out var pin), $"{part.Type} has no pin '{name}'");
            return new PinAt(id, (part.Left + pin.X, part.Top + pin.Y), pin.Exit);
        }

        public (double Left, double Top, double Right, double Bottom) Box(Part part)
        {
            var (width, height, _) = Geometry[part.Type];
            return (part.Left, part.Top, part.Left + width, part.Top + height);
        }
    }

    [GeneratedRegex(@"\bPin\.(?:Output|Input|Write|Toggle|Read|Watch|WatchRising|WatchFalling|Unwatch)\((\d+)")]
    private static partial Regex PinCall();

    [GeneratedRegex(@"^(\d+|A[0-5])$")]
    private static partial Regex UnoSignal();
}
