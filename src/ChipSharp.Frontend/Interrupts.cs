using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// The interrupt a target has for one event, from the "interrupts" table of its target file, e.g.
/// <c>"timer": { "irq": 1, "clear": "chipsharp_clear_timer_event" }</c>.
/// </summary>
/// <param name="Event">The event, as <c>[Interrupt(...)]</c> names it, e.g. "timer".</param>
/// <param name="Number">The interrupt's number (IRQ): which entry of the chip's vector table runs the handler.</param>
/// <param name="Clear">
/// The runtime function that runs first, before the program's handler: it clears the event, so that the interrupt
/// does not come again at once (and arms a repeating timer's next time). Null when the handler clears it itself.
/// </param>
public sealed record TargetInterrupt(string Event, int Number, string? Clear)
{
    /// <summary>
    /// The symbol of the handler of interrupt <paramref name="number"/>, which the runtime's vector table names, e.g.
    /// <c>chipsharp_isr_irq1</c>: the runtime gives each a weak default, and a program's handler takes its place.
    /// </summary>
    public static string HandlerSymbol(int number) => $"chipsharp_isr_irq{number}";

    /// <summary>The symbol of this interrupt's handler, see <see cref="HandlerSymbol"/>.</summary>
    public string Symbol => HandlerSymbol(Number);
}

/// <summary>
/// A method of the program marked <c>[Interrupt(...)]</c> (ChipSharp.Hardware.InterruptAttribute), which the chip runs
/// when its event happens.
/// </summary>
/// <param name="Method">The method: static, void, without parameters.</param>
/// <param name="Event">The event it handles, e.g. "timer".</param>
/// <param name="Interrupt">The target's interrupt for the event; null when the program was checked without a target.</param>
public sealed record InterruptHandler(IlMethod Method, string Event, TargetInterrupt? Interrupt);

/// <summary>
/// Interrupt handlers: the methods a program marks <c>[Interrupt(Event.Timer)]</c> and the like, which nothing in the
/// program calls but the chip, when the event happens; the library calls that start each event; and the events a
/// target has interrupts for.
/// </summary>
/// <remarks>
/// <para>
/// The whole-program pass (see <c>WholeProgramPass</c>) adds the handlers to the program and checks its rules:
/// static, void, no parameters, an event the target has, one handler per event, a handler for each event the program
/// starts, no waiting in a handler, and <c>volatile</c> on every static field a handler shares with the rest of the
/// program.
/// </para>
/// <para>
/// The IL-to-TIR translation then gives each handler an entry function exported under the symbol of the target's
/// interrupt for its event (<see cref="TargetInterrupt.Symbol"/>), which the vector table names: it calls the runtime's
/// <see cref="TargetInterrupt.Clear"/> function, then the handler. The handler is an ordinary function (C's calling
/// convention): a Cortex-M saves the registers a function may change itself when the interrupt comes, and aligns the
/// stack, so nothing more is needed around it.
/// </para>
/// </remarks>
public static class Interrupts
{
    /// <summary>The attribute that marks a handler.</summary>
    public const string AttributeType = "ChipSharp.Hardware.InterruptAttribute";

    /// <summary>The events of ChipSharp.Hardware.Event, which the program is checked against when no target is given.</summary>
    public static IReadOnlyList<string> LibraryEvents { get; } = ["timer", "pin", "uart"];

    /// <summary>The runtime functions (of <see cref="KnownExternals"/>) that start an event, and the event they start.</summary>
    internal static IReadOnlyDictionary<string, string> Starters { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ChipSharp.Runtime.TimerEveryMilliseconds"] = "timer",
        ["ChipSharp.Runtime.TimerEveryMicroseconds"] = "timer",
        ["ChipSharp.Runtime.TimerAfterMilliseconds"] = "timer",
        ["ChipSharp.Runtime.TimerAfterMicroseconds"] = "timer",
        ["ChipSharp.Runtime.PinWatch"] = "pin",
        ["ChipSharp.Runtime.PinWatchRising"] = "pin",
        ["ChipSharp.Runtime.PinWatchFalling"] = "pin",
        ["ChipSharp.Runtime.UartWatch"] = "uart", // Uart.Watch, a library substitute's call
    };

    /// <summary>The runtime functions that wait, which a handler cannot call: the interrupt that ends the wait cannot come while it runs.</summary>
    internal static IReadOnlySet<string> Waits { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "ChipSharp.Runtime.DelayMilliseconds", "ChipSharp.Runtime.DelayMicroseconds",
        "ChipSharp.Runtime.ConsoleRead", // Console.Read and ReadLine wait for the input's interrupt
    };

    /// <summary>
    /// Every method of <paramref name="assembly"/> marked <c>[Interrupt(...)]</c>, in metadata order, with the event it
    /// names; the event is null when the attribute's argument is not a string (e.g. <c>null</c>).
    /// </summary>
    public static IReadOnlyList<(IlMethod Method, string? Event)> FindMarked(IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var marked = new List<(IlMethod, string?)>();
        foreach (var method in assembly.Methods)
        {
            foreach (var attribute in assembly.FindAttributes(method, AttributeType))
            {
                marked.Add((method, attribute.Arguments is [string name] ? name : null));
            }
        }

        return marked;
    }

    /// <summary>
    /// The events <paramref name="target"/> has interrupts for, by name, from its "interrupts" table. Empty when it has
    /// none, as a PC has none.
    /// </summary>
    /// <exception cref="ChipSharpException">The table is not written as <c>"event": { "irq": number, "clear": "symbol" }</c>.</exception>
    public static IReadOnlyDictionary<string, TargetInterrupt> ForTarget(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);

        ChipSharpException Error(string message) => new($"Target '{target.Name}': {message}");

        var table = new Dictionary<string, TargetInterrupt>(StringComparer.Ordinal);
        switch (target.Settings["interrupts"])
        {
            case null:
                return table;

            case JsonObject events:
                foreach (var (name, value) in events)
                {
                    if (value is not JsonObject entry || entry["irq"] is not JsonValue irq
                        || !irq.TryGetValue(out int number) || number < 0)
                    {
                        throw Error($"'interrupts.{name}' must give the event's interrupt number, e.g. {{ \"irq\": 1 }}.");
                    }

                    string? clear = null;
                    if (entry["clear"] is { } clearNode
                        && (clearNode is not JsonValue text || !text.TryGetValue(out clear) || !IsSymbol(clear)))
                    {
                        throw Error($"'interrupts.{name}.clear' must be the name of a C function of the runtime, " +
                            "e.g. \"chipsharp_clear_timer_event\".");
                    }

                    if (table.Values.FirstOrDefault(other => other.Number == number) is { } same)
                    {
                        throw Error($"'interrupts.{same.Event}' and 'interrupts.{name}' both have interrupt {number}; " +
                            "each event needs an interrupt of its own.");
                    }

                    table.Add(name, new TargetInterrupt(name, number, clear));
                }

                return table;

            default:
                throw Error("'interrupts' must be an object of events, e.g. { \"timer\": { \"irq\": 1 } }.");
        }
    }

    private static bool IsSymbol(string? name) =>
        !string.IsNullOrEmpty(name) && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
