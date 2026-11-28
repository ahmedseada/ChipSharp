using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The Raspberry Pi Pico in rp2040js (tools/rp2040-sim/run.mjs, through <see cref="Rp2040Simulator"/>): GPIO numbers
/// are the target's pin numbers, UART0 is port 0 and the console, the buses' devices are run.mjs's --i2c and --spi.
/// </summary>
internal sealed partial class PicoChipSimulator() : ChipSimulator("pico")
{
    public override string? Missing => PicoTools.MissingSimulator.Value;

    // GPIO 0 and 1: UART0; 4 and 5: I2C0; 16, 18 and 19: SPI0; 26: ADC0
    public override ChipPins Pins { get; } = new(Output: 10, Second: 11, Pass: 12, Fail: 13, Input: 14)
    {
        Adc = 26,
        Pwm = 15,
        Uart0 = (0, 1),
        I2c0 = (4, 5),
        Spi0 = (18, 19, 16),
    };

    // the delays sleep on a timer alarm, a microsecond more than given; an event's entry is a few instructions at 125 MHz
    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.020, LatencyMilliseconds: 0.010,
        ClockMilliseconds: 0.010)
    {
        PwmMicroseconds = 0.1,
    };

    public override ChipAdc Adc { get; } = new(4096, 3.3);

    public override async Task<ChipRun> RunAsync(string image, ChipStimulus stimulus)
    {
        var run = await Rp2040Simulator.RunAsync(image, stimulus.Milliseconds, [.. Options(stimulus)]);
        var lines = run.Output.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        return new ChipRun
        {
            Stop = run.ExitCode == 3 ? ChipStop.Failed
                : run.Stopped.Contains("the program has finished", StringComparison.Ordinal) ? ChipStop.Finished
                : ChipStop.TimeUp,
            Pins = [.. run.Pins.Select(p => new ChipPinChange(p.Milliseconds, p.Pin, p.State))],
            Serial = new Dictionary<int, byte[]> { [0] = run.Uart0 },
            BaudRates = new Dictionary<int, IReadOnlyList<int>> { [0] = run.Uart0BaudRates },
            Bus = [.. lines.Select(line => BusLine().Match(line)).Where(m => m.Success).Select(m => m.Groups[1].Value)],
            Pwm = [.. lines.Select(line => PwmLine().Match(line)).Where(m => m.Success).Select(m => new ChipPwmPeriod(
                Number(m.Groups["ms"].Value), int.Parse(m.Groups["pin"].Value, CultureInfo.InvariantCulture),
                m.Groups["hz"].Success ? Number(m.Groups["hz"].Value) : 0,
                m.Groups["high"].Success ? Number(m.Groups["high"].Value) : 0))],
            Output = run.ToString(),
        };
    }

    private static IEnumerable<string> Options(ChipStimulus stimulus)
    {
        foreach (var input in stimulus.Inputs)
        {
            yield return "--input";
            yield return $"{input.Pin}={(input.High ? "high" : "low")}{At(input.AtMilliseconds)}";
        }

        foreach (var send in stimulus.Sends)
        {
            yield return "--send";
            yield return $"{send.Port}={Escaped(send.Bytes)}{At(send.AtMilliseconds)}";
        }

        foreach (var analog in stimulus.Analog)
        {
            yield return "--adc";
            yield return $"{analog.Pin}={analog.Volts.ToString("0.###", CultureInfo.InvariantCulture)}V{At(analog.AtMilliseconds)}";
        }

        foreach (var device in stimulus.Devices)
        {
            yield return device.Bus == "i2c" ? "--i2c" : "--spi";
            yield return device.Bus == "i2c"
                ? $"{device.Number}:0x{device.Address:x2}={device.Device}"
                : $"{device.Number}={device.Device}{(device.Pin is { } pin ? "@" + pin : "")}";
        }
    }

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\[\s*[0-9.]+ ms\] ((?:I2C|SPI)\d .+)$")]
    private static partial Regex BusLine();

    [GeneratedRegex(@"^\[\s*(?<ms>[0-9.]+) ms\] GPIO(?<pin>\d+) PWM (?:(?<hz>[0-9.]+) Hz, duty [0-9.]+% \((?<high>[0-9.]+) us high\)|steady \w+)$")]
    private static partial Regex PwmLine();
}
