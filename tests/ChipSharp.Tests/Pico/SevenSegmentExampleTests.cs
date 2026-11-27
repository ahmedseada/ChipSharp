using System.Globalization;
using System.Text;
using System.Text.Json;
using ChipSharp.Components;
using ChipSharp.Image.Uf2;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// examples/pico-seven-segment, a 0 to 9 counter on a 7-segment display on GPIO 2 to 8 with ChipSharp.Components'
/// SevenSegment, built the way its Program.cs says (dotnet build of the project, then <c>chipsharp build -t pico</c>)
/// and run in the simulator: the digits on the pins and over UART0, with no heap; and its Wokwi diagram, which wires
/// each segment to its pin directly, with the wires around the display rather than over it.
/// </summary>
public sealed class SevenSegmentExampleTests : IDisposable
{
    private const string Example = "pico-seven-segment";

    /// <summary>Pixels in a millimetre, as Wokwi draws its parts (96 per inch).</summary>
    private const double Pixels = 96 / 25.4;

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
    public async Task The_example_counts_from_0_to_9_once_a_second_on_GPIO_2_to_8_and_over_UART0_with_no_heap()
    {
        var project = ExampleProjects.Project(Example);
        var dll = await ExampleProjects.DotnetBuildAsync(project, _scratch);
        var output = Path.Combine(_scratch, "pico");
        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "pico", "-o", output, "--keep");
        Assert.True(exitCode == 0, error);
        var lines = printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("PicoSevenSegment.uf2", Path.GetFileName(lines[0]));
        Assert.Matches(@"^flash \d+(\.\d)? KB of 2048 KB \(\d+\.\d%\), RAM \d+ bytes of 264 KB \(stack 8 KB reserved\)$",
            lines[^1]);

        var run = await Rp2040Simulator.RunAsync(lines[0], 10_500);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.EndsWith("stopped: 10500 ms of simulated time have passed", run.Stopped);
        Assert.Equal(string.Concat(Enumerable.Range(0, 11).Select(n => $"digit {n % 10}\r\n")), Encoding.UTF8.GetString(run.Uart0));

        // in the middle of each second, GPIO 2 to 8 (A to G, high: lit) show that second's digit
        var pins = run.Pins;
        Assert.Equal(Enumerable.Range(0, 11).Select(n => SevenSegment.Pattern(n % 10)),
            Enumerable.Range(0, 11).Select(second => (byte)Enumerable.Range(0, 7)
                .Where(segment => IsHigh(pins, 2 + segment, second * 1000 + 500)).Sum(segment => 1 << segment)));
        Assert.All(pins.Where(p => p.Pin > 1), p => Assert.InRange(p.Pin, 2, 8));

