using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// The stack checks of a chip (a target whose capability "stackCheck" is true, as armv6m's is): a chip has no operating
/// system to stop a program whose stack runs into its variables or its heap, and a Cortex-M0+ cannot catch it either (a
/// fault of its MPU's guard region, a HardFault, would need the very stack that ran out to enter its handler: the core
/// locks up instead). So the functions through which the stack can grow without end check it themselves: when one starts,
/// it compares the stack pointer (its frame is there already: the call that stops the program needs one, so LLVM sets
/// it up first) with <see cref="LimitSymbol"/>, which the linker script puts a margin above the stack's bottom, and below it calls <see cref="OverflowSymbol"/> (the armv6m runtime: a stack
/// of its own, then "Stack overflow.", as .NET says it, and a stop). The checks, three instructions and a literal, are in:
/// <list type="bullet">
/// <item>every function that can call itself again (<see cref="Find"/>): in a cycle of the program's calls, through
/// other functions, virtual and interface calls and delegates (a call through an address may reach every function
/// whose address the program takes or whose method tables hold it). Only those make a stack as deep as the data they
/// work on; the others go as deep as the program's code, which the margin is for.</item>
/// <item>every function whose slots take <see cref="LargeFrame"/> bytes or more (big arrays on the stack), which could
/// pass the margin at once.</item>
/// </list>
/// The PC needs none: Windows' guard page stops the program there (see the windows-x64 runtime).
/// </summary>
internal static class LlvmStackChecks
{
    /// <summary>The target capability that asks for the checks.</summary>
    public const string Capability = "stackCheck";

    /// <summary>The linker script's symbol (an address, not a variable): below it the stack has run out.</summary>
    public const string LimitSymbol = "__chipsharp_stack_limit";

    /// <summary><c>void chipsharp_stack_overflow(void)</c>: says so and stops; it does not return.</summary>
    public const string OverflowSymbol = "chipsharp_stack_overflow";

    /// <summary>The bytes of slots from which a function checks the stack, recursive or not.</summary>
    public const long LargeFrame = 256;

    /// <summary>
    /// The functions of <paramref name="module"/> that check the stack, for <paramref name="target"/>; none when the
    /// target does not ask for it.
    /// </summary>
    public static HashSet<TirFunction> Find(TirModule module, TargetDescription target, TirLayout layout)
    {
        var checks = new HashSet<TirFunction>();
        if (!target.HasCapability(Capability))
        {
            return checks;
        }

        var defined = module.Functions.Where(function => function.Kind == TirFunctionKind.Defined).ToList();

        // what a call through an address may reach: the functions whose address is taken, or in a method table
        var addressed = new HashSet<TirFunction>(defined.SelectMany(function => function.Blocks)
            .SelectMany(block => block.Instructions).OfType<TirFunctionAddress>().Select(address => address.Function)
            .Where(function => function.Kind == TirFunctionKind.Defined));
        foreach (var type in module.TypeDescriptors)
        {
            addressed.UnionWith(type.VirtualMethods.OfType<TirFunction>());
            addressed.UnionWith(type.Interfaces.SelectMany(map => map.Methods).OfType<TirFunction>());
        }

        addressed.RemoveWhere(function => function.Kind != TirFunctionKind.Defined);
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

        checks.UnionWith(Recursive(defined, callees));
        checks.UnionWith(defined.Where(function => function.Slots.Sum(slot => layout.SizeOf(slot.Type)) >= LargeFrame));

        return checks;
    }

    /// <summary>
    /// The lines at the start of a function that checks the stack, after its slots: the stack pointer below the limit
    /// goes to <paramref name="overflow"/>, else on to <paramref name="next"/>.
    /// </summary>
    public static IEnumerable<string> Check(string pointer, string low, string overflow, string next)
    {
        yield return $"{pointer} = call ptr @llvm.stacksave.p0()";
        yield return $"{low} = icmp ult ptr {pointer}, @{LimitSymbol}";
        yield return $"br i1 {low}, label %{overflow}, label %{next}";
    }

    /// <summary>The declarations a function that checks the stack needs, which the module writes once.</summary>
    public static IEnumerable<string> Declarations { get; } =
    [
        "declare ptr @llvm.stacksave.p0()",
        $"declare void @{OverflowSymbol}() cold noreturn nounwind",
        $"@{LimitSymbol} = external global i8",
    ];

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
