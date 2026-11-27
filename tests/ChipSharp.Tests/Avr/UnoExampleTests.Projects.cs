using System.Text;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The Uno's examples of whole circuits, with no heap: the traffic light (examples/uno-traffic-light: three lights, a
/// 7-segment display, a button and an LCD; the Pico traffic light with an LCD brought to the Uno), the clock
/// (examples/uno-lcd-clock: an LCD and a DS1307 on I2C) and the servo (examples/uno-servo: a potentiometer on A0 sets a
/// servo on D9). Each is built as the README says and run in the simulator with its parts simulated; each circuit wires
/// the parts as the program drives them.
/// </summary>
public sealed partial class UnoExampleTests
{
    /// <summary>The segments each digit lights, bit 0 = A (SevenSegment's shapes).</summary>
    private static readonly byte[] Digits = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];

    /// <summary>The traffic light's turn display: the Uno pins of its segments A to G.</summary>
    private static readonly string[] TurnSegments = ["D3", "D4", "D7", "D8", "D9", "D11", "D10"];

    /// <summary>
    /// The traffic light, its button pressed at 2.5 s (D2 to GND), during lane 1's green, with an LCD at 0x27, for 16 s:
    /// over the serial port it prints exactly what the Pico's traffic lights print (the Pico's test's own text), and the
    /// LCD shows what the Pico traffic light with an LCD shows.
    /// </summary>
    [AvrSimulatorFact]
    [RunsLong]
    public async Task The_traffic_light_prints_what_the_Picos_prints_and_shows_the_same_on_its_LCD()
    {
        var run = await TrafficLightAsync();

        Assert.Equal(TrafficLightExampleTests.Expected, Encoding.ASCII.GetString(run.Usart0));
        Assert.InRange(run.Usart0BaudRates[^1], 115_200 * 0.97, 115_200 * 1.03);

        // the lane, the turn's seconds; the step, its seconds: each second, when it changes (as PicoTrafficLightLcd)
        (int Lane, int Turn, string Step)[] screens =
        [
            (1, 8, "green         4s"), (1, 7, "green         3s"), (1, 6, "green         2s"),
            (1, 4, "yellow        1s"), (1, 3, "red           2s"), (1, 2, "red           1s"), (1, 1, "switching       "),
            (2, 8, "green         4s"), (2, 7, "green         3s"), (2, 6, "green         2s"), (2, 5, "green         1s"),
            (2, 4, "yellow        1s"), (2, 3, "red           2s"), (2, 2, "red           1s"), (2, 1, "switching       "),
            (1, 8, "green         4s"), (1, 7, "green         3s"),
        ];
        Assert.Equal(
            [.. screens.SelectMany(screen => new[] { $"LCD 0x27: |Road {screen.Lane}   turn {screen.Turn}s|", $"LCD 0x27: |{screen.Step}|" })],
            run.After("LCD 0x27: "));
        Assert.Empty(run.After("LCD 0x27 busy"));
    }

    [AvrSimulatorFact]
    [RunsLong]
    public async Task The_traffic_lights_signal_display_heartbeat_and_button_work_as_on_the_Pico()
    {
        var run = await TrafficLightAsync();
        var pins = run.Pins;

        // which light is on (red A0, yellow A1, green A2) and what the turn display shows, at the times the Pico's test
        // looks (the same turn, one signal for the active lane)
        (double At, string Light, int Turn)[] expected =
        [
            (500, "A2", 8), (2400, "A2", 6), (2600, "A1", 4), (4000, "A0", 3), (6000, "A0", 1),
            (7000, "A2", 8), (11000, "A1", 4), (13000, "A0", 2), (15000, "A2", 8),
        ];
        foreach (var (at, light, turn) in expected)
        {
            var on = string.Join(' ', new[] { "A0", "A1", "A2" }.Where(pin => IsHigh(pins, pin, at)));
            Assert.Equal((at, light, turn), (at, on, Shown(pins, at)));
        }

        // the press ends the green within one 10 ms wait
        var greenOff = pins.First(p => p.Pin == "A2" && p.State == "low" && p.Milliseconds > 1000).Milliseconds;
        Assert.InRange(greenOff, 2500, 2511);

        // the button's pin has its pull-up on; the LED "L" changes every 500 ms to the microsecond, from the timer event
        Assert.Equal("input pull-up", pins.Single(p => p.Pin == "D2").State);
        var heartbeat = pins.Where(p => p.Pin == "D13" && p.State is "high" or "low").Skip(1).ToList();
        Assert.InRange(heartbeat.Count, 30, 32);
        for (var i = 1; i < heartbeat.Count; i++)
        {
            Assert.InRange(heartbeat[i].Milliseconds - heartbeat[i - 1].Milliseconds, 499.95, 500.05);
        }
    }

    /// <summary>
    /// The clock: the time from a running DS1307 on the Uno's clock, then the date and time on the LCD each second; and
    /// a halted DS1307 set to the date it starts from.
    /// </summary>
    [AvrSimulatorFact]
    [RunsLong]
    public async Task The_clock_shows_the_DS1307s_time_on_the_LCD_and_sets_a_halted_one()
    {
        var hex = await BuildAsync("uno-lcd-clock");

        var running = await AvrSimulator.RunAsync(hex, 3500, "--i2c", "0:0x27=lcd1602",
            "--i2c", "0:0x68=ds1307@2026-11-27T18:59:58");
        var halted = await AvrSimulator.RunAsync(hex, 1500, "--i2c", "0:0x27=lcd1602", "--i2c", "0:0x68=ds1307@halted");

        Assert.True(running.ExitCode == 0, running.ToString());
        Assert.Equal("The time from the DS1307: 2026-11-27 18:59:58\r\n", Encoding.ASCII.GetString(running.Usart0));
        // (a blank screen first: the LCD is cleared while the DS1307 is read)
        var screens = running.After("LCD 0x27: |").Where(line => line != "LCD 0x27: |                |").ToList();
        Assert.Equal(
        [
            "LCD 0x27: |Fri 27 Nov 2026 |", "LCD 0x27: |18:59:58        |",
            "LCD 0x27: |Fri 27 Nov 2026 |", "LCD 0x27: |18:59:59        |",
            "LCD 0x27: |Fri 27 Nov 2026 |", "LCD 0x27: |19:00:00        |",
            "LCD 0x27: |Fri 27 Nov 2026 |", "LCD 0x27: |19:00:01        |",
        ], screens);

        // each new second on the LCD as it starts: one second after the one before, give or take the writes
        var seconds = running.Times("LCD 0x27: |1");
        Assert.Equal(4, seconds.Count);
        for (var i = 1; i < seconds.Count; i++)
        {
            Assert.InRange(seconds[i] - seconds[i - 1], 990, 1010);
        }

        Assert.True(halted.ExitCode == 0, halted.ToString());
        Assert.Equal("No DS1307 running: the clock starts at 2026-11-27 18:00:00\r\n", Encoding.ASCII.GetString(halted.Usart0));
        Assert.Equal(["DS1307 0x68 set to 2026-11-27 18:00:00 (Friday, day 6), running"], halted.After("DS1307 0x68 set to"));
        Assert.Contains("LCD 0x27: |18:00:01        |", halted.After("LCD 0x27: "));
    }

    /// <summary>
    /// The servo follows the knob: 0 V, 2.5 V and 5 V on A0 are 0, 90 and 180 degrees, pulses of 1, 1.5 and 2 ms on D9,
    /// 50 times a second, each angle once on the console.
    /// </summary>
    [AvrSimulatorFact]
    [RunsLong]
    public async Task The_servo_on_D9_follows_the_knob_on_A0()
    {
        var hex = await BuildAsync("uno-servo");

        var run = await AvrSimulator.RunAsync(hex, 1200, "--adc", "A0=0V", "--adc", "A0=2.5V@400ms", "--adc", "A0=5V@800ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("knob 0, servo at 0 degrees\r\nknob 512, servo at 90 degrees\r\nknob 1023, servo at 180 degrees\r\n",
            Encoding.ASCII.GetString(run.Usart0));
        var pulses = run.Pwm.Where(period => period.Pin == "D9").ToList();
        Assert.All(pulses, period => Assert.InRange(period.Hertz, 49.99, 50.01));
        foreach (var (from, to, high) in new[] { (100.0, 400.0, 1000.0), (450.0, 800.0, 1500.0), (850.0, 1200.0, 2000.0) })
        {
            var during = pulses.Where(period => period.Milliseconds > from && period.Milliseconds < to).ToList();
            Assert.NotEmpty(during);
            Assert.All(during, period => Assert.InRange(period.HighMicroseconds, high - 0.1, high + 0.1));
        }
    }

    [Fact]
    public void The_traffic_lights_circuit_wires_the_lights_the_display_the_button_and_the_LCD_as_the_program_drives_them()
    {
        var diagram = Diagram.Read(Path.Combine(Repository.Examples, "uno-traffic-light", "diagram.json"));
        string? Attribute(string id, string name) =>
            diagram.Parts.Single(part => part.Id == id).Attributes.GetValueOrDefault(name);

        // the turn display: segments A to G on D3, D4, D7, D8, D9, D11 and D10 (the program's SevenSegment), common
        // cathode, its common pin on GND
        Assert.Equal("cathode", Attribute("turn", "common"));
        string[] segments = ["A", "B", "C", "D", "E", "F", "G"];
        for (var i = 0; i < segments.Length; i++)
        {
            Assert.True(diagram.Joined($"turn:{segments[i]}", $"uno:{TurnSegments[i][1..]}"), $"segment {segments[i]}");
        }

        Assert.True(diagram.Grounded("turn:COM.1") || diagram.Grounded("turn:COM.2"));

        // the signal: red on A0, yellow on A1, green on A2, each LED's cathode on GND
        foreach (var (light, pin) in new[] { ("red", "A0"), ("yellow", "A1"), ("green", "A2") })
        {
            Assert.True(diagram.Joined($"{light}:A", $"uno:{pin}"), light);
            Assert.Contains(light, Attribute(light, "color"));
            Assert.True(diagram.Grounded($"{light}:C"), light);
        }

        // the button between D2 and GND, which does not bounce; the LCD on the I2C bus and 5 V
        Assert.True(diagram.Connected("btn:2.l", "uno:2") && diagram.Grounded("btn:1.l"));
        Assert.Equal("0", Attribute("btn", "bounce"));
        AssertLcd(diagram);
    }

    [Fact]
    public void The_clocks_and_the_servos_circuits_wire_their_parts_as_the_programs_use_them()
    {
        var clock = Diagram.Read(Path.Combine(Repository.Examples, "uno-lcd-clock", "diagram.json"));
        AssertLcd(clock);
        Assert.True(clock.Joined("rtc:SDA", "uno:A4") && clock.Joined("rtc:SCL", "uno:A5"));
        Assert.True(clock.Joined("rtc:5V", "uno:5V") && clock.Grounded("rtc:GND"));

        var servo = Diagram.Read(Path.Combine(Repository.Examples, "uno-servo", "diagram.json"));
        Assert.True(servo.Joined("servo:PWM", "uno:9") && servo.Joined("servo:V+", "uno:5V") && servo.Grounded("servo:GND"));
        Assert.True(servo.Joined("knob:SIG", "uno:A0") && servo.Joined("knob:VCC", "uno:5V") && servo.Grounded("knob:GND"));
    }

    /// <summary>A 16x2 LCD in I2C mode: SDA on A4, SCL on A5, VCC on 5V, GND on GND.</summary>
    private static void AssertLcd(Diagram diagram)
    {
        Assert.Equal("i2c", diagram.Parts.Single(part => part.Id == "lcd").Attributes["pins"]);
        Assert.True(diagram.Joined("lcd:SDA", "uno:A4") && diagram.Joined("lcd:SCL", "uno:A5"));
        Assert.True(diagram.Joined("lcd:VCC", "uno:5V") && diagram.Grounded("lcd:GND"));
    }

    /// <summary>Builds the traffic light and runs it for 16 s, the button pressed at 2.5 s for 200 ms, with an LCD.</summary>
    private async Task<AvrSimulatorRun> TrafficLightAsync()
    {
        var hex = await BuildAsync("uno-traffic-light");

        var run = await AvrSimulator.RunAsync(hex, 16_000, "--input", "D2=low@2500ms", "--input", "D2=high@2700ms",
            "--i2c", "0:0x27=lcd1602");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.EndsWith("16000 ms of simulated time have passed", run.Stopped);
        return run;
    }

    /// <summary>True when the last high or low of the Uno pin at or before <paramref name="at"/> ms was high.</summary>
    private static bool IsHigh(IReadOnlyList<AvrPinChange> pins, string pin, double at) =>
        pins.LastOrDefault(p => p.Pin == pin && p.Milliseconds <= at && p.State is "high" or "low")?.State == "high";

    /// <summary>The digit the turn display shows at <paramref name="at"/> ms; -1 when it is blank.</summary>
    private static int Shown(IReadOnlyList<AvrPinChange> pins, double at)
    {
        var pattern = Enumerable.Range(0, 7).Where(segment => IsHigh(pins, TurnSegments[segment], at))
            .Aggregate(0, (bits, segment) => bits | 1 << segment);
        if (pattern == 0)
        {
            return -1;
        }

        var digit = Array.IndexOf(Digits, (byte)pattern);
        Assert.True(digit >= 0, $"the turn display shows 0x{pattern:X2} at {at} ms, which is no digit");
        return digit;
    }
}
