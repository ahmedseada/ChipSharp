using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Simulators;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// WCH's CH32V003 evaluation board (ch32v003-evt, milestone M5) in ChipSharp's own simulator (chipsim's "ch32v003", in
/// process), which loads the image the build makes (the image format bin, the flash from 0x0800_0000; Intel HEX or ELF
/// too) and runs it from 0 as reset leaves the chip. Pins are ChipSharp's numbers, 8 * the port's letter + the bit (PC0
/// 16, PD0 24; the target's "pinNames" when it has them): the program's are among those the board leaves free (PC0-PC7,
/// PD0, PD2-PD6: PD1 is SWIO, PD7 NRST, PA1 and PA2 the crystal's), PD0 the output WCH's examples blink; USART1 (PD5 TX,
/// PD6 RX) is serial port 0 and the console. Its cases are skipped, saying why, until the chip's runtime implements the
/// hardware contract (wave 1: pins, the delays and the clock, the console on USART1, the events).
/// </summary>
internal sealed partial class Ch32v003ChipSimulator() : ChipSimulator("ch32v003-evt")
{
    private static readonly string[] Ports = ["A", "B", "C", "D"];

    private string? _missing;
    private bool _checked;
    private List<string>? _names;

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
        if (!TargetExists)
        {
            return $"The target '{Target}' is not in ChipSharp yet (milestone M5, wave 0): it runs the conformance suite in " +
                "chipsim's ch32v003 once it, and its runtime, are.";
        }

        if (PicoTools.MissingLlvm.Value is { } llvm)
        {
            return llvm;
        }

        var registry = DefaultComponents.CreateRegistry(new BuildContext(new BuildOptions(), Description, "Program",
            Path.GetTempPath()));
        if (Description.Runtime is not { } name || !registry.TryGet<IArchRuntime>(name, out var runtime))
        {
            return $"The runtime \"{Description.Runtime}\" is not in ChipSharp yet: '{Target}' runs the conformance suite " +
                "in chipsim's ch32v003 once it implements the hardware contract (milestone M5, wave 1).";
        }

        // the hardware contract's first function, Pin.Output, defined in a C file: a runtime that has it implements the
        // contract
        return runtime.Startup(Description).Files.Any(file => !file.FileName.EndsWith(".h", StringComparison.Ordinal) &&
            PinOutputDefinition().IsMatch(file.Content))
            ? null
            : "The CH32V003's runtime does not implement the hardware contract yet (pins, the clock and the delays, the " +
                "console on USART1, the events: milestone M5, wave 1): 'ch32v003-evt' runs the conformance suite in " +
                "chipsim's ch32v003 once it does.";
    }

    // PD0, PC0, PC3, PD2 and PD3 (port C is 2, D 3); USART1 on PD5 and PD6
    public override ChipPins Pins { get; } = new(Output: 24, Second: 16, Pass: 19, Fail: 26, Input: 27)
    {
        Uart0 = (29, 30),
    };

    // 48 MHz from the PLL, the delays and the clock on SysTick; a handler's entry a few cycles, the HPE's pushes besides
    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.050, LatencyMilliseconds: 0.020,
        ClockMilliseconds: 0.020);

    public override Task<ChipRun> RunAsync(string image, ChipStimulus stimulus)
    {
        return Task.Run(() =>
        {
            var result = Simulation.Run(new SimulationOptions
            {
                Chip = "ch32v003",
                Image = image,
                Milliseconds = stimulus.Milliseconds,
                Inputs = [.. stimulus.Inputs.Select(input => new PinInput(Name(input.Pin), input.High, input.AtMilliseconds))],
                Sends = [.. stimulus.Sends.Select(send => send.Port == 0
                    ? new SerialSend(send.Bytes, send.AtMilliseconds, "USART1")
                    : throw new NotSupportedException("the CH32V003 has one serial port, 0 (USART1)."))],
                Devices = stimulus.Devices.Count == 0
                    ? []
                    : throw new NotSupportedException("chipsim's ch32v003 has no I2C or SPI devices yet (milestone M5, wave 2)."),
                Analog = stimulus.Analog.Count == 0
                    ? []
                    : throw new NotSupportedException("chipsim's ch32v003 has no analog inputs yet (milestone M5, wave 2)."),
            });
            return new ChipRun
            {
                Stop = result.ExitCode == 3 ? ChipStop.Failed
                    : result.Stopped.StartsWith("stopped: the program has finished", StringComparison.Ordinal) ? ChipStop.Finished
                    : ChipStop.TimeUp,
                Pins = [.. result.Pins.Select(p => new ChipPinChange(p.Milliseconds, Number(p.Pin), p.State))],
                Serial = new Dictionary<int, byte[]> { [0] = result.SerialOf("USART1") },
                BaudRates = new Dictionary<int, IReadOnlyList<int>>
                {
                    [0] = [.. result.After("USART1 baud ").Select(line => int.Parse(line["USART1 baud ".Length..],
                        CultureInfo.InvariantCulture))],
                },
                Bus = [],
                Pwm = [],
                Output = result.ToString(),
            };
        });
    }

    /// <summary>The chip's pin names by number: the target's, or port × 8 + bit.</summary>
    private List<string> Names => _names ??= TargetExists && Description.Settings["pinNames"] is { } names
        ? [.. names.AsArray().Select(name => name!.GetValue<string>())]
        : [.. Enumerable.Range(0, 32).Select(n => $"P{Ports[n / 8]}{(n % 8).ToString(CultureInfo.InvariantCulture)}")];

    private string Name(int pin) => Names[pin];

    private int Number(string pin) => Names.IndexOf(pin) is var n and >= 0
        ? n
        : 8 * Array.IndexOf(Ports, pin[1..2]) + (pin[2] - '0');

    [GeneratedRegex(@"\bChipSharp_Runtime_PinOutput\s*\([^;{]*\)\s*\{")]
    private static partial Regex PinOutputDefinition();
}
