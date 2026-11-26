namespace ChipSharp.Hardware;

/// <summary>
/// Time: waiting, with the chip asleep until the time has passed (on the RP2040, until timer alarm 0 wakes it), and
/// the timer event, which runs the program's <c>[Interrupt(Event.Timer)]</c> method once after a time or over and over
/// (on the RP2040, timer alarm 1). A delay of zero or a negative time returns at once. The timer runs from start-up;
/// nothing needs setting up.
/// </summary>
/// <remarks>
/// These methods run on a chip, in a program built with ChipSharp. On .NET itself they throw
/// <see cref="PlatformNotSupportedException"/>.
/// </remarks>
public static class Timing
{
    /// <summary>Waits at least <paramref name="milliseconds"/> thousandths of a second.</summary>
    public static void DelayMilliseconds(int milliseconds) => throw ChipOnly.Method("Timing.DelayMilliseconds");

    /// <summary>Waits at least <paramref name="microseconds"/> millionths of a second.</summary>
    public static void DelayMicroseconds(int microseconds) => throw ChipOnly.Method("Timing.DelayMicroseconds");

    /// <summary>
    /// Waits at least <paramref name="duration"/>, in any unit: <c>Timing.Delay(TimeSpan.FromMilliseconds(500))</c>,
    /// <c>TimeSpan.FromSeconds(2)</c>, <c>TimeSpan.FromMicroseconds(250)</c>. It waits whole microseconds (a part of one
    /// counts as one), also for durations longer than an int of microseconds.
    /// </summary>
    public static void Delay(TimeSpan duration) => throw ChipOnly.Method("Timing.Delay");

    /// <summary>
    /// Runs the program's <c>[Interrupt(Event.Timer)]</c> method every <paramref name="milliseconds"/> thousandths of
    /// a second, the first time that long from now, until <see cref="Stop"/> or another Every or After call. Zero or a
    /// negative time stops the timer; more than 2147483 (about 35 minutes) counts as 2147483.
    /// </summary>
    public static void EveryMilliseconds(int milliseconds) => throw ChipOnly.Method("Timing.EveryMilliseconds");

    /// <summary>
    /// Runs the program's <c>[Interrupt(Event.Timer)]</c> method every <paramref name="microseconds"/> millionths of
    /// a second, the first time that long from now, until <see cref="Stop"/> or another Every or After call. Zero or a
    /// negative time stops the timer.
    /// </summary>
    public static void EveryMicroseconds(int microseconds) => throw ChipOnly.Method("Timing.EveryMicroseconds");

    /// <summary>
    /// Runs the program's <c>[Interrupt(Event.Timer)]</c> method once, <paramref name="milliseconds"/> thousandths of
    /// a second from now; at once for zero or a negative time. More than 2147483 (about 35 minutes) counts as 2147483.
    /// </summary>
    public static void AfterMilliseconds(int milliseconds) => throw ChipOnly.Method("Timing.AfterMilliseconds");

    /// <summary>
    /// Runs the program's <c>[Interrupt(Event.Timer)]</c> method once, <paramref name="microseconds"/> millionths of
    /// a second from now; at once for zero or a negative time.
    /// </summary>
    public static void AfterMicroseconds(int microseconds) => throw ChipOnly.Method("Timing.AfterMicroseconds");

    /// <summary>Stops the timer an Every or After call started: the <c>[Interrupt(Event.Timer)]</c> method no longer runs.</summary>
    public static void Stop() => throw ChipOnly.Method("Timing.Stop");
}
