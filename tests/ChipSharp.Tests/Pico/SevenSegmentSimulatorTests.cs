using ChipSharp.Abstractions;
using ChipSharp.Components;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// ChipSharp.Components' SevenSegment on the simulated Pico, read from the pins: every digit and char on a
/// common-cathode display on GPIO 2 to 8, on a Pico without a heap; a common-anode display, whose levels are turned
/// round, next to one on pins in any order with a decimal point; a display made with default, or on pins the chip does
/// not have, which drives nothing; and in the .elf, its shapes in flash when a program uses it and nothing of it when
/// it does not.
/// </summary>
public sealed class SevenSegmentSimulatorTests : IDisposable
{
    /// <summary>Each step of the programs below: they show something, then wait this long.</summary>
    private const int Step = 10;

    /// <summary>The 16 shapes, 0 to 9 and A b C d E F, as the table in flash has them.</summary>
    private static readonly byte[] Shapes = [.. "0123456789ABCDEF".Select(SevenSegment.Pattern)];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [SimulatorFact]
    public async Task A_common_cathode_display_on_GPIO_2_to_8_shows_every_digit_and_char_on_a_Pico_without_a_heap()
    {
        var noHeap = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");
        var program = Build("""
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            class Program
            {
                const string Chars = "0123456789AbCdEFabcdef-_ x?";

                static void Main()
                {
                    var display = new SevenSegment(firstPin: 2);
                    Wait();
                    for (var digit = -1; digit <= 10; digit++)
                    {
                        display.Show(digit);
                        Wait();
                    }

                    for (var i = 0; i < Chars.Length; i++)
                    {
                        display.Show(Chars[i]);
                        Wait();
                    }

                    display.Show(int.MinValue);
                    Wait();
                    display.Show(int.MaxValue);
                    Wait();
                }

                static void Wait() => Timing.DelayMilliseconds(10);
            }
            """, "SevenSegmentShapes", noHeap);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.EndsWith("the program has finished: the core sleeps and nothing is scheduled to wake it", run.Stopped);
        Assert.DoesNotContain("chipsharp_new_object", ElfFile.Read(program.Elf).Symbols.Keys);

        // blank once made, then each shape for one step: '-' for -1, 10, 'x', '?' and the extremes
        var pins = run.Pins;
        byte[] expected =
        [
            0x00,
            0x40, .. Shapes[..10], 0x40,
            .. Shapes[..10], .. Shapes[10..], .. Shapes[10..], 0x40, 0x08, 0x00, 0x40, 0x40,
            0x40, 0x40,
        ];
        Assert.Equal(expected.Select(pattern => Hex(pattern)), Enumerable.Range(0, expected.Length).Select(i => Hex(Shown(pins, Segments(2), At(i)))));

        // each pin an output, low (dark) from the start; nothing else driven, no DP
        Assert.Equal(Enumerable.Range(2, 7), pins.Where(p => p.State == "low" && p.Milliseconds < 1).Select(p => p.Pin));
        Assert.All(pins, p => Assert.InRange(p.Pin, 2, 8));
    }

    [SimulatorFact]
    public async Task A_common_anode_display_turns_the_levels_round_and_one_on_any_pins_has_a_decimal_point_of_its_own()
    {
        var program = Build("""
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    // no pins, or none the chip has: nothing is driven
                    SevenSegment none = default;
                    none.Show(8);
                    none.Write(0xFF);
                    none.Point(true);
                    none.Blank();
                    var outside = new SevenSegment(firstPin: 256 + 2);
                    outside.Show(8);
                    outside.Write(0xFF);
                    Wait();

                    // GPIO 9 to 15; A to G on 21, 20, 19, 18, 17, 16, 22 and DP on 26: both blank, then 7
                    var anode = new SevenSegment(firstPin: 9, commonAnode: true);
                    var any = new SevenSegment(a: 21, b: 20, c: 19, d: 18, e: 17, f: 16, g: 22, dp: 26);
                    Wait();
                    anode.Show(7);
                    any.Show(7);
                    Wait();

                    // the point: none on the anode display; Show leaves it, Write and Blank set it
                    anode.Point(true);
                    any.Point(true);
                    Wait();
                    any.Show(3);
                    anode.Show('b');
                    Wait();
                    any.Write(0x80 | 0x06);
                    anode.Write(0x80 | 0x5B);
                    Wait();
                    any.Write(0x3F);
                    Wait();
                    any.Point(true);
                    any.Blank();
                    anode.Blank();
                    Wait();
                    any.Point(true);
                    anode.Show('-');
                    Wait();
                    any.Point(false);
                    Wait();
                }

                static void Wait() => Timing.DelayMilliseconds(10);
            }
            """, "SevenSegmentPins");

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        var pins = run.Pins;

        // nothing before the two displays are made, 10 ms in
        Assert.All(pins, p => Assert.True(p.Milliseconds >= Step, $"GPIO{p.Pin} {p.State} at {p.Milliseconds} ms"));

        // the anode display lights a segment whose pin is low: high (dark) once made
        Assert.Equal(Enumerable.Range(9, 7), pins.Where(p => p.State == "high" && p.Milliseconds < Step + 1).Select(p => p.Pin).Order());
        int[] any = [21, 20, 19, 18, 17, 16, 22, 26];
        (int Anode, int Any)[] expected =
        [
            (0x00, 0x00),       // made: both blank
            (0x07, 0x07),
            (0x07, 0x87),       // the point on the one that has it
            (0x7C, 0xCF),       // 'b'; 3, the point still on
            (0x5B, 0x86),       // raw: 2 (no DP on the anode display), and 1 with the point
            (0x5B, 0x3F),       // 0, the point off
            (0x00, 0x00),       // Blank: the point too
            (0x40, 0x80),       // the point alone
            (0x40, 0x00),
        ];
        Assert.Equal(expected.Select(step => (Hex(step.Anode), Hex(step.Any))), Enumerable.Range(1, expected.Length)
            .Select(i => (Hex(Shown(pins, Segments(9), At(i), commonAnode: true)), Hex(Shown(pins, any, At(i))))));
        Assert.Equal(Enumerable.Range(9, 7).Concat(any).Order(), pins.Select(p => p.Pin).Distinct().Order());
    }

