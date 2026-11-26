namespace ChipSharp.Hardware;

/// <summary>
/// Time: waiting, with the chip asleep until the time has passed (on the RP2040, until timer alarm 0 wakes it).
/// Zero or a negative time returns at once. The timer runs from start-up; nothing needs setting up.
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
}
