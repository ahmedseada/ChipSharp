using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The rules of interrupt handlers (see <see cref="Interrupts"/>), in C# words. Unlike a feature, a rule never "arrives":
/// its heading is the rule itself.
/// </summary>
internal sealed partial record Unsupported
{
    private const string Attribute = "[Interrupt]";

    public static readonly Unsupported HandlerShape = Rule(
        $"an {Attribute} method must be static, return void and take no parameters",
        "The chip calls it with nothing to pass and nowhere to return a value to: share values through static " +
        "volatile fields.");

    public static readonly Unsupported WaitInHandler = Rule(
        "an interrupt handler cannot wait (Timing.DelayMilliseconds, Timing.DelayMicroseconds)",
        "The interrupt that ends the wait cannot come while a handler runs, so it would wait forever: keep handlers " +
        "short, and wait in Main, e.g. for a static volatile field the handler sets.");

    /// <summary>True for a rule of interrupt handlers: <see cref="Heading"/> is <see cref="What"/> itself.</summary>
    public bool IsRule { get; private init; }

    /// <summary>The handler names an event the target, or ChipSharp.Hardware when there is no target, does not have.</summary>
    /// <param name="name">The event as the attribute names it; null when its argument is not a string.</param>
    /// <param name="known">The events there are.</param>
    /// <param name="target">The target's name; null for ChipSharp.Hardware's events.</param>
    public static Unsupported UnknownEvent(string? name, IEnumerable<string> known, string? target)
    {
        var events = ProblemGroup.List([.. known.Select(EventText)]);
        return target is null
            ? Rule($"[Interrupt({Quoted(name)})] names an event ChipSharp.Hardware does not have",
                $"The events are {events}.")
            : Rule($"[Interrupt({Quoted(name)})] names an event target '{target}' does not have",
                $"Its events are {events}.");
    }

    /// <summary>The program has a handler, and the target has no interrupts at all (a PC).</summary>
    public static Unsupported NoInterrupts(string target) => Rule(
        $"{Attribute} methods need interrupts, and target '{target}' has none",
        "Interrupts are a chip's, not a PC's: build the program for a chip, e.g. with -t pico.");

    /// <summary>Two methods or more handle one event.</summary>
    public static Unsupported TwoHandlers(string name) => Rule(
        $"the {name} event has more than one {Attribute} method",
        "Keep one: it can call the others.");

    /// <summary>The program starts an event that no method handles.</summary>
    public static Unsupported Unhandled(string name) => Rule(
        $"the program starts the {name} event, which no [Interrupt({EventConstant(name)})] method handles",
        $"Add one, e.g. [Interrupt({EventConstant(name)})] static void On{char.ToUpperInvariant(name[0])}{name[1..]}() " +
        "{ ... }: without it the chip stops in the interrupt.");

    /// <summary>
    /// A static field that a handler and the rest of the program share, one of them writing it, without
    /// <c>volatile</c>. A long, ulong or double cannot be volatile in C#.
    /// </summary>
    public static Unsupported NotVolatile(IlField field)
    {
        var type = StaticFields.Type(field);
        var name = $"{CSharpNames.Type(field.DeclaringType.Type)}.{field.Name}";
        var what = $"the static field {name} is shared by an interrupt handler and the rest of the program, and is " +
            "not volatile";
        return type is IlPrimitiveType { Kind: IlPrimitiveKind.Int64 or IlPrimitiveKind.UInt64 or IlPrimitiveKind.Float64 }
            ? Rule(what, $"A {CSharpNames.Type(type)} cannot be volatile in C#, and the chip reads and writes it in " +
                "two halves, so one side could see half of the other's change: share an int or a uint instead.")
            : Rule(what, $"Declare it volatile (static volatile {CSharpNames.Type(type)} {field.Name};): otherwise the " +
                "program may keep a copy of it, and never see the other side's change.");
    }

    private static Unsupported Rule(string what, string hint) => new(what, false, hint) { IsRule = true };

    /// <summary>"Event.Timer" for the events ChipSharp.Hardware.Event names, else the name in quotes.</summary>
    private static string EventConstant(string name) => Interrupts.LibraryEvents.Contains(name, StringComparer.Ordinal)
        ? $"Event.{char.ToUpperInvariant(name[0])}{name[1..]}"
        : Quoted(name);

    /// <summary>"\"timer\" (Event.Timer)", or just the name in quotes for an event ChipSharp.Hardware does not name.</summary>
    private static string EventText(string name) => Interrupts.LibraryEvents.Contains(name, StringComparer.Ordinal)
        ? $"{Quoted(name)} ({EventConstant(name)})"
        : Quoted(name);

    private static string Quoted(string? name) => name is null ? "null" : $"\"{name}\"";
}
