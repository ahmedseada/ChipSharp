using ChipSharp.Abstractions;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// What a target does not have of serial ports, analog inputs and PWM outputs, or a pin or port it does not have for
/// one (see <see cref="SerialAnalogRules"/>). Like the target's other rules, the heading says what the program needs
/// and that the target does not have it, and the hint holds for good.
/// </summary>
internal sealed partial record Unsupported
{
    private const string ForAChip = "Serial ports, analog inputs and PWM outputs are a chip's: build the program for " +
        "one that has them, e.g. with -t pico.";

    /// <summary>
    /// A class of ChipSharp.Hardware the target has nothing for: "ChipSharp.Hardware.Adc needs analog inputs (an ADC),
    /// and the target 'host' has none".
    /// </summary>
    public static Unsupported NoHardware(string type, string needs, string target) =>
        Rule($"{type} needs {needs}, and the target '{target}' has none", ForAChip);

    /// <summary>Console.Read or Console.ReadLine on a target whose console does not read.</summary>
    public static Unsupported NoConsoleInput(string target) => Rule(
        $"Console.Read and Console.ReadLine need a console that reads, and the target '{target}' has none",
        "On a chip the console reads a serial port (on the Pico, UART0's RX: GPIO 1, at 115200 baud): build the " +
        "program for one, e.g. with -t pico.");

    /// <summary>Console.ReadLine, which returns a new string, on a target without a heap.</summary>
    public static Unsupported ReadLineWithoutHeap(string target) => Rule(
        $"Console.ReadLine makes a string, which needs a heap, and the target '{target}' has no heap",
        "Read the line char by char with Console.Read, into a span of a constant size (stackalloc char[64]).");

    /// <summary>A UART the target does not have, given as a constant: "the target 'pico' has no UART2".</summary>
    public static Unsupported NoSuchPort(int port, int ports, string target) => Rule(
        $"the target '{target}' has no UART{port}",
        $"Its serial ports are {ProblemGroup.List([.. Enumerable.Range(0, ports).Select(n => $"UART{n}")])}.");

    /// <summary>
    /// A pin, given as a constant, that cannot be <paramref name="role"/> on the target: "GPIO 6 cannot be UART1's TX
    /// on the target 'pico'" ("pin 4 (D4) cannot be UART0's TX on the target 'uno'": see <see cref="PinNames"/>), with
    /// the pins that can.
    /// </summary>
    public static Unsupported WrongPin(TargetDescription target, int pin, string role, IReadOnlyList<int> pins) => Rule(
        $"{PinNames.Of(target, pin)} cannot be {role} on the target '{target.Name}'",
        pins.Count == 0
            ? "It has none."
            : $"{char.ToUpperInvariant(role[0])}{role[1..]} can be {PinNames.List(target, pins, "or")}.");
}
