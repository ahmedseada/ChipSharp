namespace ChipSharp.Library.Exceptions;

/// <summary>
/// What C#'s <c>throw</c> does in a program ChipSharp compiles: ChipSharp has no exceptions to catch yet, so a thrown
/// exception is one nothing catches, and the program stops as .NET stops it then, without running a finally block: it
/// prints "Unhandled exception. " and the exception's <c>ToString()</c> (an override too; .NET's stack trace after it is
/// not there), on standard error with exit code 1 on a PC, over UART0 and at a breakpoint on a chip. ChipSharp's
/// translation makes every <c>throw</c> a call of <see cref="Unhandled"/>; <c>throw null</c> stops with .NET's
/// NullReferenceException.
/// </summary>
internal static class Thrown
{
    public static void Unhandled(Exception exception) => Runtime.Unhandled(exception.ToString());
}
