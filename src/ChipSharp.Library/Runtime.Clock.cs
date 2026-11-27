namespace ChipSharp.Library;

/// <summary>
/// The runtime functions of time: the clock that DateTime.Now reads and ChipSharp.Hardware's Clock.Start sets, and the
/// counter of uptime that Stopwatch and Environment.TickCount read. Only a target whose file says <c>"clock": true</c>
/// has the Clock ones (on the RP2040 rp2040_runtime.c, over the timer's 64-bit count of microseconds; on a PC the
/// Windows runtime's chipsharp_clock.c), and <c>chipsharp check</c> refuses them on any other target;
/// <see cref="LocalUtcOffset"/> every target has. On .NET each gives what .NET's own gives.
/// </summary>
internal static partial class Runtime
{
    /// <summary>
    /// The clock now, UTC, in ticks of 100 ns since 1 January 0001: on a chip the software clock (the date
    /// <see cref="ClockStart"/> was given, or 1 January 2000 at start-up, plus the time since); on a PC Windows' clock,
    /// until the program starts a clock of its own.
    /// </summary>
    public static long ClockUtcTicks() => System.DateTime.UtcNow.Ticks;

    /// <summary>The clock now as a local time: on a chip, and on a PC once the program started its clock, <see cref="ClockUtcTicks"/>.</summary>
    public static long ClockLocalTicks() => System.DateTime.Now.Ticks;

    /// <summary>Sets the clock: from now on it counts from <paramref name="ticks"/> (ChipSharp.Hardware's Clock.Start).</summary>
    public static void ClockStart(long ticks) => throw new PlatformNotSupportedException(
        "ChipSharp.Hardware.Clock.Start runs on a chip, not on .NET: build this program with ChipSharp for a target " +
        "such as 'pico' and run it there (or in the rp2040js simulator).");

    /// <summary>The counter of uptime, Stopwatch.GetTimestamp: microseconds on a chip, the performance counter on a PC.</summary>
    public static long ClockTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>The counts of <see cref="ClockTimestamp"/> in a second, Stopwatch.Frequency: 1,000,000 on a chip.</summary>
    public static long ClockFrequency() => System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// The ticks of 100 ns from one timestamp to another, as .NET's Stopwatch.GetElapsedTime computes them (the
    /// difference times 10,000,000 / Frequency, as a double, towards zero): exact in whole numbers when the frequency
    /// divides 10,000,000, as a chip's and most PCs' do.
    /// </summary>
    public static long ClockElapsedTicks(long start, long end) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(start, end).Ticks;

    /// <summary>Milliseconds since start-up (on a PC, since Windows started), Environment.TickCount64.</summary>
    public static long ClockTickCount64() => System.Environment.TickCount64;

    /// <summary>
    /// The offset of local time from UTC now, in ticks: what the z and K formats write for a time that is not UTC. 0 on
    /// a chip, which has no time zones, and on a PC whose program started its clock; else the PC's time zone's now.
    /// </summary>
    public static long LocalUtcOffset() => TimeZoneInfo.Local.GetUtcOffset(System.DateTime.Now).Ticks;
}
