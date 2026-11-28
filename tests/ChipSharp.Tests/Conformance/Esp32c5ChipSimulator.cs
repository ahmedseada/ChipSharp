using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Simulators;
using ChipSharp.Simulators.Devices;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The ESP32-C5-DevKitC-1 (esp32c5-devkitc, milestone M4) in ChipSharp's own simulator (chipsim's "esp32c5", in
/// process), which loads the .elf (or the ESP image) the build makes and runs it as the ROM leaves the chip. Pins are
/// GPIO numbers: the program's pins are those the board leaves free (not the strapping pins 2, 3, 7 and 25-28, the
/// module's flash and PSRAM on 15-22, USB on 13 and 14, the RGB LED on 27); UART0 (GPIO 11 TX, 12 RX) is serial port 0
/// and the USB Serial/JTAG port, the console, is port 1 here (<see cref="ConsolePort"/>). Its cases are skipped, saying
/// why, while the chip's runtime does not implement the hardware contract (it does: pins, the clock and the delays, the
/// console on USB, UART0, the events, I2C, SPI, the ADC and PWM). The buses are I2C0 (SDA GPIO4, SCL GPIO5) and SPI0
/// (SCK GPIO0, MOSI GPIO1, MISO GPIO24), the analog input GPIO5 (ADC1's channel 4, 12 bits against 3.3 V) and the PWM
/// output GPIO4 (a LEDC channel); chipsim puts the shared devices on the buses and the voltages on the input.
/// </summary>
internal sealed partial class Esp32c5ChipSimulator() : ChipSimulator("esp32c5-devkitc")
{
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
                "console on USB, the events: milestone M4, wave 1): 'esp32c5-devkitc' runs the conformance suite in " +
                "chipsim's esp32c5 once it does.";
    }

    /// <summary>The console is the USB Serial/JTAG port: serial port 1 of this adapter (0 is UART0).</summary>
    public override int ConsolePort => 1;

    public override ChipPins Pins { get; } = new(Output: 6, Second: 8, Pass: 9, Fail: 10, Input: 23)
    {
        Uart0 = (11, 12),
        I2c0 = (4, 5),
        Spi0 = (0, 1, 24),
        Adc = 5,
        Pwm = 4,
    };

    // ADC1 in 12 bits, 12 dB attenuation: 4096 counts for 3.3 V
    public override ChipAdc Adc { get; } = new(4096, 3.3);

    // up to 240 MHz, a peripheral access a few cycles more; the delays and the clock on SYSTIMER's 16 MHz count
    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.020, LatencyMilliseconds: 0.010,
        ClockMilliseconds: 0.010)
    {
        PwmMicroseconds = 2,
    };

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
                Sends = [.. stimulus.Sends.Select(send => send.Port switch
                {
                    0 => new SerialSend(send.Bytes, send.AtMilliseconds, "UART0"),
                    1 => new SerialSend(send.Bytes, send.AtMilliseconds, "USB"),
                    _ => throw new NotSupportedException("the ESP32-C5's adapter has two serial ports, 0 (UART0) and 1 (USB)."),
                })],
                Devices = [.. stimulus.Devices.Select(device => device.Bus == "i2c"
                    ? new BusDevice("i2c", device.Number, device.Device, device.Address)
                    : new BusDevice("spi", device.Number, device.Device, Pin: device.Pin is { } pin ? Name(pin) : null))],
                Analog = [.. stimulus.Analog.Select(input => new AnalogInput(Name(input.Pin), input.Volts, null,
                    input.AtMilliseconds))],
            });
            return new ChipRun
            {
                Stop = result.ExitCode == 3 ? ChipStop.Failed
                    : result.Stopped.StartsWith("stopped: the program has finished", StringComparison.Ordinal) ? ChipStop.Finished
                    : ChipStop.TimeUp,
                Pins = [.. result.Pins.Where(p => p.Pin.StartsWith("GPIO", StringComparison.Ordinal))
                    .Select(p => new ChipPinChange(p.Milliseconds, int.Parse(p.Pin[4..], CultureInfo.InvariantCulture), p.State))],
                Serial = new Dictionary<int, byte[]> { [0] = result.SerialOf("UART0"), [1] = result.SerialOf("USB") },
                BaudRates = new Dictionary<int, IReadOnlyList<int>>
                {
                    [0] = [.. result.After("UART0 baud ").Select(line => int.Parse(line["UART0 baud ".Length..],
                        CultureInfo.InvariantCulture))],
                },
                Bus = [.. result.Lines.Select(line => BusLine().Match(line)).Where(m => m.Success).Select(m => m.Groups[1].Value)],
                Pwm = [.. result.Lines.Select(line => PwmLine().Match(line)).Where(m => m.Success)
                    .Select(m => new ChipPwmPeriod(double.Parse(m.Groups["ms"].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups["pin"].Value, CultureInfo.InvariantCulture),
                        m.Groups["hz"].Success ? double.Parse(m.Groups["hz"].Value, CultureInfo.InvariantCulture) : 0,
                        m.Groups["high"].Success ? double.Parse(m.Groups["high"].Value, CultureInfo.InvariantCulture) : 0))],
                Output = result.ToString(),
            };
        });
    }

    private static string Name(int pin) => "GPIO" + pin.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\[\s*[0-9.]+ ms\] ((?:I2C|SPI)\d .+)$")]
    private static partial Regex BusLine();

    [GeneratedRegex(@"^\[\s*(?<ms>[0-9.]+) ms\] GPIO(?<pin>\d+) PWM (?:(?<hz>[0-9.]+) Hz, duty [0-9.]+% \((?<high>[0-9.]+) us high\)|steady \w+)$")]
    private static partial Regex PwmLine();

    [GeneratedRegex(@"\bChipSharp_Runtime_PinOutput\s*\([^;{]*\)\s*\{")]
    private static partial Regex PinOutputDefinition();
}
