using ChipSharp.Abstractions;
using ChipSharp.Backend.C;
using ChipSharp.Build;
using ChipSharp.Flasher.EasyPdkProg;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Image.Hex;
using ChipSharp.Runtime.Pdk14;
using ChipSharp.Runtime.Pfs154;
using ChipSharp.Targets;
using ChipSharp.Tests.CBackend;
using ChipSharp.Tests.Conformance;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Sdcc;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Padauk;

/// <summary>
/// The Padauk PFS154's runtime (src/ChipSharp.Runtime.Pfs154, on src/ChipSharp.Runtime.Pdk14): its parts and what a
/// build links of them, its clock and easypdkprog's calibration marker, what chipsharp check says of what the chip
/// cannot do, and C# programs built for pfs154-bare and run in chipsim: a blink (and what it takes of the chip), a button
/// on the pin event, the timer event, PWM and the clock.
/// </summary>
public sealed class Pfs154RuntimeTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void The_PFS154_is_built_with_the_pfs154_runtime_on_the_pdk14_one_which_a_build_finds_by_its_name()
    {
        var target = Targets.Load("pfs154-bare");
        var registry = DefaultComponents.CreateRegistry(new BuildContext(new BuildOptions(), target, "Blink", _scratch));

        Assert.Equal("pfs154", target.Runtime);
        Assert.IsType<Pfs154Runtime>(registry.Get<IArchRuntime>("pfs154"));
        Assert.IsType<Pdk14Runtime>(registry.Get<IArchRuntime>("pdk14"));
    }

    [Fact]
    public void Only_the_start_up_and_the_interrupt_handler_are_linked_whole_every_other_file_is_in_the_library()
    {
        var target = Targets.Load("pfs154-bare");
        var runtime = new Pfs154Runtime();

        List<GeneratedFile> files = [.. runtime.Startup(target).Files, .. runtime.InterruptEntry(target).Files];

        Assert.Empty(runtime.Atomics(target).Files);
        static bool IsSource(GeneratedFile file) => file.FileName.EndsWith(".c", StringComparison.Ordinal)
            || file.FileName.EndsWith(".s", StringComparison.Ordinal);
        Assert.Equal(["pdk14_startup.c", "pfs154_startup.c", "pdk14_interrupt.s"],
            files.Where(file => IsSource(file) && !file.Library).Select(file => file.FileName));
        Assert.All(files.Where(file => !IsSource(file)), file => Assert.False(file.Library, file.FileName));
        Assert.Equal(files.Count, files.Select(file => file.FileName).Distinct().Count());
        // the headers: the contract, the PDK14 runtime's, the chip's from chips/pfs154.json, the target's clock
        Assert.Equal(
            [
                "chipsharp_hardware.h", "pdk14_runtime.h", "pfs154_registers.h", "pfs154_interrupts.h", "pfs154_pins.h",
                "pfs154_runtime.h", "pfs154_clock.h", "pfs154_clock.inc",
            ],
            files.Where(file => !IsSource(file)).Select(file => file.FileName));
        // the failures with parameters, a file each
        Assert.Equal(Pdk14Runtime.ArgumentFailures.Select(failure => Pdk14Runtime.FailureFileName(failure.Name)),
            files.Select(file => file.FileName).Where(name => name.StartsWith("pdk14_fail_", StringComparison.Ordinal)));
        Assert.Contains("CHIPSHARP_NORETURN void ChipSharp_Runtime_LengthBelowZero(chipsharp_i32 value)\n{\n    (void)value;\n" +
            "    chipsharp_halt_failure();\n}\n", files.Single(file => file.FileName == "pdk14_fail_LengthBelowZero.c").Content);
    }

    [Fact]
    public void The_clock_is_the_IHRC_divided_as_the_target_says_with_easypdkprogs_calibration_marker_for_it()
    {
        var target = Targets.Load("pfs154-bare");

        var header = Pfs154Runtime.ClockHeader(target);

        Assert.Equal((2_000_000, 8, (byte)0x3c), Pfs154Runtime.SystemClock(target));
        Assert.Contains("#define PFS154_SYSTEM_CLOCK_HZ 2000000ul\n", header);
        Assert.Contains("#define PFS154_SYSTEM_CLOCK_SHIFT 3u\n", header);
        Assert.Contains("#define PFS154_CLKMD \"0x3c\"\n", header);
        // the flasher's marker, word for word: 'R', 'C', 1, 2000000 in 4 bytes, 5000 in 2, IHRCR's address
        Assert.Equal(EasyPdkProgFlasher.IhrcCalibrationMarker(2_000_000, 5000),
            Pfs154Runtime.IhrcCalibrationMarker(2_000_000, Pfs154Runtime.CalibrationMillivolts));
        Assert.Contains("#define PFS154_IHRC_CALIBRATION \\\n    \"and a, #0x52\\n\" \\\n    \"and a, #0x43\\n\" \\\n" +
            "    \"and a, #0x01\\n\" \\\n    \"and a, #0x80\\n\" \\\n    \"and a, #0x84\\n\" \\\n    \"and a, #0x1e\\n\" \\\n" +
            "    \"and a, #0x00\\n\" \\\n    \"and a, #0x88\\n\" \\\n    \"and a, #0x13\\n\" \\\n    \"and a, #0x0b\\n\"\n", header);
        Assert.Contains("    PFS154_SYSTEM_CLOCK_SHIFT = 3\n", Pfs154Runtime.ClockInclude(target));
    }

    [Fact]
    public void A_clock_the_runtime_does_not_set_up_is_an_error_that_says_which_it_does()
    {
        var folder = Path.Combine(_scratch, "targets");
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "targets"), "*.json",
            SearchOption.AllDirectories))
        {
            var copy = Path.Combine(folder, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "targets"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        File.WriteAllText(Path.Combine(folder, "board", "pfs154-odd.json"),
            """{ "name": "pfs154-odd", "inherits": "pfs154", "clockHz": 1600000, "clock": { "source": "ihrc", "divider": 10 } }""");
        File.WriteAllText(Path.Combine(folder, "board", "pfs154-wrong.json"),
            """{ "name": "pfs154-wrong", "inherits": "pfs154", "clockHz": 4000000, "clock": { "source": "ihrc", "divider": 8 } }""");
        var targets = new TargetLoader(folder);

        var odd = Assert.Throws<ChipSharpException>(() => Pfs154Runtime.ClockHeader(targets.Load("pfs154-odd")));
        var wrong = Assert.Throws<ChipSharpException>(() => Pfs154Runtime.ClockHeader(targets.Load("pfs154-wrong")));

        Assert.StartsWith("Target 'pfs154-odd': the PFS154's runtime runs it on the IHRC divided by 2, 4, 8, 16, 32 or 64",
            odd.Message);
        Assert.Equal("Target 'pfs154-wrong': its \"clockHz\" is \"4000000\", but the IHRC / 8 is 2000000 Hz.", wrong.Message);
    }

    [Fact]
    public void Check_says_what_the_PFS154_does_not_have_a_pin_the_pin_event_cannot_watch_and_a_pin_with_no_PWM()
    {
        var target = Targets.Load("pfs154-bare");
        string Summary(string statements, string name)
        {
            var dll = CSharpCompiler.Compile(["using ChipSharp.Hardware;\n" + statements + """

                static partial class Program
                {
                    [Interrupt(Event.Pin)]
                    static void Changed() => Pin.Toggle(4);
                }
                """], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], name);
            var analysis = WholeProgramPass.Check(IlAssembly.Load(dll, name + ".dll"), target);
            Assert.False(analysis.IsSupported, name);
            return analysis.Summary;
        }

        Assert.Contains("pin 3 (PA3) cannot be watched (the pin event) on the target 'pfs154-bare'",
            Summary("Pin.Input(3);\nPin.WatchRising(3);", "WatchPa3"));
        var watch = Summary("Pin.Watch(12);", "WatchPb4");
        Assert.True(watch.Contains("pin 12 (PB4) cannot be watched (the pin event)", StringComparison.Ordinal)
            && watch.Contains("Watched (the pin event) can be pins 0 (PA0) or 8 (PB0).", StringComparison.Ordinal), watch);
        Assert.Contains("pin 4 (PA4) cannot be a PWM output on the target 'pfs154-bare'",
            Summary("Pin.Watch(0);\nPwm.Output(4, 1000);", "PwmPa4"));
        Assert.Contains("ChipSharp.Hardware.Uart needs a serial port (UART), and the target 'pfs154-bare' has none",
            Summary("Pin.Watch(8);\nUart.Open(0, 1, 2, 9600);", "Uart"));
        Assert.Contains("Pwm.Duty takes a double, and the target 'pfs154-bare' has no 64-bit double",
            Summary("Pin.Watch(0);\nPwm.Output(13, 1000);\nPwm.Duty(13, 0.5);", "Duty"));
    }

    [SdccFact]
    public void Before_anything_else_the_start_up_trims_the_IHRC_sets_the_clock_and_has_easypdkprogs_marker()
    {
        var program = Link("""
            using ChipSharp.Hardware;

            Pin.Output(4);
            Pin.Write(4, true);
            """, "Start");

        // at the address the map gives __sdcc_external_startup, which SDCC's start-up calls first
        var start = (uint)program.Symbols["___sdcc_external_startup"] / 2;
        ushort[] trimAndClock = [0x3fed, 0x018b, 0x2f3c, 0x0183]; // call 0x7ed, mov.io 0x0b,a, mov a,#0x3c, mov.io 0x03,a
        Assert.Equal([.. trimAndClock, .. EasyPdkProgFlasher.IhrcCalibrationMarker(2_000_000, 5000), (ushort)0x0200],
            Enumerable.Range(0, 15).Select(i => program.Word(start + (uint)i)));
        // the marker once in the program, as easypdkprog wants it
        var marker = EasyPdkProgFlasher.IhrcCalibrationMarker(2_000_000, 5000);
        Assert.Single(Enumerable.Range(0, 2048 - marker.Count),
            word => marker.Select((value, i) => program.Word((uint)(word + i)) == value).All(same => same));
    }

    [SdccFact]
    public async Task A_blink_takes_a_few_hundred_words_and_toggles_its_LED_every_500_ms()
    {
        var program = Link("""
            using ChipSharp.Hardware;

            Pin.Output(4);
            while (true)
            {
                Pin.Toggle(4);
                Timing.DelayMilliseconds(500);
            }
            """, "Blink");

        output.WriteLine(program.ToString());
        var run = await Pfs154.RunAsync(program.Ihx, new ChipStimulus(2600));

        Assert.True(run.Stop == ChipStop.TimeUp, run.ToString());
        var toggles = run.Levels(4).Select(change => change.Milliseconds).ToList();
        Assert.True(toggles.Count == 7, run.ToString());
        Assert.All(toggles.Skip(2).Zip(toggles.Skip(1)), pair => Assert.InRange(pair.First - pair.Second, 500, 500.2));
        // the program memory and RAM a blink takes, the memory report's words and bytes
        Assert.InRange(program.Use.CodeUsed / 2, 200UL, 400UL);
        Assert.InRange(program.Use.DataUsed, 1UL, 40UL);
        // the runtime's library files it has: the pins it uses, the delay, T16, the stops; nothing of the events
        Assert.Equal(["pdk14_stops.s.rel", "pfs154_delay_milliseconds.s.rel", "pfs154_pin.s.rel", "pfs154_pin_output.s.rel",
            "pfs154_pin_toggle.s.rel", "pfs154_t16.s.rel"], program.Library.Order(StringComparer.Ordinal));
    }

    [SdccFact]
    public async Task A_button_on_PA0_toggles_the_LED_through_the_pin_event_on_its_falling_edges()
    {
        var program = Link("""
            using ChipSharp.Hardware;

            Pin.Output(4);
            Pin.Input(0);
            Registers.Write(0x12, 0x01); // PAPH: PA0's pull-up on
            Pin.WatchFalling(0);
            while (true)
            {
            }

            static partial class Program
            {
                [Interrupt(Event.Pin)]
                static void Pressed() => Pin.Toggle(4);
            }
            """, "Button");

        // pressed at 10, 30 and 50 ms (to GND), let go between
        var run = await Pfs154.RunAsync(program.Ihx, new ChipStimulus(70)
        {
            Inputs = [new(0, false, 10), new(0, true, 20), new(0, false, 30), new(0, true, 40), new(0, false, 50)],
        });

        Assert.True(run.Stop == ChipStop.TimeUp, run.ToString());
        Assert.Contains(run.Pins, change => change.Pin == 0 && change.State == "input pull-up");
        var toggles = run.Levels(4).Skip(1).Select(change => change.Milliseconds).ToList();
        Assert.True(toggles.Count == 3, run.ToString());
        Assert.All(toggles.Zip([10.0, 30.0, 50.0]), pair => Assert.InRange(pair.First, pair.Second, pair.Second + 0.15));
    }

    [SdccFact]
    public async Task The_timer_event_toggles_the_LED_every_10_ms_while_Main_waits_and_After_comes_once()
    {
        var program = Link("""
            using ChipSharp.Hardware;

            Pin.Output(4);
            Timing.EveryMilliseconds(10);
            Timing.DelayMilliseconds(55);
            Timing.AfterMicroseconds(2500);
            Timing.DelayMilliseconds(20);

            static partial class Program
            {
                [Interrupt(Event.Timer)]
                static void Tick() => Pin.Toggle(4);
            }
            """, "TimerBlink");

        var run = await Pfs154.RunAsync(program.Ihx, new ChipStimulus(200));

        // 5 times every 10 ms, then once 2.5 ms after the delay, then the program has ended
        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        var levels = run.Levels(4);
        var toggles = levels.Skip(1).Select(change => change.Milliseconds - levels[0].Milliseconds).ToList();
        Assert.True(toggles.Count == 6, run.ToString());
        Assert.All(toggles.Take(5).Select((at, i) => (at, i)), toggle =>
            Assert.InRange(toggle.at, 10 * (toggle.i + 1), 10 * (toggle.i + 1) + 0.3));
        Assert.InRange(toggles[5], 57.5, 58.0);
    }

    [SdccFact]
    public async Task PWM_on_PB5_and_PA3_makes_the_frequencies_and_pulses_asked_for()
    {
        var program = Link("""
            using ChipSharp.Hardware;

            Pwm.Output(13, 1000);
            Pwm.PulseMicroseconds(13, 300);
            Pwm.Output(3, 50);
            Pwm.PulseMicroseconds(3, 1500);
            Timing.DelayMilliseconds(100);
            Pwm.Stop(3);
            Timing.DelayMilliseconds(10);
            """, "Pwm");

        var run = await Pfs154.RunAsync(program.Ihx, new ChipStimulus(300));

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        // PB5: 1008 Hz (the system clock / 31 / 64), 300 us high to a count of 15.5 us; PA3: 50 Hz as near as TM2 makes it
        var pb5 = run.Pwm.Last(period => period.Pin == 13 && period.Hertz > 0);
        Assert.InRange(pb5.Hertz, 1007, 1009);
        Assert.InRange(pb5.HighMicroseconds, 292, 308);
        var pa3 = run.Pwm.Last(period => period.Pin == 3 && period.Hertz > 0);
        Assert.InRange(pa3.Hertz, 48, 52);
        Assert.InRange(pa3.HighMicroseconds, 1470, 1530);
        Assert.StartsWith("input", run.Pins.Last(change => change.Pin == 3).State, StringComparison.Ordinal);
    }

    [SdccFact]
    public async Task The_clock_counts_microseconds_for_Stopwatch_and_milliseconds_for_TickCount64()
    {
        var program = Link("""
            using System;
            using System.Diagnostics;
            using ChipSharp.Hardware;

            Pin.Output(4);
            Pin.Output(9);
            var start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetTimestamp() - start < 70_000)
            {
            }

            Pin.Toggle(4);
            start = Environment.TickCount64;
            Timing.DelayMilliseconds(20);
            start = Environment.TickCount64 - start;
            if (start >= 20 && start <= 21)
            {
                Pin.Toggle(9);
            }
            """, "Clock", stack: "16"); // its 101 bytes of variables and the target's 48 for the stack outgrow the 128; the loop is shallow

        var run = await Pfs154.RunAsync(program.Ihx, new ChipStimulus(200));

        // 70 ms of Stopwatch, past a round of T16 (65.536 ms); 20 ms of TickCount64 (or 21, as the milliseconds fall)
        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        var levels = run.Levels(4);
        Assert.InRange(levels[1].Milliseconds - levels[0].Milliseconds, 70, 70.3);
        Assert.True(run.Levels(9).Count == 2, run.ToString());
    }

    private static ChipSimulator Pfs154 => ChipSimulators.Get("pfs154-bare");

    /// <summary>
    /// C# with ChipSharp.Hardware built for pfs154-bare as a build does (the whole-program pass, the c code generator,
    /// the runtime, the toolchain sdcc), SDCC's files kept: the .ihx, its map's symbols, what it takes of the chip, and
    /// the library files linked. <paramref name="stack"/> replaces the target's RAM for the stack (link.stack), for a
    /// program that is shallow and needs the room.
    /// </summary>
    private LinkedProgram Link(string source, string name, string? stack = null)
    {
        var folder = Path.Combine(_scratch, name);
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, name + ".dll");
        File.WriteAllBytes(dll, CSharpCompiler.Compile([source], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], name));
        var target = Targets.Load("pfs154-bare");
        if (stack is not null)
        {
            target = CSdcc.WithStack(target, stack);
        }

        var module = ChipSharpBuilder.Translate(dll, target);
        var linked = module.UsesHeap ? target : target.WithoutHeap();
        var runtime = new Pfs154Runtime();
        var binary = new SdccToolchain(new SdccToolchainOptions
        {
            OutputFolder = Path.Combine(folder, "sdcc"),
            ProgramName = name,
            CacheFolder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "sdcc-cache"),
        }).Build(new GeneratedCode(
        [
            .. new CCodeGenerator().Generate(module, linked).Files, .. runtime.Startup(linked).Files,
            .. runtime.InterruptEntry(linked).Files,
        ]), linked);
        var map = Path.ChangeExtension(binary.Path, ".map");
        // "./Blink.lib    [ pfs154_pin_toggle.s.rel ]": the files of the build's library the linker took
        var library = File.ReadAllLines(map).Where(line => line.StartsWith($"./{name}.lib ", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf('[', StringComparison.Ordinal) + 1)..line.IndexOf(']', StringComparison.Ordinal)].Trim())
            .ToList();
        return new LinkedProgram(binary.Path, SdccPrograms.MapSymbols(map), SdccMemoryUse.Read(binary, linked), library,
            IntelHexFile.Read(binary.Path));
    }

    private sealed record LinkedProgram(string Ihx, IReadOnlyDictionary<string, ulong> Symbols, SdccMemoryUse Use,
        IReadOnlyList<string> Library, IntelHexFile Hex)
    {
        private readonly Dictionary<uint, byte> _bytes = Hex.Blocks
            .SelectMany(block => block.Data.Select((value, i) => (Address: block.Address + (uint)i, value)))
            .ToDictionary(b => b.Address, b => b.value);

        /// <summary>The program memory's word at <paramref name="address"/> (a word address), 0x3fff where blank.</summary>
        public ushort Word(uint address) => _bytes.TryGetValue(2 * address, out var low)
            ? (ushort)(low | _bytes.GetValueOrDefault(2 * address + 1) << 8)
            : (ushort)0x3fff;

        public override string ToString() => $"{Ihx}: {Use.CodeUsed / 2} words, {Use.DataUsed} bytes of RAM; " +
            string.Join(", ", Library);
    }
}
