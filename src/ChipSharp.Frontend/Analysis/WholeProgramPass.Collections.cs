using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The library's collections in the whole-program pass: an interrupt handler that calls a method of the library which
/// makes an object (<c>list.Add(x)</c> grows the list's array, <c>new Dictionary</c>'s entries) breaks the handlers'
/// rule there, at the handler's call, as a <c>new</c> of its own does.
/// </summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// True when <paramref name="instruction"/> calls a method of the library that makes an object, or one of the library's
    /// methods it reaches in <paramref name="program"/> does (a class's <c>newobj</c>, an array on the heap of
    /// <paramref name="heapArrays"/>).
    /// </summary>
    private static bool LibraryAllocates(IlInstruction instruction, HashSet<IlMethod> program,
        IReadOnlySet<IlInstruction> heapArrays) =>
        ValueTypes.CalledMethod(instruction) is { } callee && LibrarySubstitutes.Defines(callee) && program.Contains(callee)
        && Reach([callee], program).Any(method => LibrarySubstitutes.Defines(method)
            && (method.Body?.Instructions ?? []).Any(made => ClassRules.Allocates(made) || heapArrays.Contains(made)));
}
