using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Tests.Avr;

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The Arduino Uno in avr8js (tools/avr-sim/run.mjs, through <see cref="AvrSimulator"/>): the simulator names pins as the
/// Uno's headers do (D0-D13, A0-A5), the target's "pinNames" give their numbers; USART0 is port 0 and the console; the
/// buses' devices are the Pico simulator's, on bus 0.
/// </summary>
internal sealed partial class UnoChipSimulator() : ChipSimulator("uno")
{
    private List<string>? _names;

    public override string? Missing => AvrTools.MissingSimulator.Value;

    // D0 and D1: USART0; D2: INT0; D9: Timer1's PWM; D11-D13: SPI; A0 (14): an analog input; A4 and A5: I2C
    public override ChipPins Pins { get; } = new(Output: 4, Second: 8, Pass: 6, Fail: 7, Input: 2)
    {
        Adc = 14,
        Pwm = 9,
        Uart0 = (1, 0),
        I2c0 = (18, 19),
        Spi0 = (13, 11, 12),
    };

    // the delays poll Timer1 (two counts a microsecond) at 16 MHz; an event's entry saves the registers, then the
    // runtime finds out which event came (avr8js ends a fast PWM pulse a count early, so the high time is a count off)
    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.030, LatencyMilliseconds: 0.060,
        ClockMilliseconds: 0.030)
    {
        PwmMicroseconds = 1,
    };

    public override ChipAdc Adc { get; } = new(1024, 5.0);

    /// <summary>The Uno's pin names by number, as the simulator prints them.</summary>
    private List<string> Names =>
        _names ??= [.. Description.Settings["pinNames"]!.AsArray().Select(name => name!.GetValue<string>())];

    public override async Task<ChipRun> RunAsync(string image, ChipStimulus stimulus)
    {
        var run = await AvrSimulator.RunAsync(image, stimulus.Milliseconds, [.. Options(stimulus)]);
        var lines = run.Output.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        int? Number(string name) => Names.IndexOf(name) is >= 0 and var pin ? pin : null;
        return new ChipRun
        {
            Stop = run.ExitCode == 3 ? ChipStop.Failed
                : run.Stopped.Contains("the program has finished", StringComparison.Ordinal) ? ChipStop.Finished
                : ChipStop.TimeUp,
            Pins = [.. run.Pins.Where(p => Number(p.Pin) is not null)
                .Select(p => new ChipPinChange(p.Milliseconds, Number(p.Pin)!.Value, p.State))],
            Serial = new Dictionary<int, byte[]> { [0] = run.Usart0 },
            BaudRates = new Dictionary<int, IReadOnlyList<int>> { [0] = run.Usart0BaudRates },
            Bus = [.. lines.Select(line => BusLine().Match(line)).Where(m => m.Success).Select(m => m.Groups[1].Value)],
            Pwm = [.. lines.Select(line => PwmLine().Match(line)).Where(m => m.Success && Number(m.Groups["pin"].Value) is not null)
                .Select(m => new ChipPwmPeriod(Parse(m.Groups["ms"].Value), Number(m.Groups["pin"].Value)!.Value,
                    m.Groups["hz"].Success ? Parse(m.Groups["hz"].Value) : 0,
                    m.Groups["high"].Success ? Parse(m.Groups["high"].Value) : 0))],
            Output = run.ToString(),
        };
    }

    private IEnumerable<string> Options(ChipStimulus stimulus)
    {
        foreach (var input in stimulus.Inputs)
        {
            yield return "--input";
            yield return $"{Names[input.Pin]}={(input.High ? "high" : "low")}{At(input.AtMilliseconds)}";
        }

        foreach (var send in stimulus.Sends)
        {
            // USART0 is the only port: run.mjs's --send takes no port
            yield return "--send";
            yield return Escaped(send.Bytes) + At(send.AtMilliseconds);
        }

        foreach (var analog in stimulus.Analog)
        {
            yield return "--adc";
            yield return $"{Names[analog.Pin]}={analog.Volts.ToString("0.###", CultureInfo.InvariantCulture)}V{At(analog.AtMilliseconds)}";
        }

        foreach (var device in stimulus.Devices)
        {
            yield return device.Bus == "i2c" ? "--i2c" : "--spi";
            yield return device.Bus == "i2c"
                ? $"{device.Number}:0x{device.Address:x2}={device.Device}"
                : $"{device.Number}={device.Device}{(device.Pin is { } pin ? "@" + Names[pin] : "")}";
        }
    }

    private static double Parse(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\[\s*[0-9.]+ ms\] ((?:I2C|SPI)\d .+)$")]
    private static partial Regex BusLine();

    [GeneratedRegex(@"^\[\s*(?<ms>[0-9.]+) ms\] (?<pin>[DA]\d+) PWM (?:(?<hz>[0-9.]+) Hz, duty [0-9.]+% \((?<high>[0-9.]+) us high\)|steady \w+)$")]
    private static partial Regex PwmLine();
}
