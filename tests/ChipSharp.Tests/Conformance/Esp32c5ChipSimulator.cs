using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Simulators;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The ESP32-C5-DevKitC-1 (esp32c5-devkitc, milestone M4) in ChipSharp's own simulator (chipsim's "esp32c5", in
/// process), which loads the .elf (or the ESP image) the build makes and runs it as the ROM leaves the chip. Pins are
/// GPIO numbers: the program's pins are those the board leaves free (not the strapping pins 2, 3, 7 and 25-28, the
/// module's flash and PSRAM on 15-22, USB on 13 and 14, the RGB LED on 27); UART0 (GPIO 11 TX, 12 RX) is port 0 and the
/// console. Its cases are skipped, saying why, until the chip's runtime implements the hardware contract (pins, the
/// clock and the delays, the console on UART0, the events: M4's wave 1); chipsim does not simulate its ADC, PWM, I2C and
/// SPI yet (<see cref="Unsimulated"/>).
/// </summary>
internal sealed partial class Esp32c5ChipSimulator() : ChipSimulator("esp32c5-devkitc")
{
    /// <summary>The guards of the hardware chipsim does not simulate on the ESP32-C5 yet, with why.</summary>
    private static readonly Dictionary<string, string> Unsimulated = new()
    {
        ["adc"] = "chipsim does not simulate the ESP32-C5's ADC yet.",
        ["pwm"] = "chipsim does not simulate the ESP32-C5's LEDC (PWM) yet.",
        ["i2c"] = "chipsim does not simulate the ESP32-C5's I2C controller yet.",
        ["spi"] = "chipsim does not simulate the ESP32-C5's SPI controller yet.",
    };

    private string? _missing;
    private bool _checked;

    public override string? Missing
    {
        get
        {
            if (!_checked)
            {
                _missing = FindMissing();
                _checked = true;
            }

            return _missing;
        }
    }

    private string? FindMissing()
    {
        if (PicoTools.MissingLlvm.Value is { } llvm)
        {
            return llvm;
        }

        var registry = DefaultComponents.CreateRegistry(new BuildContext(new BuildOptions(), Description, "Program",
            Path.GetTempPath()));
        if (!registry.TryGet<IArchRuntime>(Description.Runtime!, out var runtime))
        {
            return $"The runtime \"{Description.Runtime}\" is not in ChipSharp yet.";
        }

        // the hardware contract's first function, Pin.Output, defined in a C file: a runtime that has it implements the
        // contract
        return runtime.Startup(Description).Files.Any(file => !file.FileName.EndsWith(".h", StringComparison.Ordinal) &&
            PinOutputDefinition().IsMatch(file.Content))
            ? null
            : "The ESP32-C5's runtime does not implement the hardware contract yet (pins, the clock and the delays, the " +
                "console on UART0, the events: milestone M4, wave 1): 'esp32c5-devkitc' runs the conformance suite in " +
                "chipsim's esp32c5 once it does.";
    }

    public override string? MissingFor(IReadOnlyList<string> needs) =>
        needs.Select(need => Unsimulated.GetValueOrDefault(need)).FirstOrDefault(reason => reason is not null) ?? Missing;

    public override ChipPins Pins { get; } = new(Output: 6, Second: 8, Pass: 9, Fail: 10, Input: 23)
    {
        Uart0 = (11, 12),
    };

    // up to 240 MHz, a peripheral access a few cycles more; the delays and the clock on SYSTIMER's 16 MHz count
    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.020, LatencyMilliseconds: 0.010,
        ClockMilliseconds: 0.010);

    public override Task<ChipRun> RunAsync(string image, ChipStimulus stimulus)
    {
        return Task.Run(() =>
        {
            var result = Simulation.Run(new SimulationOptions
            {
                Chip = "esp32c5",
                Image = image,
                Milliseconds = stimulus.Milliseconds,
                Inputs = [.. stimulus.Inputs.Select(input => new PinInput(Name(input.Pin), input.High, input.AtMilliseconds))],
                Sends = [.. stimulus.Sends.Select(send => send.Port == 0
                    ? new SerialSend(send.Bytes, send.AtMilliseconds, "UART0")
                    : throw new NotSupportedException("the ESP32-C5's adapter has one serial port, 0 (UART0)."))],
                Devices = stimulus.Devices.Count == 0 ? [] : throw new NotSupportedException("chipsim has no ESP32-C5 buses yet."),
                Analog = stimulus.Analog.Count == 0 ? [] : throw new NotSupportedException("chipsim has no ESP32-C5 ADC yet."),
            });
            return new ChipRun
            {
                Stop = result.ExitCode == 3 ? ChipStop.Failed
                    : result.Stopped.StartsWith("stopped: the program has finished", StringComparison.Ordinal) ? ChipStop.Finished
                    : ChipStop.TimeUp,
                Pins = [.. result.Pins.Where(p => p.Pin.StartsWith("GPIO", StringComparison.Ordinal))
                    .Select(p => new ChipPinChange(p.Milliseconds, int.Parse(p.Pin[4..], CultureInfo.InvariantCulture), p.State))],
                Serial = new Dictionary<int, byte[]> { [0] = result.SerialOf("UART0") },
                BaudRates = new Dictionary<int, IReadOnlyList<int>>
                {
                    [0] = [.. result.After("UART0 baud ").Select(line => int.Parse(line["UART0 baud ".Length..],
                        CultureInfo.InvariantCulture))],
                },
                Bus = [],
                Pwm = [],
                Output = result.ToString(),
            };
        });
    }

    private static string Name(int pin) => "GPIO" + pin.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\bChipSharp_Runtime_PinOutput\s*\([^;{]*\)\s*\{")]
    private static partial Regex PinOutputDefinition();
}