        // its shapes in flash, and no heap
        var elf = ElfFile.Read(Path.Combine(output, "PicoSevenSegment.elf"));
        Assert.DoesNotContain("chipsharp_new_object", elf.Symbols.Keys);
        var image = PicoBuild.FlashImage(elf);
        byte[] shapes = [.. "0123456789ABCDEF".Select(SevenSegment.Pattern)];
        Assert.Contains(Enumerable.Range(0, image.Length - shapes.Length), at => image.AsSpan(at, shapes.Length).SequenceEqual(shapes));
    }

    [Fact]
    public void The_examples_Wokwi_diagram_wires_each_segment_to_its_pin_directly_around_the_display()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Examples, Example, "diagram.json")));
        var parts = json.RootElement.GetProperty("parts").EnumerateArray()
            .ToDictionary(part => part.GetProperty("id").GetString()!, part => part.Clone());
        var wires = json.RootElement.GetProperty("connections").EnumerateArray()
            .Select(wire => new Wire(wire[0].GetString()!, wire[1].GetString()!,
                [.. wire[3].EnumerateArray().Select(step => step.GetString()!)]))
            .ToList();

        // the Pico at 0, 0 and a common-cathode digit; nothing else, no resistors (Wokwi needs none)
        Assert.Equal(["wokwi-7segment", "wokwi-pi-pico"],
            parts.Values.Select(part => part.GetProperty("type").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal((0.0, 0.0), (parts["pico"].GetProperty("top").GetDouble(), parts["pico"].GetProperty("left").GetDouble()));
        var digit = parts.Single(part => part.Value.GetProperty("type").GetString() == "wokwi-7segment");
        Assert.Equal("cathode", digit.Value.GetProperty("attrs").GetProperty("common").GetString());
        var id = digit.Key;

        // a wire of its own from GP2 to A, ..., GP8 to G; one from a GND of the Pico to a COM; UART0 on the serial monitor
        Assert.Equal(
            [
                ("pico:GND.1", $"{id}:COM.2"), ("pico:GP0", "$serialMonitor:RX"), ("pico:GP1", "$serialMonitor:TX"),
                .. Enumerable.Range(0, 7).Select(segment => ($"pico:GP{2 + segment}", $"{id}:{(char)('A' + segment)}")),
            ],
            wires.Select(wire => (wire.From, wire.To)).Order());

        // the display left of the Pico; each wire leaves the Pico to the left, keeps off both parts, and comes into its
        // pin straight from above (the top row) or below (the bottom row)
        var display = (Left: digit.Value.GetProperty("left").GetDouble(), Top: digit.Value.GetProperty("top").GetDouble());
        var (right, bottom) = (display.Left + 12.55 * Pixels, display.Top + 22 * Pixels);
        Assert.True(right < 0, $"the display reaches x = {right}, over the Pico");
        var paths = new List<(string Wire, List<(double X, double Y)> Points)>();
        foreach (var wire in wires.Where(wire => wire.To.StartsWith(id + ":", StringComparison.Ordinal)))
        {
            var (end, top) = DisplayPin(wire.To[(id.Length + 1)..], display);
            var points = Corners(PicoPin(wire.From), wire.Steps, end);
            paths.Add((wire.To, points));

            Assert.True(wire.Steps[0].StartsWith("h-", StringComparison.Ordinal), $"{wire.From} does not leave the Pico to the left");
            Assert.All(points.Skip(1), point => Assert.True(point.X < 0, $"{wire.To}'s wire goes over the Pico at {point}"));
            for (var i = 1; i < points.Count - 1; i++)
            {
                Assert.False(Meet((points[i - 1], points[i]), ((display.Left, display.Top), (right, bottom))),
                    $"{wire.To}'s wire goes over the display from {points[i - 1]} to {points[i]}");
            }

            var last = points[^2];
            Assert.Equal(end.X, last.X, 0.01);
            Assert.True(top ? last.Y < display.Top : last.Y > bottom, $"{wire.To}'s wire does not come into it from outside");
        }

        // and the wires meet once: F and G cross, which the display has the other way round from GP7 and GP8
        var meetings = paths.SelectMany((a, i) => paths.Skip(i + 1).Select(b => (a, b, Count: Meetings(a.Points, b.Points))))
            .Where(pair => pair.Count > 0).Select(pair => $"{pair.a.Wire} {pair.b.Wire}: {pair.Count}");
        Assert.Equal([$"{id}:F {id}:G: 1"], meetings);
    }

    /// <summary>One wire of a diagram: its two pins and the steps Wokwi draws it by ("h-20", "v15", "*").</summary>
    private sealed record Wire(string From, string To, string[] Steps);

    /// <summary>True when the last high or low of <paramref name="pin"/> at or before <paramref name="at"/> ms was high.</summary>
    private static bool IsHigh(IReadOnlyList<PinChange> pins, int pin, double at) =>
        pins.LastOrDefault(p => p.Pin == pin && p.Milliseconds <= at && p.State is "high" or "low")?.State == "high";

    /// <summary>
    /// Where a pin on the Pico's left edge is, in pixels from its top left corner at 0, 0: wokwi-pi-pico's pins are
    /// 1.6 mm in, the first (GP0) 3.4 mm down and the others 2.54 mm apart.
    /// </summary>
    private static (double X, double Y) PicoPin(string pin)
    {
        string[] left = ["GP0", "GP1", "GND.1", "GP2", "GP3", "GP4", "GP5", "GND.2", "GP6", "GP7", "GP8"];
        var at = Array.IndexOf(left, pin["pico:".Length..]);
        Assert.True(at >= 0, $"{pin} is not on the Pico's left edge");
        return (1.6 * Pixels, (3.4 + 2.54 * at) * Pixels);
    }

    /// <summary>
    /// Where a pin of a one-digit wokwi-7segment is (its pins on top and bottom, 2.54 mm apart from 1.195 mm in), and
    /// whether it is in the top row (G F COM.2 A B, 1 mm down) or the bottom one (E D COM.1 C DP, 19 mm down).
    /// </summary>
    private static ((double X, double Y) At, bool Top) DisplayPin(string pin, (double Left, double Top) display)
    {
        string[] top = ["G", "F", "COM.2", "A", "B"];
        string[] bottom = ["E", "D", "COM.1", "C", "DP"];
        var column = Array.IndexOf(top, pin) is var t and >= 0 ? t : Array.IndexOf(bottom, pin);
        Assert.True(column >= 0, $"the display has no pin {pin}");
        var isTop = top.Contains(pin);
        return ((display.Left + (1.195 + 2.54 * column) * Pixels, display.Top + (isTop ? 1 : 19) * Pixels), isTop);
    }

    /// <summary>
    /// The corners of a wire Wokwi draws from <paramref name="start"/> by <paramref name="steps"/>: "h" and "v" move it
    /// across and down (in pixels), and "*", the last, goes straight on to <paramref name="end"/>.
    /// </summary>
    private static List<(double X, double Y)> Corners((double X, double Y) start, string[] steps, (double X, double Y) end)
    {
        Assert.Equal("*", steps[^1]);
        var points = new List<(double X, double Y)> { start };
        foreach (var step in steps)
        {
            var (x, y) = points[^1];
            if (step == "*")
            {
                points.Add(end);
                break;
            }

            var by = double.Parse(step[1..], CultureInfo.InvariantCulture);
            points.Add(step[0] == 'h' ? (x + by, y) : (x, y + by));
        }

        return points;
    }

    /// <summary>
    /// True when two straight lines across or down (or rectangles, from their top left to their bottom right corner)
    /// meet, or come within 2 pixels of each other, where a wire would look joined to what it passes.
    /// </summary>
    private static bool Meet(((double X, double Y) From, (double X, double Y) To) a,
        ((double X, double Y) From, (double X, double Y) To) b)
    {
        const double Near = 2;
        return Math.Max(a.From.X, a.To.X) + Near >= Math.Min(b.From.X, b.To.X) &&
            Math.Min(a.From.X, a.To.X) - Near <= Math.Max(b.From.X, b.To.X) &&
            Math.Max(a.From.Y, a.To.Y) + Near >= Math.Min(b.From.Y, b.To.Y) &&
            Math.Min(a.From.Y, a.To.Y) - Near <= Math.Max(b.From.Y, b.To.Y);
    }

    /// <summary>How many times two wires, each straight lines across and down, meet: where they cross, touch or run together.</summary>
    private static int Meetings(List<(double X, double Y)> a, List<(double X, double Y)> b) =>
        Enumerable.Range(1, a.Count - 1).Sum(i => Enumerable.Range(1, b.Count - 1).Count(j => Meet((a[i - 1], a[i]), (b[j - 1], b[j]))));
}
