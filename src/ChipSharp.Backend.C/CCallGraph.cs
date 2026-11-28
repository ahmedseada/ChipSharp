using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// The functions that need a frame on the stack (SDCC's <c>__reentrant</c>) on a target whose compiler keeps a
/// function's parameters and locals at fixed addresses in RAM otherwise (<c>"recursion": "reentrant"</c>: SDCC's pdk14
/// port), as a function that runs again while it still runs would overwrite them:
/// <list type="bullet">
/// <item>every function in a cycle of the program's calls (it can call itself again, through other functions, virtual
/// and interface calls and delegates);</item>
/// <item>every function that may run both in an interrupt and outside one: one that the exported functions (an
/// interrupt handler's entry, which the runtime's vector calls) reach, and that the entry point reaches too;</item>
/// <item>every function called through an address (SDCC passes arguments through a pointer only on the stack).</item>
/// </list>
/// A call through an address may reach every function whose address the program takes, or a method table holds. The
/// generated file marks these with <see cref="CCodeGenerator.ReentrantMacro"/>.
/// </summary>
internal static class CCallGraph
{
    /// <summary>The defined functions of <paramref name="module"/> that may run again while they run, or are called through an address.</summary>
    public static HashSet<TirFunction> Reentrant(TirModule module)
    {
        var defined = module.Functions.Where(function => function.Kind == TirFunctionKind.Defined).ToList();
        var addressed = Addressed(module);
        var callees = Callees(defined, addressed);
        var reentrant = Recursive(defined, callees);
        reentrant.UnionWith(addressed);
        if (module.EntryPoint is { } entry)
        {
            var inInterrupts = Reached(defined.Where(function => function.ExportName is not null), callees);
            var outside = Reached([entry], callees);
            reentrant.UnionWith(inInterrupts.Where(outside.Contains));
        }

        return reentrant;
    }

    /// <summary>The defined functions of <paramref name="module"/> in a cycle of its calls: those that can call themselves again.</summary>
    public static HashSet<TirFunction> Recursive(TirModule module)
    {
        var defined = module.Functions.Where(function => function.Kind == TirFunctionKind.Defined).ToList();
        return Recursive(defined, Callees(defined, Addressed(module)));
    }

    /// <summary>What each defined function calls: a call through an address, every function whose address is taken.</summary>
    private static Dictionary<TirFunction, List<TirFunction>> Callees(List<TirFunction> defined, HashSet<TirFunction> addressed)
    {
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

        return callees;
    }

    /// <summary>The functions <paramref name="roots"/> run: themselves, and every function they call, directly or not.</summary>
    private static HashSet<TirFunction> Reached(IEnumerable<TirFunction> roots,
        Dictionary<TirFunction, List<TirFunction>> callees)
    {
        var reached = new HashSet<TirFunction>();
        var work = new Stack<TirFunction>(roots);
        while (work.TryPop(out var function))
        {
            if (reached.Add(function) && callees.TryGetValue(function, out var calls))
            {
                foreach (var callee in calls)
                {
                    work.Push(callee);
                }
            }
        }

        return reached;
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
