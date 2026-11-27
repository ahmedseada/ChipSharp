namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.Environment"/>: <see cref="CurrentManagedThreadId"/>, which C# reads in the class it
/// makes of an iterator (<c>yield return</c>), to hand the first enumerator out of the object itself on the thread that
/// made it. A program runs on one thread, .NET's first: 1.
/// </summary>
[Substitute(typeof(System.Environment))]
public static class Environment
{
    public static int CurrentManagedThreadId => 1;
}
