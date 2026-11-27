namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.Environment"/>: <see cref="CurrentManagedThreadId"/>, which C# reads in the class it
/// makes of an iterator (<c>yield return</c>), to hand the first enumerator out of the object itself on the thread that
/// made it (a program runs on one thread, .NET's first: 1); and <see cref="TickCount"/> and <see cref="TickCount64"/>,
/// the milliseconds since start-up (on a PC since Windows started, as .NET's), on a target with a clock.
/// </summary>
[Substitute(typeof(System.Environment))]
public static class Environment
{
    public static int CurrentManagedThreadId => 1;

    /// <summary>Milliseconds since start-up, as an int: it wraps around to int.MinValue after 24.9 days, as .NET's does.</summary>
    public static int TickCount => (int)Runtime.ClockTickCount64();

    public static long TickCount64 => Runtime.ClockTickCount64();
}
