using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Simulators;
using ChipSharp.Simulators.Devices;
using ChipSharp.Tests.Sdcc;

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// A chip built through C with SDCC (milestone M6), in ChipSharp's own simulators (src/ChipSharp.Simulators, chipsim),
/// in process: pins by the target's "pinNames" (PA0, PB5, ...), UART1 as port 0. Its cases run once the build pipeline has
/// its parts (the code generator "c", the chip's runtime, which implements the hardware contract), and are skipped,
/// saying which is missing, until then. chipsim puts the simulators' shared devices on a chip's buses (--i2c, --spi) and
/// voltages on its analog inputs (--adc).
/// </summary>
internal abstract partial class SdccChipSimulator(string target, string chip) : ChipSimulator(target)
{
    private List<string>? _names;

    public override string? Missing
    {
        get
        {
            if (SdccInstall.Missing.Value is { } sdcc)
            {
                return sdcc;
            }

            var registry = DefaultComponents.CreateRegistry(new BuildContext(new BuildOptions(), Description, "Program",
                Path.GetTempPath()));
            if (!registry.TryGet<ICodeGenerator>(Description.Backend, out _))
            {
                return $"The code generator \"{Description.Backend}\" (C, for SDCC) is not in ChipSharp yet: " +
                    $"'{Target}' runs the conformance suite once it is.";
            }

            return registry.TryGet<IArchRuntime>(Description.Runtime!, out _)
                ? null
                : $"The runtime \"{Description.Runtime}\" is not in ChipSharp yet: '{Target}' runs the conformance suite " +
                    "once it implements the hardware contract.";
        }
    }

    /// <summary>The chip's pin names by number, as chipsim names them.</summary>
    private List<string> Names =>
        _names ??= [.. Description.Settings["pinNames"]!.AsArray().Select(name => name!.GetValue<string>())];

    public override Task<ChipRun> RunAsync(string image, ChipStimulus stimulus)
    {
        return Task.Run(() =>
        {
            var result = Simulation.Run(new SimulationOptions
            {
                Chip = chip,
                Image = image,
                Milliseconds = stimulus.Milliseconds,
                Inputs = [.. stimulus.Inputs.Select(input => new PinInput(Names[input.Pin], input.High, input.AtMilliseconds))],
                Sends = [.. stimulus.Sends.Select(send => send.Port == 0
                    ? new SerialSend(send.Bytes, send.AtMilliseconds)
                    : throw new NotSupportedException($"the {chip} has one serial port, 0 (UART1)."))],
                Devices = [.. stimulus.Devices.Select(device => device.Bus == "i2c"
                    ? new BusDevice("i2c", device.Number, device.Device, device.Address)
                    : new BusDevice("spi", device.Number, device.Device, Pin: device.Pin is { } pin ? Names[pin] : null))],
                Analog = [.. stimulus.Analog.Select(input => new AnalogInput(Names[input.Pin], input.Volts, null,
                    input.AtMilliseconds))],
            });
            int? Number(string name) => Names.IndexOf(name) is >= 0 and var pin ? pin : null;
            return new ChipRun
            {
                Stop = result.ExitCode == 3 ? ChipStop.Failed
                    : result.Stopped.StartsWith("stopped: the program has finished", StringComparison.Ordinal) ? ChipStop.Finished
                    : ChipStop.TimeUp,
                Pins = [.. result.Pins.Where(p => Number(p.Pin) is not null)
                    .Select(p => new ChipPinChange(p.Milliseconds, Number(p.Pin)!.Value, p.State))],
                Serial = new Dictionary<int, byte[]> { [0] = result.Serial },
                BaudRates = new Dictionary<int, IReadOnlyList<int>>
                {
                    [0] = [.. result.After("UART1 baud ").Select(line => int.Parse(line["UART1 baud ".Length..],
                        CultureInfo.InvariantCulture))],
                },
                Bus = [.. result.Lines.Select(line => BusLine().Match(line)).Where(m => m.Success)
                    .Select(m => m.Groups[1].Value)],
                Pwm = [.. result.Lines.Select(line => PwmLine().Match(line)).Where(m => m.Success && Number(m.Groups["pin"].Value) is not null)
                    .Select(m => new ChipPwmPeriod(Parse(m.Groups["ms"].Value), Number(m.Groups["pin"].Value)!.Value,
                        m.Groups["hz"].Success ? Parse(m.Groups["hz"].Value) : 0,
                        m.Groups["high"].Success ? Parse(m.Groups["high"].Value) : 0))],
                Output = result.ToString(),
            };
        });
    }

    private static double Parse(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\[\s*[0-9.]+ ms\] ((?:I2C|SPI)\d .+)$")]
    private static partial Regex BusLine();

    [GeneratedRegex(@"^\[\s*(?<ms>[0-9.]+) ms\] (?<pin>P[A-F][0-7]) PWM (?:(?<hz>[0-9.]+) Hz, duty [0-9.]+% \((?<high>[0-9.]+) us high\)|steady \w+)$")]
    private static partial Regex PwmLine();
}

/// <summary>
/// The Padauk PFS154 at 2 MHz (pfs154-bare), in chipsim's "pfs154": pins are port * 8 + bit (PA0-PA7 0-7, PB0-PB7
/// 8-15); PA0 is one of its two pins with an interrupt, PB5 TM3's PWM output. No serial port, ADC or bus.
/// </summary>
internal sealed class Pfs154ChipSimulator() : SdccChipSimulator("pfs154-bare", "pfs154")
{
    public override ChipPins Pins { get; } = new(Output: 4, Second: 3, Pass: 9, Fail: 10, Input: 0) { Pwm = 13 };

    // 2 MHz, an instruction a cycle (mostly), and 32-bit arithmetic in software: a delay's loop and an event's entry
    // take tens of microseconds
    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.300, LatencyMilliseconds: 0.150,
        ClockMilliseconds: 0.200)
    {
        PwmMicroseconds = 8,
    };
}

/// <summary>
/// The STM8S103F3 at 16 MHz (stm8s103-board), in chipsim's "stm8s103": pins are port * 8 + bit (PA1 1, PB5 13, PC3 19,
/// PD6 30); UART1 (PD5, PD6) is port 0, I2C on PB5 and PB4, SPI on PC5-PC7 (their devices chipsim's), AIN3 on PD2 (its
/// voltages chipsim's), TIM2's channel 1 on PD4.
/// </summary>
internal sealed class Stm8ChipSimulator() : SdccChipSimulator("stm8s103-board", "stm8s103")
{
    public override ChipPins Pins { get; } = new(Output: 1, Second: 2, Pass: 3, Fail: 19, Input: 20)
    {
        Adc = 26,
        Pwm = 28,
        Uart0 = (29, 30),
        I2c0 = (13, 12),
        Spi0 = (21, 22, 23),
    };

    // 16 MHz, and 32-bit arithmetic in SDCC's helpers (TimeSpan's 64-bit divisions in them take a few hundred
    // microseconds): the delays and the clock watch TIM1's microseconds, an event's entry is the runtime's handler
    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.150, LatencyMilliseconds: 0.040,
        ClockMilliseconds: 0.040)
    {
        PwmMicroseconds = 1,
    };

    // AIN2-AIN6 in 10 bits against VDD, the board's 3.3 V
    public override ChipAdc Adc { get; } = new(1024, 3.3);
}
