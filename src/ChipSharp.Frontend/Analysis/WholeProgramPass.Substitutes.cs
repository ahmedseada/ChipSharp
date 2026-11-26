using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Library substitutes in the whole-program pass (see <see cref="LibrarySubstitutes"/>): the program's calls to them
/// are calls to methods it reaches and checks like its own, and the rules of interrupt handlers look into them.
/// </summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// The runtime functions the call at <paramref name="instruction"/> runs, for the rules of interrupt handlers: the
    /// one a library call becomes (<see cref="KnownExternals"/>), or for a call to a library substitute, every one the
    /// substitute calls, itself or through the library methods it calls. What a substitute does, such as the wait in
    /// <c>Timing.Delay(TimeSpan)</c>, counts where the program calls it. None for any other instruction.
    /// </summary>
    private static IEnumerable<ExternalFunction> RuntimeCalls(IlInstruction instruction)
    {
        if (instruction is { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { Definition: null } callee } })
        {
            return KnownExternals.TryMap(callee, out var external) ? [external] : [];
        }

        if (ValueTypes.CalledMethod(instruction) is not { } substitute || !LibrarySubstitutes.Defines(substitute))
        {
            return [];
        }

        var found = new List<ExternalFunction>();
        var seen = new HashSet<IlMethod>();
        var pending = new Stack<IlMethod>([substitute]);
        while (pending.TryPop(out var method))
        {
            if (!seen.Add(method))
            {
                continue;
            }

            foreach (var inner in method.Body?.Instructions ?? [])
            {
                if (ValueTypes.CalledMethod(inner) is { } called && LibrarySubstitutes.Defines(called))
                {
                    pending.Push(called);
                }
                else
                {
                    found.AddRange(RuntimeCalls(inner));
                }
            }
        }

        return found.DistinctBy(function => function.DisplayName);
    }
}
