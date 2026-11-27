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
    /// <paramref name="target"/>: a heap use (<see cref="Unsupported.IsHeapUse"/>) is no problem on a target with a heap
    /// (or for any target, when <paramref name="target"/> is null); on a target without a heap, a feature that needs one
    /// does not arrive with M3, it needs a heap the target does not have (<see cref="Unsupported.WithoutHeap"/>), and so
    /// does every place that makes an object of a class, which compiles on a target with a heap; on a target without a
    /// console, every
    /// call of Console.Write and Console.WriteLine is a problem too, added after the others. True when a problem says
    /// so: the target's own rules found something.
    /// </summary>
    public static bool Apply(TargetDescription? target, IReadOnlyList<IlMethod> methods, List<Problem> problems)
    {
        // a heap use (Unsupported.IsHeapUse) is no problem where there is a heap: on any target, when checked without one
        problems.RemoveAll(problem => problem.Unsupported.IsHeapUse && (target is null || target.HasHeap));
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

            found |= AddObjectsWithoutHeap(target, ram, methods, problems);

            // System.GC's methods work on a heap (see KnownExternals.UsesHeap)
            var gc = Unsupported.WithoutHeap(Unsupported.GcMethods, target.Name, ram);
            foreach (var method in methods.Where(method => !LibrarySubstitutes.Defines(method)))
            {
                foreach (var instruction in (method.Body?.Instructions ?? []).Where(IsGcCall))
                {
                    problems.Add(Problem.At(method, instruction, gc));
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
    /// On a target without a heap, every place that makes an object (<see cref="ClassRules.Allocates"/>: the classes
    /// themselves compile, their objects need the heap) and has no problem yet is one, among the other problems of its
    /// method's code by offset. True when there is one.
    /// </summary>
    private static bool AddObjectsWithoutHeap(TargetDescription target, ulong? ram, IReadOnlyList<IlMethod> methods,
        List<Problem> problems)
    {
        var withoutHeap = Unsupported.WithoutHeap(Unsupported.CreatingObjects, target.Name, ram);
        var found = false;

        // the problems so far are the methods', method by method in their order, those of a method's code by offset
        var end = 0;
        foreach (var method in methods)
        {
            var start = end;
            while (end < problems.Count && problems[end].Method == method)
            {
                end++;
            }

            if (LibrarySubstitutes.Defines(method))
            {
                continue;
            }

            foreach (var instruction in (method.Body?.Instructions ?? []).Where(ClassRules.Allocates))
            {
                if (problems.Skip(start).Take(end - start).Any(problem => problem.Offset == instruction.Offset))
                {
                    continue;
                }

                var at = start;
                while (at < end && (problems[at].Offset is not { } offset || offset < instruction.Offset))
                {
                    at++;
                }

                problems.Insert(at, Problem.At(method, instruction, withoutHeap));
                end++;
                found = true;
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

    /// <summary>A call of a method of System.GC that ChipSharp compiles (see <see cref="KnownExternals.UsesHeap"/>).</summary>
    private static bool IsGcCall(IlInstruction instruction) => instruction is
    {
        OpCode: IlOpCode.Call,
        Operand: IlMethodOperand { Method: { Definition: null, DeclaringType: IlNamedType { FullName: "System.GC" } } callee },
    } && KnownExternals.TryMap(callee, out var function) && KnownExternals.UsesHeap(function);

    /// <summary>A call of Console.Write or Console.WriteLine that ChipSharp compiles (see <see cref="KnownExternals"/>).</summary>
    private static bool IsConsoleWrite(IlInstruction instruction) => instruction is
    {
        OpCode: IlOpCode.Call,
        Operand: IlMethodOperand { Method: { Definition: null, DeclaringType: IlNamedType { FullName: "System.Console" } } callee },
    } && KnownExternals.TryMap(callee, out _);
}
