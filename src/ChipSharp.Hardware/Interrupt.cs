namespace ChipSharp.Hardware;

/// <summary>
/// Marks a method the chip runs when <see cref="Event"/> happens, interrupting whatever the program is doing at that
/// moment: <c>[Interrupt(Event.Timer)] static void Tick() =&gt; Pin.Toggle(25);</c>. The method must be static, return
/// void and take no parameters, and each event has at most one such method. Nothing needs to call it: ChipSharp puts it
/// where the chip looks for it. It runs once the program has started its event, e.g. with
/// <see cref="Timing.EveryMilliseconds"/> or <see cref="Pin.WatchRising"/>.
/// </summary>
/// <remarks>
/// <para>
/// Keep the method short: the rest of the program waits while it runs, and so do the other events. It cannot wait
/// itself (<see cref="Timing.DelayMilliseconds"/>): set a field and let <c>Main</c> do the waiting.
/// </para>
/// <para>
/// The method shares values with the rest of the program through static fields, and each field they share must be
/// <c>volatile</c> (<c>static volatile int count;</c>): otherwise the compiler may keep a copy of it in a register in
/// <c>Main</c>'s loop, and <c>Main</c> never sees the change. ChipSharp refuses to build a program that shares a field
/// that is not volatile. A <c>long</c> cannot be volatile in C#, and the chip reads and writes it in two halves, so share
/// an <c>int</c> instead.
/// </para>
/// <para>
/// In a program of top-level statements, put the method in <c>partial class Program { ... }</c> (or any class): as a
/// local function, C# would warn that nothing calls it.
/// </para>
/// <para>
/// Interrupts exist on chips, not on a PC: a program with such a method builds for a chip target, such as "pico".
/// </para>
/// </remarks>
/// <param name="event">The event, one of <see cref="Hardware.Event"/>'s: <see cref="Hardware.Event.Timer"/> or <see cref="Hardware.Event.Pin"/>.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class InterruptAttribute(string @event) : Attribute
{
    /// <summary>The event the method handles, e.g. "timer".</summary>
    public string Event { get; } = @event;
}

/// <summary>The events an <see cref="InterruptAttribute"/> method can handle.</summary>
public static class Event
{
    /// <summary>
    /// The timer: <see cref="Timing.EveryMilliseconds"/> (or <see cref="Timing.EveryMicroseconds"/>) runs the method
    /// over and over, <see cref="Timing.AfterMilliseconds"/> (or <see cref="Timing.AfterMicroseconds"/>) once.
    /// </summary>
    public const string Timer = "timer";

    /// <summary>
    /// A pin changed: <see cref="Pin.Watch"/>, <see cref="Pin.WatchRising"/> and <see cref="Pin.WatchFalling"/> say
    /// which pins, and which changes. One method handles every pin watched; it can <see cref="Pin.Read"/> them.
    /// </summary>
    public const string Pin = "pin";
}
