using ChipSharp.Tests.Sdcc;

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The adapters themselves: every chip's pins are pins its target has, used once each; and the SDCC chips' adapters run
/// an image in chipsim and give back its pins by the target's numbers, as the Pico's and the Uno's do, before ChipSharp
/// builds C# for them (hand-written C here).
/// </summary>
public sealed class ChipSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void Every_chips_pins_are_its_targets_and_each_is_used_once()
    {
        Assert.Equal(["pico", "uno", "pfs154-bare", "stm8s103-board"], ChipSimulators.All.Select(chip => chip.Target));
        Assert.All(ChipSimulators.All, chip =>
        {
            var pins = chip.Pins;
            List<int> used =
            [
                pins.Output, pins.Second, pins.Pass, pins.Fail, pins.Input,
                .. pins.Adc is { } adc ? [adc] : Array.Empty<int>(),
                .. pins.Pwm is { } pwm ? [pwm] : Array.Empty<int>(),
                .. pins.Uart0 is { } uart ? [uart.Tx, uart.Rx] : Array.Empty<int>(),
                .. pins.I2c0 is { } i2c ? [i2c.Sda, i2c.Scl] : Array.Empty<int>(),
                .. pins.Spi0 is { } spi ? [spi.Sck, spi.Tx, spi.Rx] : Array.Empty<int>(),
            ];
            Assert.True(used.Count == used.Distinct().Count(), $"{chip}: a pin is used twice");

            // a chip with names for its pins (the Uno, the SDCC chips) names each pin used
            if (chip.Description.Settings["pinNames"] is { } names)
            {
                Assert.All(used, pin => Assert.False(string.IsNullOrEmpty(names[pin]?.GetValue<string>()), $"{chip}: pin {pin}"));
            }

            // the capabilities' pins are the target file's
            var functions = chip.Description.Settings["pinFunctions"];
            if (pins.Adc is { } analog)
            {
                Assert.Contains(analog, functions!["adc"]!.AsArray().Select(n => n!.GetValue<int>()));
            }

            if (pins.Pwm is { } output && functions?["pwm"] is { } pwms)
            {
                Assert.Contains(output, pwms.AsArray().Select(n => n!.GetValue<int>()));
            }

            Assert.Equal(chip.Description.HasCapability("i2c"), pins.I2c0 is not null);
            Assert.Equal(chip.Description.HasCapability("spi"), pins.Spi0 is not null);
        });
    }

    [Fact]
    public void A_chip_says_why_a_case_cannot_run_and_a_part_its_simulator_lacks_skips_only_its_cases()
    {
        var stm8 = ChipSimulators.Get("stm8s103-board");

        Assert.Equal(stm8.Missing, stm8.MissingFor(["chip"]));
        Assert.StartsWith("chipsim's STM8S103 has its I2C controller's registers", stm8.MissingFor(["i2c"]));
        Assert.False(ChipSimulators.Get("pfs154-bare").Has("uart"));
        Assert.True(stm8.Has("uart") && stm8.Has("chip") && !stm8.Has("interrupts.timer"));
    }

    [SdccFact]
    public async Task The_SDCC_chips_adapters_run_an_image_in_chipsim_and_give_its_pins_by_the_targets_numbers()
    {
        foreach (var (target, pin) in new[] { ("pfs154-bare", 4), ("stm8s103-board", 13) })
        {
            var chip = ChipSimulators.Get(target);
            var binary = SdccPrograms.Build(chip.Description, Path.Combine(_scratch, target));

            var run = await chip.RunAsync(binary.Path, new ChipStimulus(5));

            // the hand-written blink drives PA4 (the PFS154's) or PB5 (the STM8S103's) and runs until the time is up
            Assert.True(run.Stop == ChipStop.TimeUp, run.ToString());
            Assert.Contains(run.Pins, change => change.Pin == pin && change.State is "low" or "high");
            Assert.All(run.Pins, change => Assert.InRange(change.Pin, 0, 31));
        }
    }
}