    [PicoFact]
    public void Its_shapes_are_in_flash_in_a_program_that_uses_it_and_nothing_of_it_is_in_one_that_does_not()
    {
        const string Uses = """
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            var display = new SevenSegment(a: 2, b: 3, c: 4, d: 5, e: 6, f: 7, g: 8, dp: 9);
            for (var digit = 0; ; digit = (digit + 1) % 10)
            {
                Show(display, digit);
                Timing.DelayMilliseconds(1000);
            }

            static void Show(SevenSegment display, int digit)
            {
                display.Show(digit);
                display.Show((char)('A' + digit));
                display.Write(SevenSegment.Pattern(digit) | 0x80);
                display.Point(digit % 2 == 0);
                display.Blank();
            }
            """;
        const string DoesNot = """
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            Pin.Output(2);
            for (var digit = 0; ; digit = (digit + 1) % 10)
            {
                Pin.Write(2, digit % 2 == 0);
                Timing.DelayMilliseconds(1000);
            }
            """;

        var used = ElfFile.Read(Build(Uses, "WithDisplay").Elf);
        var unused = ElfFile.Read(Build(DoesNot, "WithoutDisplay").Elf);

        // the 16 bytes of the table once, or not at all; no function of it
        Assert.Equal(1, Count(PicoBuild.FlashImage(used), Shapes));
        Assert.Equal(0, Count(PicoBuild.FlashImage(unused), Shapes));
        Assert.DoesNotContain(unused.Symbols.Keys, name => name.Contains("SevenSegment", StringComparison.Ordinal));

        // every method of it and its table, in less than half a kilobyte (both programs have Pin's runtime functions)
        var cost = PicoBuild.FlashImage(used).Length - PicoBuild.FlashImage(unused).Length;
        Assert.InRange(cost, 200, 512);
    }

    /// <summary>Builds <paramref name="source"/> for the Pico (or <paramref name="target"/>) with ChipSharp.Components.</summary>
    private PicoProgram Build(string source, string name, TargetDescription? target = null) =>
        PicoBuild.Build(source, name, Path.Combine(_scratch, name), target ?? PicoBuild.Pico, [PicoBuild.ComponentsAssembly]);

    /// <summary>The middle of step <paramref name="step"/> (0 for the first wait), in milliseconds from the start.</summary>
    private static double At(int step) => step * Step + Step / 2.0;

    /// <summary>The pins of segments A to G of a display on <paramref name="first"/> to <paramref name="first"/> + 6.</summary>
    private static int[] Segments(int first) => [.. Enumerable.Range(first, 7)];

    private static string Hex(int pattern) => $"0x{pattern:X2}";

    /// <summary>
    /// The segments lit at <paramref name="at"/> ms on the display whose segments, from A on, are on
    /// <paramref name="segmentPins"/>: bit 0 for A; lit when the pin is high (low for a common anode).
    /// </summary>
    private static int Shown(IReadOnlyList<PinChange> pins, int[] segmentPins, double at, bool commonAnode = false)
    {
        var shown = 0;
        for (var segment = 0; segment < segmentPins.Length; segment++)
        {
            var level = pins.LastOrDefault(p => p.Pin == segmentPins[segment] && p.Milliseconds <= at && p.State is "high" or "low");
            Assert.True(level is not null, $"GPIO{segmentPins[segment]} is not driven at {at} ms");
            if (level.State == (commonAnode ? "low" : "high"))
            {
                shown |= 1 << segment;
            }
        }

        return shown;
    }

    /// <summary>How many times <paramref name="part"/> is in <paramref name="bytes"/>.</summary>
    private static int Count(byte[] bytes, byte[] part)
    {
        var count = 0;
        for (var at = 0; at <= bytes.Length - part.Length; at++)
        {
            if (bytes.AsSpan(at, part.Length).SequenceEqual(part))
            {
                count++;
            }
        }

        return count;
    }
}
