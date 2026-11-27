using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The program's classes (see <see cref="ClassRules"/>), with the helpers of MethodLowering.Objects.cs:
/// <list type="bullet">
/// <item><c>newobj</c> of a class's constructor: a new object of the class (<c>newobject</c> of its descriptor), then
/// the constructor, called with the object as its <c>this</c> and the arguments, which IL evaluated before; then the
/// object is on the stack. A constructor's call of object's own constructor (<c>base()</c> at the root) does nothing.</item>
/// <item><c>callvirt</c> (<see cref="CallVirtual"/>): the null check of the object, then the call of the method, which
/// is not virtual. <c>call</c> of an instance method (C#'s <c>this(...)</c>, <c>base.M()</c>, a call on
/// <c>new C()</c>) is a plain call, as in .NET: no null check.</item>
/// <item><c>ldfld</c>, <c>stfld</c> and <c>ldflda</c> of an instance field of a class: the field's address in the object
/// (<c>fieldaddr</c> on the ref, after its null check), and a load or store through it (volatile after
/// <c>volatile.</c>). The address of a field is a reference like any other (a struct field's methods take it as their
/// <c>this</c>): it points into the object, which the garbage collector sees.</item>
/// <item><c>this</c> of a class's method is never null (C# checks the object where it calls the method), so its fields
/// need no null check; any other object is checked once per block that uses it.</item>
/// <item>Null meeting an object where ways join (C#'s <c>c ? node : null</c>, <c>node?.Next</c>): the null that comes
/// first goes into both the ptr and the ref stack slot, so the block that joins them takes an object (a ref) when an
/// object comes in too, and text (a ptr) when text does (<see cref="ArrivingTypes"/>). A block that every way reaches
/// with null starts with that null, pending (<see cref="EntryValue"/>).</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>
    /// The blocks, and the depths of their entry stacks, that every way in seen so far reaches with null: the entry type
    /// there is ptr until an object comes in, which makes it ref.
    /// </summary>
    private readonly HashSet<(IlBlock Block, int Depth)> _nullEntries = [];

    private bool? _mayHoldObjects;

    /// <summary>True for the instructions <see cref="LowerClassInstruction"/> translates.</summary>
    private static bool IsClassInstruction(IlInstruction instruction) => instruction switch
    {
        { OpCode: IlOpCode.Callvirt } => true,
        { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method.Definition: { } constructor } } =>
            ObjectTypes.IsClass(constructor.DeclaringType),
        { OpCode: IlOpCode.Ldfld or IlOpCode.Ldflda or IlOpCode.Stfld,
            Operand: IlFieldOperand { Field.Definition: { IsStatic: false } field } } => ObjectTypes.IsClass(field.DeclaringType),
        { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: var callee } } =>
            ClassRules.IsObjectConstructor(callee) || ClassRules.IsReferenceEquals(callee),
        _ => false,
    };

    private void LowerClassInstruction(IlInstruction instruction)
    {
        switch (instruction.OpCode)
        {
            case IlOpCode.Newobj:
                NewClassObject(((IlMethodOperand)instruction.Operand!).Method.Definition!);
                break;

            case IlOpCode.Callvirt:
                CallVirtual();
                break;

            case IlOpCode.Ldfld:
                var field = ObjectField();
                var address = ObjectFieldAddress(PopObject(), field);
                Push(ToStack(_ir.Load(address.Field!.Type, address, IsVolatileAccess()), field.Type));
                break;

            case IlOpCode.Ldflda:
                field = ObjectField();
                Push(ObjectFieldAddress(PopObject(), field));
                break;

            case IlOpCode.Stfld:
                field = ObjectField();
                var (objectType, index) = ObjectTypes.FieldOf(field);
                var value = FromStack(PopValue(), objectType.Fields[index].Type, field.Type,
                    $"field {ObjectTypes.Name(field.DeclaringType)}.{field.Name}");
                _ir.Store(ObjectFieldAddress(PopObject(), field), value, IsVolatileAccess());
                break;

            case IlOpCode.Call when ClassRules.IsObjectConstructor(((IlMethodOperand)instruction.Operand!).Method):
                // object's constructor has nothing to do
                PopObject();
                break;

            case IlOpCode.Call:
                // object.ReferenceEquals(a, b)
                var right = PopObject();
                var left = PopObject();
                Push(StackValue.OfBool(_ir.Compare(TirCompareOp.Eq, left, right)));
                break;

            default:
                throw Unsupported(instruction);
        }
    }

    /// <summary>
    /// newobj of <paramref name="constructor"/>, a class's: the arguments stay on the stack, with the new object slid in
    /// under them as <c>this</c>, for <see cref="Call"/>; then the object is pushed.
    /// </summary>
    private void NewClassObject(IlMethod constructor)
    {
        var descriptor = _program.ClassDescriptor(constructor.DeclaringType);
        var arguments = new StackValue[constructor.Parameters.Count];
        for (var i = arguments.Length - 1; i >= 0; i--)
        {
            arguments[i] = PopValue();
        }

        var made = NewObject(descriptor);
        PushObject(made);
        foreach (var argument in arguments)
        {
            Push(argument);
        }

        Call();
        PushObject(made);
    }

    /// <summary>
    /// <c>callvirt</c>: a call of an instance method on an object, the way C# makes every one, which .NET makes only
    /// after checking the object for null. A callvirt through a slot (a virtual method, an interface's, object's) is
    /// MethodLowering.Virtual.cs'; what comes here is not virtual, so it is the null check of the object, then a plain
    /// <see cref="Call"/>.
    /// </summary>
    private void CallVirtual()
    {
        var reference = ((IlMethodOperand)_instruction.Operand!).Method;
        if (reference.Definition is not { IsStatic: false } method || !ObjectTypes.IsClass(method.DeclaringType))
        {
            throw Error($"callvirt of {reference.DisplayName} is not supported: ChipSharp calls the methods of the " +
                "program's own classes");
        }

        if (ClassRules.IsOverridable(method))
        {
            throw Error($"callvirt of {method}, a virtual method, is not supported yet (virtual calls arrive with M3)");
        }

        // this is below the arguments
        var self = _stack.Count - method.Parameters.Count - 1;
        if (self < 0)
        {
            throw Error($"{_instruction.Name} of {method} needs {method.Parameters.Count + 1} values, but the evaluation " +
                $"stack has {_stack.Count}");
        }

        _stack[self] = StackValue.Of(NullCheck(ObjectFromStack(_stack[self], $"the object {method} is called on")));
        Call();
    }

    /// <summary>
    /// The instance field of a class that the ldfld, ldflda or stfld being translated names. Its class gets its
    /// descriptor, if no object of it was made yet: <c>fieldaddr</c> names the object type of a class of the module.
    /// </summary>
    private IlField ObjectField()
    {
        var field = ((IlFieldOperand)_instruction.Operand!).Field.Definition!;
        _program.ClassDescriptor(field.DeclaringType);
        return field;
    }

    /// <summary>
    /// At the start of each block: no ref is known not to be null yet, as no value of another block is used in it, but
    /// for <c>this</c> of a class's method, a parameter, which is never null.
    /// </summary>
    private void StartObjectsOfBlock()
    {
        _notNull.Clear();
        if (!_method.IsStatic && ObjectTypes.IsClass(_method.DeclaringType) && _argumentSlots.Length > 0
            && _argumentSlots[0] is null && _arguments[0].Parameter is { } self)
        {
            _notNull.Add(self);
        }
    }

    /// <summary>
    /// The value at <paramref name="depth"/> of the stack <paramref name="block"/> starts with: null when every way into
    /// it brought null there (C# may keep a null local on the stack, <c>none ?? list</c>), so that it meets an object
    /// as null again; else the value in the stack slot.
    /// </summary>
    private StackValue EntryValue(IlBlock block, int depth) => _nullEntries.Contains((block, depth))
        ? StackValue.OfNull()
        : StackValue.InSlot(StackSlot(depth, block.EntryStack![depth]));

    /// <summary>
    /// For <see cref="Leave"/>: the types the stack has for <paramref name="successor"/>, <paramref name="types"/> but
    /// for null meeting an object. Null coming in at a depth where the block takes an object comes in as an object; an
    /// object coming in where every way seen so far brought null (and the block is still to be translated) makes the
    /// block take an object there. Null where no other way came in yet is a ptr, as before, and remembered as such.
    /// </summary>
    private TirType[] ArrivingTypes(IlBlock successor, TirType[] types)
    {
        var arriving = (TirType[])types.Clone();
        var entry = successor.EntryStack;
        var ahead = successor.First > _block.First;
        for (var depth = 0; depth < _stack.Count; depth++)
        {
            var known = entry is not null && depth < entry.Count ? entry[depth] : null;
            if (_stack[depth].IsNull)
            {
                if (known == TirType.Ref)
                {
                    arriving[depth] = TirType.Ref;
                }
                else if (entry is null && ahead && MayHoldObjects)
                {
                    _nullEntries.Add((successor, depth));
                }
            }
            else if (_nullEntries.Contains((successor, depth)) && known == TirType.Ptr)
            {
                if (!ahead)
                {
                    throw Error($"control jumps back to {IlPrinter.Label(successor.Offset)} with a value where every " +
                        "way before brought null");
                }

                _nullEntries.Remove((successor, depth));
                if (_stack[depth].IsObject)
                {
                    var upgraded = entry!.ToArray();
                    upgraded[depth] = TirType.Ref;
                    successor.EntryStack = upgraded;
                }
            }
        }

        return arriving;
    }

    /// <summary>
    /// For <see cref="Leave"/>: the stores of the null at <paramref name="depth"/> (pending, from <c>ldnull</c>) for
    /// <paramref name="successors"/>: a zero of the type each block takes there, and while a block has seen only null
    /// there, also a zero ref in the ref slot, for an object that may still come (when the method has objects at all).
    /// Null when the value at the depth is not a pending null.
    /// </summary>
    private List<(TirSlot Slot, TirValue Value)>? NullStores(int depth, IEnumerable<IlBlock> successors)
    {
        if (!_stack[depth].IsNull)
        {
            return null;
        }

        var types = new HashSet<TirType>();
        foreach (var successor in successors)
        {
            types.Add(successor.EntryStack![depth]);
            if (_nullEntries.Contains((successor, depth)) && MayHoldObjects)
            {
                types.Add(TirType.Ref);
            }
        }

        return [.. types.Select(type => (StackSlot(depth, type), (TirValue)_ir.Zero(type)))];
    }

    /// <summary>True when the method has objects anywhere: in its signature, its locals, or what its instructions use.</summary>
    private bool MayHoldObjects => _mayHoldObjects ??= FindObjects();

    private bool FindObjects()
    {
        bool IsObject(IlType type) => ObjectTypes.TryMapType(type, _method.Assembly, out _);
        return IsObject(_method.ReturnType) || _method.Parameters.Any(parameter => IsObject(parameter.Type))
            || (!_method.IsStatic && ObjectTypes.IsClass(_method.DeclaringType))
            || _body.Locals.Any(local => IsObject(local.Type))
            || _body.Instructions.Any(instruction => ClassRules.Allocates(instruction) || instruction.Operand switch
            {
                IlFieldOperand { Field: var field } => IsObject(field.FieldType),
                IlMethodOperand { Method: var method } => IsObject(method.ReturnType),
                _ => false,
            });
    }
}
