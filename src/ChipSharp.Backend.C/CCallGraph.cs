using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// The functions SDCC must give a frame on the stack (<c>__reentrant</c>) on the processors where it keeps a function's
/// parameters and locals in static memory by default (a Padauk, an 8051, ...): a function whose locals that would
/// overwrite while it still needs them, because it can call itself again (it is in a cycle of the program's calls,
/// through other functions, virtual and interface calls and delegates), and every function called through an address
/// (SDCC calls those only with their arguments on the stack). The generated file marks them with
/// <see cref="CCodeGenerator.ReentrantMacro"/>, which is <c>__reentrant</c> with those processors' SDCC and nothing
/// elsewhere.
/// </summary>
internal static class CCallGraph
{
    /// <summary>The defined functions of <paramref name="module"/> that may run again while they run, or are called through an address.</summary>
    public static HashSet<TirFunction> Reentrant(TirModule module)
    {
        var defined = module.Functions.Where(function => function.Kind == TirFunctionKind.Defined).ToList();
        var addressed = Addressed(module);
        var callees = new Dictionary<TirFunction, List<TirFunction>>();
        foreach (var function in defined)
        {
            var calls = new List<TirFunction>();
            var indirect = false;
            foreach (var instruction in function.Blocks.SelectMany(block => block.Instructions))
            {
                if (instruction is TirCall { Callee: { Kind: TirFunctionKind.Defined } callee })
                {
                    calls.Add(callee);
                }

                indirect |= instruction is TirCallIndirect;
            }

            if (indirect)
            {
                calls.AddRange(addressed);
            }

            callees[function] = calls;
        }

        var reentrant = Recursive(defined, callees);
        reentrant.UnionWith(addressed);
        return reentrant;
    }

    /// <summary>The defined functions a call through an address may reach: those whose address is taken, or in a method table.</summary>
    public static HashSet<TirFunction> Addressed(TirModule module)
    {
        var addressed = new HashSet<TirFunction>(module.Functions.SelectMany(function => function.Blocks)
            .SelectMany(block => block.Instructions).OfType<TirFunctionAddress>().Select(address => address.Function));
        foreach (var type in module.TypeDescriptors)
        {
            addressed.UnionWith(type.VirtualMethods.OfType<TirFunction>());
            addressed.UnionWith(type.Interfaces.SelectMany(map => map.Methods).OfType<TirFunction>());
        }

        addressed.RemoveWhere(function => function.Kind != TirFunctionKind.Defined);
        return addressed;
    }

    /// <summary>The functions in a cycle of calls (Tarjan's strongly connected components, without recursion).</summary>
    private static HashSet<TirFunction> Recursive(List<TirFunction> functions,
        Dictionary<TirFunction, List<TirFunction>> callees)
    {
        var recursive = new HashSet<TirFunction>();
        var index = new Dictionary<TirFunction, int>();
        var lowest = new Dictionary<TirFunction, int>();
        var onStack = new HashSet<TirFunction>();
        var stack = new Stack<TirFunction>();
        foreach (var root in functions)
        {
            if (index.ContainsKey(root))
            {
                continue;
            }

            var work = new Stack<(TirFunction Function, int Next)>();
            work.Push((root, 0));
            while (work.TryPop(out var frame))
            {
                var (function, next) = frame;
                if (next == 0)
                {
                    index[function] = lowest[function] = index.Count;
                    stack.Push(function);
                    onStack.Add(function);
                }

                var calls = callees[function];
                if (next < calls.Count)
                {
                    work.Push((function, next + 1));
                    var callee = calls[next];
                    if (!index.ContainsKey(callee))
                    {
                        work.Push((callee, 0));
                    }
                    else if (onStack.Contains(callee))
                    {
                        lowest[function] = Math.Min(lowest[function], index[callee]);
                    }

                    continue;
                }

                if (lowest[function] == index[function])
                {
                    var component = new List<TirFunction>();
                    TirFunction member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    } while (member != function);

                    if (component.Count > 1 || calls.Contains(function))
                    {
                        recursive.UnionWith(component);
                    }
                }

                if (work.TryPeek(out var caller))
                {
                    lowest[caller.Function] = Math.Min(lowest[caller.Function], lowest[function]);
                }
            }
        }

        return recursive;
    }
}
