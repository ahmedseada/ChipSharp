using System.Globalization;

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
    /// on the target 'pico'", with the pins that can.
    /// </summary>
    public static Unsupported WrongPin(int pin, string role, IReadOnlyList<int> pins, string target) => Rule(
        $"GPIO {pin} cannot be {role} on the target '{target}'",
        pins.Count == 0 ? "It has none." : $"{char.ToUpperInvariant(role[0])}{role[1..]} can be GPIO {Pins(pins)}.");

    /// <summary>"4, 8, 20 or 24"; a run of three or more pins as "0 to 29".</summary>
    private static string Pins(IReadOnlyList<int> pins)
    {
        var parts = new List<string>();
        for (var i = 0; i < pins.Count;)
        {
            var end = i;
            while (end + 1 < pins.Count && pins[end + 1] == pins[end] + 1)
            {
                end++;
            }

            parts.AddRange(end - i >= 2
                ? [$"{pins[i]} to {pins[end]}"]
                : pins.Skip(i).Take(end - i + 1).Select(pin => pin.ToString(CultureInfo.InvariantCulture)));
            i = end + 1;
        }

        return parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.Take(parts.Count - 1))} or {parts[^1]}";
    }
}
