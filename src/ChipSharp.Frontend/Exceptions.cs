using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// C#'s <c>throw</c>, without exceptions to catch (catching arrives later): a thrown exception is one nothing catches, so
/// the program stops as .NET stops it then, printing "Unhandled exception. " and the exception's <c>ToString()</c>
/// (standard error and exit code 1 on a PC, UART0 and a breakpoint on a chip), and runs no finally block, as .NET runs
/// none either (see <see cref="TryFinally"/>).
/// <list type="bullet">
/// <item>The exceptions are objects of ChipSharp.Library's substitutes for .NET's (<c>Exceptions/*.cs</c>:
/// <c>Exception</c>, <c>InvalidOperationException</c>, <c>ArgumentException</c> with its parameter's name,
/// <c>KeyNotFoundException</c>, ...), with .NET's messages; the program's own exception classes derive from them like
/// from any class (<see cref="ObjectTypes.Class"/>), overriding <c>Message</c> or <c>ToString</c> too.</item>
/// <item>A <c>throw</c> is a call of ChipSharp.Library's <c>Exceptions.Thrown.Unhandled</c> (<see cref="Thrower"/>),
/// which the whole-program pass reaches from every method that throws, and which never returns.</item>
/// </list>
/// An exception is an object, so a <c>throw</c> needs a heap.
/// </summary>
internal static class Exceptions
{
    /// <summary>ChipSharp.Library's class of what a throw does.</summary>
    public const string ThrownType = "ChipSharp.Library.Exceptions.Thrown";

    private static readonly Lazy<IlMethod> Unhandled = new(() =>
        LibrarySubstitutes.Assembly.FindType(ThrownType)?.Methods.FirstOrDefault(method => method.Name == "Unhandled")
        ?? throw new InvalidOperationException($"ChipSharp's library has no {ThrownType}.Unhandled."));

    /// <summary>The library method every <c>throw</c> calls with the exception: it stops the program.</summary>
    public static IlMethod Thrower => Unhandled.Value;

    /// <summary>The method <paramref name="instruction"/> makes part of the program: <see cref="Thrower"/> for a throw.</summary>
    public static IlMethod? Reached(IlInstruction instruction) =>
        instruction.OpCode == IlOpCode.Throw ? Thrower : null;
}
