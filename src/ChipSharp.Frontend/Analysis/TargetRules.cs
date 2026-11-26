using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// What a program may use on one target, from the capabilities of its target file: "heap" (see
/// <see cref="TargetDescription.HeapSize"/>) and "console" (<see cref="TargetDescription.HasConsole"/>). The
/// whole-program pass applies them when it checks a program for a target; checked without one, a program may use what
/// any target has.
/// </summary>
internal static class TargetRules
{
    /// <summary>
    /// Changes <paramref name="problems"/>, the places in <paramref name="methods"/> that ChipSharp cannot compile, for
    /// <paramref name="target"/>: on a target without a heap, a feature that needs one does not arrive with M3, it needs
    /// a heap the target does not have (<see cref="Unsupported.WithoutHeap"/>); on a target without a console, every
    /// call of Console.Write and Console.WriteLine is a problem too, added after the others. True when a problem says
    /// so: the target's own rules found something.
    /// </summary>
    public static bool Apply(TargetDescription? target, IReadOnlyList<IlMethod> methods, List<Problem> problems)
    {
        if (target is null)
        {
            return false;
        }

        var found = false;
        if (!target.HasHeap)
        {
            var ram = DataMemory(target)?.Size;
            for (var i = 0; i < problems.Count; i++)
            {
                if (problems[i].Unsupported.NeedsHeap)
                {
                    problems[i] = problems[i].With(Unsupported.WithoutHeap(problems[i].Unsupported, target.Name, ram));
                    found = true;
                }
            }
        }

        if (!target.HasConsole)
        {
            var noConsole = Unsupported.NoConsole(target.Name);
            foreach (var method in methods.Where(method => !LibrarySubstitutes.Defines(method)))
            {
                foreach (var instruction in (method.Body?.Instructions ?? []).Where(IsConsoleWrite))
                {
                    problems.Add(Problem.At(method, instruction, noConsole));
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The memory the target's variables are in: the region "link.data" names, else the one named "ram"; null when it
    /// has neither (a PC).
    /// </summary>
    private static MemoryRegion? DataMemory(TargetDescription target) =>
        target.Memory.TryGetValue(target.Setting("link.data") ?? "ram", out var region) ? region : null;

    /// <summary>A call of Console.Write or Console.WriteLine that ChipSharp compiles (see <see cref="KnownExternals"/>).</summary>
    private static bool IsConsoleWrite(IlInstruction instruction) => instruction is
    {
        OpCode: IlOpCode.Call,
        Operand: IlMethodOperand { Method: { Definition: null, DeclaringType: IlNamedType { FullName: "System.Console" } } callee },
    } && KnownExternals.TryMap(callee, out _);
}
