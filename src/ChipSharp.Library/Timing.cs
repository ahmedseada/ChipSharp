namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's C# for the methods of <see cref="ChipSharp.Hardware.Timing"/> that are not runtime functions themselves.
/// </summary>
[Substitute(typeof(ChipSharp.Hardware.Timing))]
internal static class Timing
{
    /// <summary>
    /// <see cref="ChipSharp.Hardware.Timing.Delay"/>: waits at least <paramref name="duration"/>, in whole microseconds
    /// (a part of one counts as one), in waits of at most int.MaxValue microseconds (about 36 minutes) each. Zero or a
    /// negative duration returns at once.
    /// </summary>
    public static void Delay(System.TimeSpan duration)
    {
        long ticks = duration.Ticks;
        if (ticks <= 0)
        {
            return;
        }

        long microseconds = ticks / System.TimeSpan.TicksPerMicrosecond
            + (ticks % System.TimeSpan.TicksPerMicrosecond != 0 ? 1 : 0);
        while (microseconds > int.MaxValue)
        {
            ChipSharp.Hardware.Timing.DelayMicroseconds(int.MaxValue);
            microseconds -= int.MaxValue;
        }

        ChipSharp.Hardware.Timing.DelayMicroseconds((int)microseconds);
    }
}
