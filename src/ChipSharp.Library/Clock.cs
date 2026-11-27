namespace ChipSharp.Library;

/// <summary>ChipSharp's C# for <see cref="ChipSharp.Hardware.Clock"/>: Start is the runtime's ClockStart.</summary>
[Substitute(typeof(ChipSharp.Hardware.Clock))]
internal static class Clock
{
    public static void Start(DateTime now) => Runtime.ClockStart(now.Ticks);
}
