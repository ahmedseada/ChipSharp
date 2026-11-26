namespace ChipSharp.Hardware;

/// <summary>
/// Waiting: the chip sleeps until the time has passed (on the RP2040, until timer alarm 0 wakes it). Zero or a
/// negative time returns at once.
/// </summary>
/// <remarks>
/// These methods run on a chip, in a program built with ChipSharp. On .NET itself they throw
/// <see cref="PlatformNotSupportedException"/>.
/// </remarks>
public static class Delay
{
    /// <summary>Waits <paramref name="milliseconds"/> thousandths of a second.</summary>
    public static void Milliseconds(int milliseconds) => throw ChipOnly.Method("Delay.Milliseconds");

    /// <summary>Waits <paramref name="microseconds"/> millionths of a second.</summary>
    public static void Microseconds(int microseconds) => throw ChipOnly.Method("Delay.Microseconds");
}
