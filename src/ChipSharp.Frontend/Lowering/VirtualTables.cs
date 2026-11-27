using System.Reflection;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The virtual method tables and interface method tables of a program (see <see cref="Ir.TirTypeDescriptor"/>):
/// which slots they have and where, worked out from every method before any is translated, so that each call knows its
/// slot's number. Only what a call needs is there:
/// <list type="bullet">
/// <item>A slot is in the tables only when some call through it can run two methods or more on the objects the program
/// makes (<see cref="VirtualDispatch.Targets"/> for the type of the object it is made on, as
/// <see cref="VirtualMethods.StackType"/> knows it): a call with one target is a plain call, one with none (no object
/// that could be there is ever made) is the null check alone.</item>
/// <item>A class's virtual method table is its base class's, then the slots its own methods open, in their order;
/// System.Object's Equals and GetHashCode come first in every class's. A slot is at the same place in every class that
/// has it, so a call loads its method from there whatever the object's class. Only the classes whose objects the
/// program makes fill theirs in: no object has another class's descriptor as its header.</item>
/// <item>An interface's method table has its slots in the order the program first calls them; each class that the
/// program makes and that implements the interface has one in its interface map, found by the interface's descriptor
/// (<c>ChipSharp.Object.FindInterface</c>, a scan of the map, which is a few entries long), for every interface a call or
/// a type test needs.</item>
/// </list>
/// </summary>
internal sealed class VirtualTables
{
    private readonly HashSet<DispatchSlot> _live = [];
    private readonly List<DispatchSlot> _objectSlots = [];
    private readonly Dictionary<IlType, List<DispatchSlot>> _interfaceSlots = [];
    private readonly Dictionary<IlTypeDefinition, IReadOnlyList<DispatchSlot>> _tables = [];

    public VirtualTables(IReadOnlyList<IlMethod> methods)
    {
        Dispatch = VirtualDispatch.Of(methods);
        foreach (var method in methods)
        {
            var instructions = method.Body?.Instructions ?? [];
            for (var i = 0; i < instructions.Count; i++)
            {
                // the receiver as the translation sees it, so that each call with two targets or more has its slot
                if ((VirtualMethods.CalledSlot(instructions, i) ?? Delegates.LoadedSlot(instructions, i)) is not { } slot
                    || _live.Contains(slot))
                {
                    continue;
                }

                // Console.WriteLine(object) calls the ToString of its one argument, on top of the stack, and a delegate of
                // a virtual method (ldvirtftn) loads the method of the object there
                var callee = ((IlMethodOperand)instructions[i].Operand!).Method;
                var printed = StringMethods.IsConsoleWriteOfObject(instructions[i]);
                var loaded = instructions[i].OpCode == IlOpCode.Ldvirtftn;
                var receiver = VirtualMethods.StackType(method, i, printed || loaded ? 0 : callee.ParameterTypes.Count)
                    ?? (printed ? callee.ParameterTypes[0] : callee.DeclaringType);
                if (Dispatch.Targets(slot, receiver).Count < 2)
                {
                    continue;
                }

                _live.Add(slot);
                if (slot.Kind == DispatchSlotKind.Interface)
                {
                    if (!_interfaceSlots.TryGetValue(slot.Owner, out var slots))
                    {
                        _interfaceSlots.Add(slot.Owner, slots = []);
                    }

                    slots.Add(slot);
                }
            }
        }

        // object's slots in the order System.Object declares them
        _objectSlots.AddRange(_live.Where(slot => slot.Kind == DispatchSlotKind.Object)
            .OrderBy(slot => slot.Name switch { "Equals" => 0, "GetHashCode" => 1, _ => 2 }));
    }

    /// <summary>The classes the program makes and the slots it calls (rapid type analysis).</summary>
    public VirtualDispatch Dispatch { get; }

    /// <summary>The slots of System.Object's own virtual method table, which every class's starts with.</summary>
    public IReadOnlyList<DispatchSlot> ObjectTable => _objectSlots;

    /// <summary>The slots of the virtual method table of <paramref name="type"/>, a class of the program, in order.</summary>
    public IReadOnlyList<DispatchSlot> TableOf(IlTypeDefinition type)
    {
        if (!_tables.TryGetValue(type, out var table))
        {
            var inherited = VirtualMethods.BaseOf(type) is { } baseClass ? TableOf(baseClass) : _objectSlots;
            var own = type.Methods
                .Where(method => method is { IsStatic: false } && (method.Attributes & MethodAttributes.Virtual) != 0)
                .Select(VirtualMethods.SlotOf)
                .Where(slot => slot.Kind == DispatchSlotKind.Class && slot.Opener!.DeclaringType == type && _live.Contains(slot));

            // then those the instances of its generic virtual methods open, in the order the program first calls them
            var generic = _live.Where(slot => slot is { Kind: DispatchSlotKind.Class, Opener.MethodArguments.Count: > 0 }
                && slot.Opener.DeclaringType == type);
            table = [.. inherited, .. own, .. generic];
            _tables.Add(type, table);
        }

        return table;
    }

    /// <summary>The slots of the method table of <paramref name="interfaceType"/>, in order; empty when no call goes through it.</summary>
    public IReadOnlyList<DispatchSlot> InterfaceTable(IlType interfaceType) =>
        _interfaceSlots.TryGetValue(interfaceType, out var slots) ? slots : [];

    /// <summary>
    /// Where <paramref name="slot"/> is in every table that has it: its number in the virtual method table of the class
    /// that opens it (and so of every class derived from it), or in its interface's method table; null when it is in none.
    /// </summary>
    public int? IndexOf(DispatchSlot slot)
    {
        var table = slot.Kind switch
        {
            DispatchSlotKind.Class => TableOf(slot.Opener!.DeclaringType),
            DispatchSlotKind.Object => _objectSlots,
            _ => InterfaceTable(slot.Owner),
        };
        var index = table.ToList().IndexOf(slot);
        return index < 0 ? null : index;
    }
}
