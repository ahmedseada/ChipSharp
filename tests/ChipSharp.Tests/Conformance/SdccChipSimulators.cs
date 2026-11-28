using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Simulators;
using ChipSharp.Tests.Sdcc;

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// A chip built through C with SDCC (milestone M6), in ChipSharp's own simulators (src/ChipSharp.Simulators, chipsim),
/// in process: pins by the target's "pinNames" (PA0, PB5, ...), UART1 as port 0. Its cases run once the build pipeline has
/// its parts (the code generator "c", the chip's runtime, which implements the hardware contract), and are skipped,
/// saying which is missing, until then; chipsim has no devices on the buses and no analog inputs yet, so the cases that
/// need those are skipped too (<see cref="Unsimulated"/>).
/// </summary>
internal abstract partial class SdccChipSimulator(string target, string chip) : ChipSimulator(target)
{
    private List<string>? _names;

    /// <summary>The guards of the hardware whose outside chipsim does not simulate yet, with why.</summary>
    protected virtual IReadOnlyDictionary<string, string> Unsimulated { get; } = new Dictionary<string, string>();

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

    /// <summary>The conformance cases (their methods' names) whose programs the chip has no room for, with why.</summary>
    protected virtual IReadOnlyDictionary<string, string> TooBig { get; } = new Dictionary<string, string>();

    public override string? MissingFor(IReadOnlyList<string> needs) =>
        needs.Select(need => Unsimulated.GetValueOrDefault(need)).FirstOrDefault(reason => reason is not null) ?? Missing;

    public override string? MissingFor(string test, IReadOnlyList<string> needs) =>
        MissingFor(needs) ?? TooBig.GetValueOrDefault(test);

    /// <summary>The chip's pin names by number, as chipsim names them.</summary>
    private List<string> Names =>
        _names ??= [.. Description.Settings["pinNames"]!.AsArray().Select(name => name!.GetValue<string>())];

    public override Task<ChipRun> RunAsync(string image, ChipStimulus stimulus)
    {
        if (stimulus.Analog.Count > 0 || stimulus.Devices.Count > 0)
        {
            throw new NotSupportedException($"chipsim's {chip} has no analog inputs or devices on its buses yet.");
        }

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
                Bus = [],
                Pwm = [.. result.Lines.Select(line => PwmLine().Match(line)).Where(m => m.Success && Number(m.Groups["pin"].Value) is not null)
                    .Select(m => new ChipPwmPeriod(Parse(m.Groups["ms"].Value), Number(m.Groups["pin"].Value)!.Value,
                        m.Groups["hz"].Success ? Parse(m.Groups["hz"].Value) : 0,
                        m.Groups["high"].Success ? Parse(m.Groups["high"].Value) : 0))],
                Output = result.ToString(),
            };
        });
    }

    private static double Parse(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\[\s*(?<ms>[0-9.]+) ms\] (?<pin>P[A-F][0-7]) PWM (?:(?<hz>[0-9.]+) Hz, duty [0-9.]+% \((?<high>[0-9.]+) us high\)|steady \w+)$")]
    private static partial Regex PwmLine();
}

/// <summary>
/// The Padauk PFS154 at 2 MHz (pfs154-bare), in chipsim's "pfs154": pins are port * 8 + bit (PA0-PA7 0-7, PB0-PB7
/// 8-15); PA0 is one of its two pins with an interrupt, PB5 TM3's PWM output. No serial port, ADC or bus. Two cases'
/// programs do not fit its 2K words and 128 bytes (<see cref="TooBig"/>): what their C# needs of TimeSpan and DateTime,
/// as the c code generator writes it for SDCC, whose Padauk ports keep every function's values at addresses of their
/// own; the runtime's part of them (the delays, the clock) is tested on its own (Pfs154RuntimeTests).
/// </summary>
internal sealed class Pfs154ChipSimulator() : SdccChipSimulator("pfs154-bare", "pfs154")
{
    private const string NoRoom = "{0} does not fit the PFS154 (2K words of program memory, 128 bytes of RAM) as the c " +
        "code generator writes it for SDCC, whose Padauk ports keep the values of every function at addresses of their " +
        "own: the case runs there once its C does.";

    protected override IReadOnlyDictionary<string, string> TooBig { get; } = new Dictionary<string, string>
    {
        [nameof(TimingConformanceTests.The_delays_wait_at_least_the_time_given_and_little_more)] = string.Format(
            CultureInfo.InvariantCulture, NoRoom, "Timing.Delay(TimeSpan.FromMilliseconds(2)) (TimeSpan's 64-bit " +
            "arithmetic: about 360 bytes of RAM)"),
        [nameof(TimingConformanceTests.The_clock_counts_the_time_for_Stopwatch_TickCount64_and_DateTime_UtcNow)] =
            string.Format(CultureInfo.InvariantCulture, NoRoom, "The case's Stopwatch.GetElapsedTime, TimeSpan and " +
                "DateTime (about 6000 words of program memory and 1.2 KB of RAM)"),
    };

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
/// PD6 30); UART1 (PD5, PD6) is port 0, I2C on PB5 and PB4, SPI on PC5-PC7, AIN3 on PD2, TIM2's channel 1 on PD4.
/// </summary>
internal sealed class Stm8ChipSimulator() : SdccChipSimulator("stm8s103-board", "stm8s103")
{
    private const string Registers = "chipsim's STM8S103 has its {0}'s registers but nothing outside it yet ({1}): the " +
        "case runs once it has.";

    public override ChipPins Pins { get; } = new(Output: 1, Second: 2, Pass: 3, Fail: 19, Input: 20)
    {
        Adc = 26,
        Pwm = 28,
        Uart0 = (29, 30),
        I2c0 = (13, 12),
        Spi0 = (21, 22, 23),
    };

    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.060, LatencyMilliseconds: 0.040,
        ClockMilliseconds: 0.040)
    {
        PwmMicroseconds = 1,
    };

    // AIN0-AIN6 in 10 bits against VDD, the board's 3.3 V
    public override ChipAdc Adc { get; } = new(1024, 3.3);

    protected override IReadOnlyDictionary<string, string> Unsimulated { get; } = new Dictionary<string, string>
    {
        ["i2c"] = string.Format(CultureInfo.InvariantCulture, Registers, "I2C controller", "no device answers on the bus"),
        ["spi"] = string.Format(CultureInfo.InvariantCulture, Registers, "SPI controller", "no device on the bus"),
        ["adc"] = string.Format(CultureInfo.InvariantCulture, Registers, "ADC", "no voltage on its inputs"),
    };
}
