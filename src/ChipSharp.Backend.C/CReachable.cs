using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// What of a module the program can reach, from its entry point and its exported functions (an interrupt handler's
/// entry): the functions it calls or takes the address of, the globals and type descriptors they name (in the code that
/// is written: see <see cref="CLiveness"/>), and what those
/// name in turn (a descriptor's base, interfaces and methods; a constant object's header and fields). The c code
/// generator writes only these. The llvm code generator writes everything and leaves the rest to LLVM, which drops a
/// function nothing calls; C warns of one (-Wunused-function), and SDCC keeps it, in a Padauk's 2K words of program memory.
/// </summary>
internal sealed class CReachable
{
    private CReachable()
    {
    }

    public HashSet<TirFunction> Functions { get; } = [];

    public HashSet<TirGlobal> Globals { get; } = [];

    public HashSet<TirTypeDescriptor> Types { get; } = [];

    public static CReachable Find(TirModule module)
    {
        var reachable = new CReachable();
        var functions = new Stack<TirFunction>();
        var globals = new Stack<TirGlobal>();
        var types = new Stack<TirTypeDescriptor>();

        void Function(TirFunction? function)
        {
            if (function is not null && reachable.Functions.Add(function))
            {
                functions.Push(function);
            }
        }

        void Global(TirGlobal global)
        {
            if (reachable.Globals.Add(global))
            {
                globals.Push(global);
            }
        }

        void Type(TirTypeDescriptor? type)
        {
            if (type is not null && reachable.Types.Add(type))
            {
                types.Push(type);
            }
        }

        Function(module.EntryPoint);
        foreach (var exported in module.Functions.Where(function => function.ExportName is not null))
        {
            Function(exported);
        }

        while (functions.Count > 0 || globals.Count > 0 || types.Count > 0)
        {
            if (functions.TryPop(out var function))
            {
                var live = function.Kind == TirFunctionKind.Defined ? CLiveness.Find(function).Live : null;
                foreach (var instruction in function.Blocks.SelectMany(block => block.Instructions).Where(i => live!.Contains(i)))
                {
                    switch (instruction)
                    {
                        case TirCall call:
                            Function(call.Callee);
                            break;
                        case TirFunctionAddress address:
                            Function(address.Function);
                            break;
                        case TirGlobalAddress address:
                            Global(address.Global);
                            break;
                        case TirTypeAddress address:
                            Type(address.TypeDescriptor);
                            break;
                        case TirNewObject allocation:
                            Type(allocation.TypeDescriptor);
                            break;
                        case TirNewArray allocation:
                            Type(allocation.TypeDescriptor);
                            break;
                    }
                }
            }
            else if (globals.TryPop(out var global))
            {
                Type(global.Header);
                Type(global.ObjectType);
                foreach (var value in global.FieldValues ?? [])
                {
                    switch (value)
                    {
                        case TirGlobalAddress address:
                            Global(address.Global);
                            break;
                        case TirTypeAddress address:
                            Type(address.TypeDescriptor);
                            break;
                    }
                }
            }
            else if (types.TryPop(out var type))
            {
                Type(type.Base);
                Type(type.ElementDescriptor);
                foreach (var method in type.VirtualMethods)
                {
                    Function(method);
                }

                foreach (var map in type.Interfaces)
                {
                    Type(map.Interface);
                    foreach (var method in map.Methods)
                    {
                        Function(method);
                    }
                }
            }
        }

        return reachable;
    }
}
