using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// Rapid type analysis: the classes whose objects the program makes (<c>newobj</c>) and the slots it calls through
/// (<see cref="VirtualMethods.CalledSlot"/>), so that only what can run is compiled. An override is part of the program
/// only when its class (or a class derived from it that keeps it) is made somewhere and its slot is called somewhere;
/// a call whose slot has one method on every object that can be there is a plain call (<see cref="Targets"/>).
/// <list type="bullet">
/// <item>The whole-program pass follows the program with it (<see cref="Follow"/>): each class made adds what the slots
/// called so far run on it, each slot called what it runs on the classes made so far, until nothing new is reached.</item>
/// <item>The translation sees the same, from the methods the pass found (<see cref="Of"/>).</item>
/// </list>
/// </summary>
internal sealed class VirtualDispatch
{
    private readonly List<IlTypeDefinition> _created = [];
    private readonly HashSet<IlTypeDefinition> _isCreated = [];
    private readonly List<DispatchSlot> _called = [];
    private readonly HashSet<DispatchSlot> _isCalled = [];
    private readonly Dictionary<(IlTypeDefinition, DispatchSlot), DispatchTarget?> _resolved = [];

    /// <summary>The classes of the program whose objects it makes, in the order first made.</summary>
    public IReadOnlyList<IlTypeDefinition> Created => _created;

    /// <summary>True when the program makes objects of System.Object itself (<c>new object()</c>).</summary>
    public bool CreatesPlainObjects { get; private set; }

    /// <summary>
    /// True when the program makes arrays (<c>newarr</c>, <c>Array.Empty&lt;T&gt;()</c>), which may be objects too: they
    /// run object's own Equals and GetHashCode.
    /// </summary>
    public bool CreatesArrays { get; private set; }

    /// <summary>The slots the program calls through, in the order first called.</summary>
    public IReadOnlyList<DispatchSlot> Called => _called;

    /// <summary>What the classes and slots of <paramref name="methods"/> are: every object they make and slot they call.</summary>
    public static VirtualDispatch Of(IEnumerable<IlMethod> methods)
    {
        var dispatch = new VirtualDispatch();
        foreach (var method in methods)
        {
            var instructions = method.Body?.Instructions ?? [];
            for (var i = 0; i < instructions.Count; i++)
            {
                dispatch.Follow(instructions, i);
            }
        }

        return dispatch;
    }

    /// <summary>
    /// Takes in instruction <paramref name="index"/> of <paramref name="instructions"/>: a class it makes, or a slot it
    /// calls. Returns the methods that this makes part of the program (none twice), generic code left out.
    /// </summary>
    public IReadOnlyList<IlMethod> Follow(IReadOnlyList<IlInstruction> instructions, int index)
    {
        if (VirtualMethods.CalledSlot(instructions, index) is { } slot)
        {
            return _isCalled.Add(slot) ? Reached(Call(slot)) : [];
        }

        if (instructions[index] is { OpCode: IlOpCode.Newarr }
            or { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { Name: "Empty", Definition: null } } })
        {
            CreatesArrays = true;
            return [];
        }

        if (instructions[index] is { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } } made
            && ClassRules.Allocates(made))
        {
            if (constructor.Definition is not { } definition)
            {
                CreatesPlainObjects = true;
                return [];
            }

            return _isCreated.Add(definition.DeclaringType) ? Reached(Create(definition.DeclaringType)) : [];
        }

        return [];
    }

    /// <summary>
    /// What a call through <paramref name="slot"/> can run, on an object of a class the program makes that a variable of
    /// <paramref name="receiver"/> can hold (null: any), each once: none when no such object is ever made (only null can
    /// be there), one when the call has a single target.
    /// </summary>
    public IReadOnlyList<DispatchTarget> Targets(DispatchSlot slot, IlType? receiver)
    {
        var targets = new List<DispatchTarget>();
        foreach (var type in _created.Where(type => VirtualMethods.Has(type, slot) && VirtualMethods.Fits(type, receiver)))
        {
            if (Resolve(type, slot) is { } target && !targets.Contains(target))
            {
                targets.Add(target);
            }
        }

        if (slot.Kind == DispatchSlotKind.Object && (CreatesPlainObjects || CreatesArrays)
            && (receiver is null || ObjectTypes.IsSystemObject(receiver))
            && new DispatchTarget(null, slot.Name) is var own && !targets.Contains(own))
        {
            targets.Add(own);
        }

        return targets;
    }

    /// <summary>What <paramref name="slot"/> runs on an object of <paramref name="type"/> (see <see cref="VirtualMethods.Resolve"/>).</summary>
    public DispatchTarget? Resolve(IlTypeDefinition type, DispatchSlot slot)
    {
        if (!_resolved.TryGetValue((type, slot), out var target))
        {
            target = VirtualMethods.Resolve(type, slot);
            _resolved.Add((type, slot), target);
        }

        return target;
    }

    private IEnumerable<DispatchTarget?> Call(DispatchSlot slot)
    {
        _called.Add(slot);
        return _created.Where(type => VirtualMethods.Has(type, slot)).Select(type => Resolve(type, slot));
    }

    private IEnumerable<DispatchTarget?> Create(IlTypeDefinition type)
    {
        _created.Add(type);
        return _called.Where(slot => VirtualMethods.Has(type, slot)).Select(slot => Resolve(type, slot));
    }

    private static IlMethod[] Reached(IEnumerable<DispatchTarget?> targets) =>
    [
        .. targets.Select(target => target?.Method)
            .OfType<IlMethod>()
            .Where(method => !Generics.Instantiations.IsGeneric(method))
            .Distinct(),
    ];
}
