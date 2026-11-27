namespace ChipSharp.Hardware;

/// <summary>
/// The clock that <see cref="DateTime.Now"/>, <see cref="DateTime.UtcNow"/> and <see cref="DateTime.Today"/> read: on a
/// chip a software clock, the date it was started at plus the time its timer has counted since (on the RP2040 the
/// timer's 64-bit count of microseconds, which runs from start-up). Until <see cref="Start"/> sets it, it starts at
/// 1 January 2000 0:00 when the chip starts. A chip has no time zones: <c>DateTime.Now</c> and
/// <c>DateTime.UtcNow</c> are the same time, Kind Local and Kind Utc.
/// </summary>
/// <remarks>
/// On a PC the clock is Windows' (and Now its local time) until the program calls <see cref="Start"/>; from then on
/// it is a software clock as on a chip, so the same program shows the same time on both. A target needs a timer for
/// it (<c>"clock": true</c> in its file); <c>chipsharp check</c> refuses DateTime.Now and Stopwatch on one without.
/// On .NET itself Start throws <see cref="PlatformNotSupportedException"/>.
/// </remarks>
public static class Clock
{
    /// <summary>
    /// Sets the clock to <paramref name="now"/>: from this moment on, DateTime.Now counts from it, e.g.
    /// <c>Clock.Start(new DateTime(2026, 11, 27, 18, 0, 0));</c> or the time a real-time clock chip keeps
    /// (<c>Clock.Start(Ds1307.Read());</c>). Its Kind does not matter.
    /// </summary>
    public static void Start(DateTime now) => throw ChipOnly.Method("Clock.Start");
}
