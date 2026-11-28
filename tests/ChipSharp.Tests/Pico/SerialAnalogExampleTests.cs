using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The examples of serial input, analog input and PWM (examples/pico-uart-echo, pico-adc-pwm and pico-servo), built
/// the way the README says (dotnet build of the project, then <c>chipsharp build -t pico</c>) and run in the rp2040js
/// simulator with what their Wokwi circuit would give them: lines typed, a knob turned.
/// </summary>
public sealed partial class SerialAnalogExampleTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [SimulatorFact]
    [RunsLong]
    public async Task The_command_prompt_switches_the_LED_and_answers_each_line()
    {
        var uf2 = await BuildAsync("pico-uart-echo", heap: true);

        var run = await Rp2040Simulator.RunAsync(uf2, 400,
            "--send", @"0=led on\r@50ms", "--send", @"0=status\r\n@100ms", "--send", @"0=hello, Pico\n@150ms",
            "--send", @"0=led off\r@200ms", "--send", @"0=status\n@250ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(
            [
                "ChipSharp on a Pico. Commands: led on, led off, status", "> LED on", "> LED on, 2 commands",
                "> you said: hello, Pico", "> LED off", "> LED off, 5 commands",
            ],
            Uart0Lines(run).Take(6));
        var led = run.Pins.Where(p => p.Pin == 25 && p.State is "low" or "high").ToList();
        Assert.Equal(["low", "high", "low"], led.Select(p => p.State));
        Assert.InRange(led[1].Milliseconds, 50, 51); // "led on\r" takes 7 bytes' time, 0.6 ms
        Assert.InRange(led[2].Milliseconds, 200, 201);
    }

    [SimulatorFact]
    [RunsLong]
    public async Task The_knob_sets_the_LEDs_brightness_and_its_voltage_goes_to_the_console()
    {
        var uf2 = await BuildAsync("pico-adc-pwm", heap: false);

        var run = await Rp2040Simulator.RunAsync(uf2, 400,
            "--adc", "26=0V", "--adc", "26=1.65V@100ms", "--adc", "26=1.7V@200ms", "--adc", "26=3.3V@300ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        // a change of less than 0.1 V (1.65 V to 1.7 V) is not printed
        Assert.Equal(["knob 0.000 V, LED 0%", "knob 1.650 V, LED 50%", "knob 3.299 V, LED 100%"], Uart0Lines(run));
        var pwm = PwmLines(run, 15);
        Assert.Equal("steady low", pwm[0].Text);
        Assert.Matches(@"^1000\.00 Hz, duty 50\.01% \(500\.1\d us high\)$", pwm.First(line => line.Milliseconds > 100).Text);
        Assert.Matches(@"^1000\.00 Hz, duty 51\.5\d% ", pwm.First(line => line.Milliseconds > 200).Text);
        Assert.Equal("steady high", pwm[^1].Text);
    }

    [SimulatorFact]
    [RunsLong]
    public async Task The_servo_sweeps_with_pulses_of_1_to_2_ms_at_50_Hz()
    {
        var uf2 = await BuildAsync("pico-servo", heap: false);

        var run = await Rp2040Simulator.RunAsync(uf2, 3000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(
            [
                "servo at 0 degrees", "servo at 45 degrees", "servo at 90 degrees", "servo at 135 degrees",
                "servo at 180 degrees", "servo at 135 degrees", "servo at 90 degrees", "servo at 45 degrees",
            ],
            Uart0Lines(run).Take(8));
        var pulses = PwmLines(run, 22).Select(line => PwmPulse().Match(line.Text)).Where(match => match.Success)
            .Select(match => (Hertz: Number(match, 1), Microseconds: Number(match, 2))).ToList();
        Assert.All(pulses, pulse => Assert.InRange(pulse.Hertz, 49.99, 50.01));
        Assert.InRange(pulses.Min(pulse => pulse.Microseconds), 999, 1001);
        Assert.InRange(pulses.Max(pulse => pulse.Microseconds), 1999, 2001);
    }

    [Fact]
    public void Each_has_a_Wokwi_circuit_with_its_parts_on_the_pins_the_program_uses()
    {
        static JsonArray Connections(string example) =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Examples, example, "diagram.json")))!["connections"]!
                .AsArray();
        static bool Wired(JsonArray connections, string from, string to) => connections.Any(wire =>
            (wire![0]!.GetValue<string>() == from && wire[1]!.GetValue<string>() == to)
            || (wire[0]!.GetValue<string>() == to && wire[1]!.GetValue<string>() == from));

        foreach (var example in new[] { "pico-uart-echo", "pico-adc-pwm", "pico-servo" })
        {
            // direct wires, as Wokwi needs: no resistors, the Pico at the top left
            var parts = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Examples, example, "diagram.json")))![
                "parts"]!.AsArray();
            Assert.DoesNotContain(parts, part => part!["type"]!.GetValue<string>() == "wokwi-resistor");
            var pico = parts.Single(part => part!["type"]!.GetValue<string>() == "wokwi-pi-pico")!;
            Assert.Equal((0, 0), (pico["top"]!.GetValue<int>(), pico["left"]!.GetValue<int>()));
            var connections = Connections(example);
            Assert.True(Wired(connections, "pico:GP0", "$serialMonitor:RX"), example);
            Assert.True(Wired(connections, "pico:GP1", "$serialMonitor:TX"), example);
        }

        Assert.True(Wired(Connections("pico-adc-pwm"), "knob:SIG", "pico:GP26"));
        Assert.True(Wired(Connections("pico-adc-pwm"), "pico:GP15", "lamp:A"));
        Assert.True(Wired(Connections("pico-servo"), "servo:PWM", "pico:GP22"));
    }

    /// <summary>
    /// Builds examples/<paramref name="example"/> with dotnet build and chipsharp build -t pico, which says whether the
    /// program uses the heap; returns the .uf2.
    /// </summary>
    private async Task<string> BuildAsync(string example, bool heap)
    {
        var dll = await ExampleProjects.DotnetBuildAsync(ExampleProjects.Project(example), _scratch);
        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "pico", "-o",
            Path.Combine(_scratch, "pico"));

        Assert.True(exitCode == 0, error);
        var lines = printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(heap, lines[1].Contains("+ heap", StringComparison.Ordinal));
        return lines[0];
    }

    private static List<string> Uart0Lines(SimulatorRun run) =>
    [
        .. run.Output.Split('\n').Select(line => line.TrimEnd('\r'))
            .Select(line => UartLine().Match(line)).Where(match => match.Success).Select(match => match.Groups[1].Value),
    ];

    private static List<(double Milliseconds, string Text)> PwmLines(SimulatorRun run, int pin) =>
    [
        .. run.Output.Split('\n').Select(line => PwmLine().Match(line.TrimEnd('\r')))
            .Where(match => match.Success && match.Groups[2].Value == pin.ToString(CultureInfo.InvariantCulture))
            .Select(match => (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[3].Value)),
    ];

    private static double Number(Match match, int group) =>
        double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\[\s*[0-9.]+ ms\] UART0: (.*)$")]
    private static partial Regex UartLine();

    [GeneratedRegex(@"^\[\s*([0-9.]+) ms\] GPIO(\d+) PWM (.+)$")]
    private static partial Regex PwmLine();

    [GeneratedRegex(@"^([0-9.]+) Hz, duty [0-9.]+% \(([0-9.]+) us high\)$")]
    private static partial Regex PwmPulse();
}
