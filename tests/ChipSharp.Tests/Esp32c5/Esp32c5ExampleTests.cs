using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChipSharp.Image.Esp;
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
    /// <summary>
    /// The pins the board file leaves a program that the examples use: what <c>chipsharp check</c> accepts (2, 3, 11 and 12 are
    /// the LCD traffic light's segments: strapping and UART bridge pins, see its README).
    /// </summary>
    private static readonly int[] FreePins = [0, 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 23, 24];

    /// <summary>
    /// The GPIOs Wokwi's board-esp32-c5-devkitc-1 names otherwise than by their number (wokwi-boards,
    /// boards/esp32-c5-devkitc-1/board.json: "TX" is GPIO11 and "RX" GPIO12, UART0's pins; it has no pins "11" and "12",
    /// so a wire to esp:11 connects nothing).
    /// </summary>
    private static readonly Dictionary<string, int> WokwiPinNames = new(StringComparer.Ordinal) { ["TX"] = 11, ["RX"] = 12 };

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
    /// What PicoTrafficLightLcd prints in its simulator (rp2040js, the owner's simulate.ps1: the button pressed at 2.5 s,
    /// 15.5 s run): the serial port's lines and the LCD's screen, each time it changes.
    /// </summary>
    private static readonly string[] PicoConsole =
    [
        "road : 1",
        "    green 4",
        "    green 3",
        "    green 2",
        "lane : 1 interrupted, move to lane 2",
        "    yellow 1",
        "    red 2",
        "    red 1",
        "road : 2",
        "    green 4",
        "    green 3",
        "    green 2",
        "    green 1",
        "    yellow 1",
        "    red 2",
        "    red 1",
        "road : 1",
        "    green 4",
    ];

    private static readonly string[] PicoLcd =
    [
        "LCD 0x27: |Road 1   turn 8s|",
        "LCD 0x27: |green         4s|",
        "LCD 0x27: |Road 1   turn 7s|",
        "LCD 0x27: |green         3s|",
        "LCD 0x27: |Road 1   turn 6s|",
        "LCD 0x27: |green         2s|",
        "LCD 0x27: |Road 1   turn 4s|",
        "LCD 0x27: |yellow        1s|",
        "LCD 0x27: |Road 1   turn 3s|",
        "LCD 0x27: |red           2s|",
        "LCD 0x27: |Road 1   turn 2s|",
        "LCD 0x27: |red           1s|",
        "LCD 0x27: |Road 1   turn 1s|",
        "LCD 0x27: |switching       |",
        "LCD 0x27: |Road 2   turn 8s|",
        "LCD 0x27: |green         4s|",
        "LCD 0x27: |Road 2   turn 7s|",
        "LCD 0x27: |green         3s|",
        "LCD 0x27: |Road 2   turn 6s|",
        "LCD 0x27: |green         2s|",
        "LCD 0x27: |Road 2   turn 5s|",
        "LCD 0x27: |green         1s|",
        "LCD 0x27: |Road 2   turn 4s|",
        "LCD 0x27: |yellow        1s|",
        "LCD 0x27: |Road 2   turn 3s|",
        "LCD 0x27: |red           2s|",
        "LCD 0x27: |Road 2   turn 2s|",
        "LCD 0x27: |red           1s|",
        "LCD 0x27: |Road 2   turn 1s|",
        "LCD 0x27: |switching       |",
        "LCD 0x27: |Road 1   turn 8s|",
        "LCD 0x27: |green         4s|",
    ];

    /// <summary>
    /// The owner's LCD traffic light, its Pico program with only the pins and the I2C bus changed, runs 15.5 s with the
    /// button on GPIO 10 pressed at 2.5 s, during lane 1's green: its console lines and each text its LCD shows are
    /// those the Pico version prints; the lights, the heartbeat and the turn display are on the pins the README says.
    /// </summary>
    [PicoFact]
    [RunsLong]
    public async Task The_LCD_traffic_light_prints_and_shows_what_the_Pico_version_does()
    {
        var example = await BuildAsync("esp32c5-lcd-traffic-light");

        var run = Run(example, 15_500, "--i2c", "0:0x27=lcd1602", "--input", "GPIO10=high@2500ms", "--input", "GPIO10=low@2700ms");

        // the same lines and the same screens, in the same order, as the Pico's
        Assert.Equal(PicoConsole, Text(run).Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(PicoLcd, run.After("LCD 0x27: "));
        Assert.Contains(run.Pins, p => p is { Pin: "GPIO10", State: "input pull-down" });

        // the press at 2.5 s cuts the green, as on the Pico
        var times = run.Times("USB: lane : 1 interrupted");
        Assert.Single(times);
        Assert.InRange(times[0], 2500, 2515);

        // the signal: lane 1 green on GPIO 9, then yellow (8), red (6); the heartbeat (23) toggles every 500 ms
        Assert.True(IsHigh(run.Pins, "GPIO9", 1000) && !IsHigh(run.Pins, "GPIO6", 1000), $"{example}\n{run}");
        Assert.True(IsHigh(run.Pins, "GPIO8", 3000) && !IsHigh(run.Pins, "GPIO9", 3000), $"{example}\n{run}");
        Assert.True(IsHigh(run.Pins, "GPIO6", 5000) && !IsHigh(run.Pins, "GPIO8", 5000), $"{example}\n{run}");
        Assert.True(IsHigh(run.Pins, "GPIO9", 8000), $"{example}\n{run}");
        var heartbeat = Led(run, 23);
        Assert.InRange(heartbeat.Count, 30, 32);
        Assert.InRange(heartbeat[2].Milliseconds - heartbeat[1].Milliseconds, 499.9, 500.2);

        // the turn display (A to G on GPIO 0, 1, 2, 3, 11, 12, 24) at 1 s into lane 1's turn shows 7: 0x07 is A, B and C
        var segments = new[] { 0, 1, 2, 3, 11, 12, 24 };
        Assert.Equal(
            [true, true, true, false, false, false, false],
            segments.Select(pin => IsHigh(run.Pins, "GPIO" + pin.ToString(CultureInfo.InvariantCulture), 1500)).ToArray());
    }

    /// <summary>
    /// The same LCD traffic light started as ESP-IDF's bootloader starts an app (chipsim --idf-bootloader: the image passes
    /// the bootloader's checks, the CPU is on the PLL at 160 MHz, the RTC watchdog runs and resets the chip after 9 s
    /// unless the program turns it off, the registers are the bootloader's): it prints and shows exactly what the ROM
    /// start does, the lines and LCD texts in the same order, for 15.5 s (past the watchdog's 9 s).
    /// </summary>
    [PicoFact]
    [RunsLong]
    public async Task The_LCD_traffic_light_started_as_the_IDF_bootloader_leaves_the_chip_does_the_same()
    {
        var example = await BuildAsync("esp32c5-lcd-traffic-light");

        var run = Run(example, 15_500, "--i2c", "0:0x27=lcd1602", "--input", "GPIO10=high@2500ms", "--input", "GPIO10=low@2700ms",
            "--idf-bootloader");

        Assert.Equal(PicoConsole, Text(run).Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(PicoLcd, run.After("LCD 0x27: "));
        Assert.Contains("stopped: 15500 ms of simulated time have passed", run.Stopped);
        Assert.True(IsHigh(run.Pins, "GPIO9", 1000) && !IsHigh(run.Pins, "GPIO6", 1000), $"{example}\n{run}");
        Assert.InRange(Led(run, 23).Count, 30, 32);
    }

    /// <summary>The servo on GPIO 6 sweeps 0 to 180 degrees and back: pulses of 1 to 2 ms, 50 times a second.</summary>
    [PicoFact]
    [RunsLong]
    public async Task The_servo_sweeps_with_pulses_of_1_to_2_ms_at_50_Hz_on_GPIO6()
    {
        var example = await BuildAsync("esp32c5-servo");

        var run = Run(example, 3800);

        Assert.Equal(
            "servo at 0 degrees: pulse 1000 us\r\nservo at 30 degrees: pulse 1166 us\r\nservo at 60 degrees: pulse 1333 us\r\n" +
            "servo at 90 degrees: pulse 1500 us\r\nservo at 120 degrees: pulse 1666 us\r\nservo at 150 degrees: pulse 1833 us\r\n" +
            "servo at 180 degrees: pulse 2000 us\r\nservo at 150 degrees: pulse 1833 us\r\nservo at 120 degrees: pulse 1666 us\r\n" +
            "servo at 90 degrees: pulse 1500 us\r\nservo at 60 degrees: pulse 1333 us\r\nservo at 30 degrees: pulse 1166 us\r\n" +
            "servo at 0 degrees: pulse 1000 us\r\n",
            Text(run));

        // the pin's measured pulses: 50 Hz, from 1 ms up to 2 ms
        var pulses = run.After("GPIO6 PWM").Select(line => PulseLine().Match(line)).Where(m => m.Success).ToList();
        Assert.NotEmpty(pulses);
        Assert.All(pulses, m => Assert.InRange(double.Parse(m.Groups["hz"].Value, CultureInfo.InvariantCulture), 49.9, 50.2));
        var lengths = pulses.Select(m => double.Parse(m.Groups["us"].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.InRange(lengths.Min(), 990, 1010);
        Assert.InRange(lengths.Max(), 1990, 2010);
    }

    /// <summary>
    /// The clock reads the DS1307's date (halted: the program sets one), shows the date and the time on the LCD and
    /// counts the seconds from its own clock.
    /// </summary>
    [PicoFact]
    [RunsLong]
    public async Task The_LCD_clock_shows_the_DS1307s_date_and_time_each_second()
    {
        var example = await BuildAsync("esp32c5-lcd-clock");

        var running = Run(example, 3500, "--i2c", "0:0x27=lcd1602", "--i2c", "0:0x68=ds1307@2026-11-27T18:00:00");
        var halted = Run(example, 2500, "--i2c", "0:0x27=lcd1602", "--i2c", "0:0x68=ds1307@halted");
        var noLcd = Run(example, 500);

        Assert.Equal("The time from the DS1307: 2026-11-27 18:00:00\r\n", Text(running));
        Assert.Equal(
            [
                "|Fri 27 Nov 2026 |", "|18:00:00        |", "|Fri 27 Nov 2026 |", "|18:00:01        |",
                "|Fri 27 Nov 2026 |", "|18:00:02        |", "|Fri 27 Nov 2026 |", "|18:00:03        |",
            ],
            running.After("LCD 0x27: ").Select(line => line[10..]));
        var seconds = running.Times("LCD 0x27: |18:00");
        Assert.InRange(seconds[1] - seconds[0], 999, 1002);

        Assert.Equal("No DS1307 running: the clock starts at 2026-11-27 18:00:00\r\n", Text(halted));
        Assert.Equal("|18:00:02        |", halted.After("LCD 0x27: ")[^1][10..]);
        Assert.Equal("No LCD answers at 0x27: check its wiring.\r\n", Text(noLcd));
    }

    /// <summary>
    /// Each example has a README with the build, flash and chipsim commands, a Wokwi circuit that wires only the pins
    /// the program uses (directly: LEDs from their pin to GND), and a program of the portable API alone on pins the
    /// board leaves a program.
    /// </summary>
    [Fact]
    public void Each_example_has_a_README_a_circuit_and_a_program_of_the_portable_API_on_free_pins()
    {
        Assert.Equal(["esp32c5-blink", "esp32c5-button", "esp32c5-classes", "esp32c5-lcd-clock", "esp32c5-lcd-traffic-light", "esp32c5-serial", "esp32c5-servo", "esp32c5-timer-blink"],
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

            // the console is the USB Serial/JTAG port, which Wokwi's serial monitor shows when the board says so
            // (docs.wokwi.com/guides/esp32: { "serialInterface": "USB_SERIAL_JTAG" }); without it, it shows UART0
            if (program.Contains("Console.", StringComparison.Ordinal))
            {
                Assert.True((string?)diagram["parts"]![0]!["attrs"]?["serialInterface"] == "USB_SERIAL_JTAG",
                    $"{example}: the board part has no \"serialInterface\": \"USB_SERIAL_JTAG\"");
            }
            var wires = diagram["connections"]!.AsArray().Select(wire => (From: (string)wire![0]!, To: (string)wire[1]!)).ToList();
            foreach (var (from, to) in wires)
            {
                foreach (var end in new[] { from, to }.Where(end => end.StartsWith("esp:", StringComparison.Ordinal)))
                {
                    var name = end["esp:".Length..];
                    Assert.DoesNotContain(name, new[] { "11", "12" }); // Wokwi's TX and RX
                    Assert.True(name.StartsWith("GND.", StringComparison.Ordinal) || name is "5V" or "3V3" ||
                        FreePins.Contains(WokwiPinNames.TryGetValue(name, out var gpio)
                            ? gpio
                            : int.Parse(name, CultureInfo.InvariantCulture)), $"{example}: {end}");
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
        /// <summary>The ROM-direct image, Name.rom.bin; <see cref="Bin"/> is the ESP-IDF app.</summary>
        public string Rom { get; init; } = "";

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
        Assert.Equal(4, lines.Length);
        // Name.bin, the ESP-IDF app (Wokwi); Name.rom.bin, the ROM-direct image chipsharp flash writes; the memory; which is which
        Assert.Equal(Path.GetFileNameWithoutExtension(project) + ".bin", Path.GetFileName(lines[0]));
        Assert.Equal(Path.GetFileNameWithoutExtension(project) + ".rom.bin", Path.GetFileName(lines[1]));
        Assert.True(MemoryReport().IsMatch(lines[2]), lines[2]);
        Assert.Contains("Wokwi", lines[3]);
        Assert.Contains("chipsharp flash writes it at 0x2000", lines[3]);
        var rom = EspImage.Read(File.ReadAllBytes(lines[1]));
        Assert.DoesNotContain(rom.Segments, segment => EspIdfBootloaderRules.IsMapped(segment.Address));
        Assert.True(EspAppDescription.IsPresent(rom.Segments[0].Data));

        // behind ESP-IDF's bootloader too (Wokwi, a devkit with its factory bootloader): every rule it checks holds
        Assert.Equal([], EspIdfBootloaderRules.Check(File.ReadAllBytes(lines[0])));
        var built = new BuiltExample(example, lines[0], lines[2], new FileInfo(lines[0]).Length) { Rom = lines[1] };
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
            [options.Contains("--idf-bootloader") ? example.Bin : example.Rom, "--ms", milliseconds.ToString(CultureInfo.InvariantCulture), .. options]));
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

    [GeneratedRegex(@"PWM (?<hz>[\d.]+) Hz, duty [\d.]+% \((?<us>[\d.]+) us high\)")]
    private static partial Regex PulseLine();

    [GeneratedRegex(@"^SRAM \d+(\.\d)? KB( \+ heap \d+ KB)? of 369\.4 KB \(\d+\.\d%\) \(stack 16 KB reserved\)$")]
    private static partial Regex MemoryReport();

    [GeneratedRegex(@"<ProjectReference Include=""[^""]*\\(?<name>[^\\""]+)\.csproj""")]
    private static partial Regex ProjectReference();
}
