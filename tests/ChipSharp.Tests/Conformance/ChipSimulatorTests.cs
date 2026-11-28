using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Support;

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
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void Every_chips_pins_are_its_targets_and_each_is_used_once()
    {
        Assert.Equal(["pico", "uno", "pfs154-bare", "stm8s103-board", "esp32c5-devkitc", "ch32v003-evt"],
            ChipSimulators.All.Select(chip => chip.Target));
        Assert.All(ChipSimulators.All.Where(chip => chip.TargetExists), chip =>
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
    public void A_chip_says_why_a_case_cannot_run_and_the_STM8S103_runs_every_case_its_target_has()
    {
        var stm8 = ChipSimulators.Get("stm8s103-board");

        // chipsim has devices on the STM8S103's buses and voltages on its analog inputs: nothing of its target is left out
        Assert.All((string[])["chip", "i2c", "spi", "adc", "uart", "interrupts.pin", "interrupts.timer", "interrupts.uart",
            "clock", "console", "consoleInput", "pwm"], need =>
        {
            Assert.True(stm8.Has(need), need);
            Assert.Equal(stm8.Missing, stm8.MissingFor([need]));
        });
        Assert.False(ChipSimulators.Get("pfs154-bare").Has("uart"));
    }

    [Simulators.Ch32v003Fact]
    public async Task The_CH32V003_s_adapter_runs_an_image_in_chipsim_and_gives_its_pins_and_USART1_by_number()
    {
        var chip = ChipSimulators.Get("ch32v003-evt");
        var program = await Simulators.Ch32v003CProgram.BuildAsync("""
            #include "ch32.h"
            int main(void)
            {
                pin_output(2, 0);                 /* PC0: the adapter's Output, 16 */
                pin_write(2, 0, 1);
                usart_init(8000000, 115200);
                usart_print("ok\n");
                usart_flush();
                return 0;
            }
            """, _scratch);

        var run = await chip.RunAsync(program.Elf, new ChipStimulus(20) { Sends = [new ChipSend(0, [0x41], 1)] });

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        Assert.Equal(["low", "high"], run.Levels(chip.Pins.Output).Select(change => change.State));
        Assert.Contains(run.Pins, change => change.Pin == chip.Pins.Uart0!.Value.Tx && change.State == "high"); // PD5: TX idle
        Assert.Equal("ok\n"u8.ToArray(), run.SerialOf(0));
        Assert.Equal([115942], run.BaudRates[0]);
        if (!chip.TargetExists)
        {
            Assert.Contains("is not in ChipSharp yet", chip.Missing, StringComparison.Ordinal);
            Assert.True(chip.Has("chip"));
            Assert.False(chip.Has("uart"));
        }
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
