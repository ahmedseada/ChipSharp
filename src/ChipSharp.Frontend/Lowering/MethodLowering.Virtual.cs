using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Virtual calls, calls through interfaces and type tests (see <see cref="VirtualMethods"/> and
/// <see cref="VirtualTables"/>):
/// <list type="bullet">
/// <item>A <c>callvirt</c> through a slot (a virtual method, an interface's, object's Equals or GetHashCode) is the null
/// check of the object, then a call of what the slot runs. When only one method can run there, on every object of a
/// class the program makes that the object's variable can hold (its type is known from the instruction that loaded it,
/// <see cref="VirtualMethods.StackType"/>: a local, a parameter, a field, a call's result, a <c>new</c>, a cast), it is
/// a plain call of that method: devirtualized, as a call on a sealed class or of a sealed override is. When none can
/// (no such object is ever made, so it is null) the null check is all there is. Else the method's address is loaded
/// from the object's virtual method table (the descriptor its header points to: <c>typeof</c>, the slot's element of
/// <c>virtuals</c>) or from the interface's method table in its interface map (<c>ChipSharp.Object.FindInterface</c>),
/// and called: <c>call indirect</c>; object's Equals and GetHashCode go through a function that knows arrays, which
/// have no table.</item>
/// <item><c>isinst</c> (C#'s <c>is</c> and <c>as</c>) and <c>castclass</c> (<c>(T)x</c>) test the object's class (and
/// its bases), its interface map or its array type against the type's descriptor
/// (<see cref="ObjectFunctions.TypeTest"/>). A test against object, or against a type the object's variable already
/// is, is the object itself; an <c>is</c> or <c>as</c> that no object the program makes can pass is null. A cast that
/// fails stops the program with .NET's InvalidCastException message.</item>
/// <item>A <c>call</c> of object's own Equals or GetHashCode (C#'s <c>base.Equals(other)</c>) calls ChipSharp's.</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>True for the instructions <see cref="LowerVirtualInstruction"/> translates.</summary>
    private bool IsVirtualInstruction(IlInstruction instruction) => instruction switch
    {
        { OpCode: IlOpCode.Isinst or IlOpCode.Castclass } => true,
        { OpCode: IlOpCode.Callvirt } => VirtualMethods.CalledSlot(_body.Instructions, _body.IndexOf(instruction.Offset)) is not null,
        { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: var callee } } => VirtualMethods.ObjectSlotOf(callee) is not null,
        _ => false,
    };

    private void LowerVirtualInstruction(IlInstruction instruction)
    {
        switch (instruction.OpCode)
        {
            case IlOpCode.Isinst or IlOpCode.Castclass:
                TypeTest(((IlTypeOperand)instruction.Operand!).Type, cast: instruction.OpCode == IlOpCode.Castclass);
                break;

            case IlOpCode.Callvirt:
                CallThroughSlot(VirtualMethods.CalledSlot(_body.Instructions, _body.IndexOf(instruction.Offset))!);
                break;

            default:
                CallObjectMethod(VirtualMethods.ObjectSlotOf(((IlMethodOperand)instruction.Operand!).Method)!);
                break;
        }
    }

    /// <summary>The IL type of the value <paramref name="depth"/> deep on the stack before the instruction being translated.</summary>
    private IlType? StackType(int depth) => VirtualMethods.StackType(_method, _body.IndexOf(_instruction.Offset), depth);

    /// <summary>A <c>callvirt</c> through <paramref name="slot"/>: the null check, then what the slot runs on the object.</summary>
    private void CallThroughSlot(DispatchSlot slot)
    {
        var callee = ((IlMethodOperand)_instruction.Operand!).Method;
        var signature = slot.Signature;
        var name = slot.ToString();
        var values = new StackValue[signature.ParameterTypes.Count];
        for (var i = values.Length - 1; i >= 0; i--)
        {
            values[i] = PopValue();
        }

        var receiverType = StackType(values.Length) ?? callee.DeclaringType;
        var receiver = NullCheck(ObjectFromStack(PopValue(), $"the object {name} is called on"));
        var arguments = new List<TirValue> { receiver };
        for (var i = 0; i < values.Length; i++)
        {
            var type = signature.ParameterTypes[i];
            arguments.Add(FromStack(values[i], MapType(type, $"parameter {i + 1} of {name}"), type, $"argument {i + 1} of {name}"));
        }

        var returnType = MapType(signature.ReturnType, $"the result of {name}");
        var targets = _program.VirtualTables.Dispatch.Targets(slot, receiverType);
        TirValue? result = slot.Kind == DispatchSlotKind.Object && StringMethods.MayBeString(receiverType)
            ? ObjectCallOrString(slot, targets, arguments)
            : targets.Count switch
        {
            // no object that could be there is ever made: it is null, which the null check stops at
            0 => returnType == TirType.Void ? null : _ir.Zero(returnType),
            1 => _ir.Call(_program.Function(targets[0]), arguments),
            // object's Equals and GetHashCode: through a function that knows arrays, which have no virtual method table
            _ when slot.Kind == DispatchSlotKind.Object =>
                _ir.Call(_program.ObjectFunctions.VirtualObjectCall(slot.Name, SlotIndex(slot)), arguments),
            _ => _ir.CallIndirect(returnType, MethodAddress(slot, receiver), arguments),
        };

        if (result is TirInstruction { HasResult: true })
        {
            Push(ToStack(result, signature.ReturnType));
        }
    }

    /// <summary>
    /// The address of the method <paramref name="slot"/> runs on <paramref name="receiver"/> (not null): its element of
    /// the virtual method table of the object's class, or of the interface's method table in its interface map.
    /// </summary>
    private TirValue MethodAddress(DispatchSlot slot, TirValue receiver)
    {
        var type = _ir.TypeOf(receiver);
        TirValue table = slot.Kind == DispatchSlotKind.Interface
            ? _ir.Call(_program.ObjectFunctions.FindInterface, type, _ir.AddressOf(_program.InterfaceDescriptor(slot.Owner)))
            : _ir.FieldAddress(type, TirTypeDescriptor.Layout, TirTypeDescriptor.VirtualsField);
        return _ir.Load(TirType.Ptr, _ir.ElementAddress(table, TirType.Ptr, _ir.Const(TirType.I32, SlotIndex(slot))));
    }

    /// <summary>Where <paramref name="slot"/> is in the method tables (see <see cref="VirtualTables.IndexOf"/>).</summary>
    private int SlotIndex(DispatchSlot slot) => _program.VirtualTables.IndexOf(slot)
        ?? throw Error($"the call through {slot} has no place in a method table");

    /// <summary>
    /// <c>isinst</c> (<paramref name="cast"/> false) or <c>castclass</c> to <paramref name="target"/>: object, a class
    /// of the program, an interface or an array type (the whole-program pass rejects the others). An array comes out as
    /// the array value a variable of its type holds.
    /// </summary>
    private void TypeTest(IlType target, bool cast)
    {
        if (FixedText.IsText(StackArrays.Primitive(target)))
        {
            StringTypeTest(cast);
            return;
        }

        var known = StackType(0);
        var reference = ObjectFromStack(PopValue(), _instruction.Name);
        var assembly = _method.Assembly;
        var plain = ValueTypes.WithoutModifiers(target);
        var targetClass = ObjectTypes.Class(plain, assembly);
        var kind = plain is IlArrayType ? TirTypeDescriptorKind.Array
            : VirtualMethods.IsInterface(plain, assembly) ? TirTypeDescriptorKind.Interface
            : targetClass is not null || ObjectTypes.IsSystemObject(plain) ? TirTypeDescriptorKind.Class
            : throw Error($"{_instruction.Name} to {CSharpNames.Type(plain)} is not supported: ChipSharp tests objects " +
                "against object, the program's classes, the interfaces they implement and arrays");

        bool Passes(IlTypeDefinition type) =>
            targetClass is not null ? VirtualMethods.IsDerived(type, targetClass) : VirtualMethods.Implements(type, plain);

        // against object, or a type the variable already is: the object itself (null too)
        TirValue result;
        if (ObjectTypes.IsSystemObject(plain) || (known is not null && (known.Equals(plain)
            || (kind != TirTypeDescriptorKind.Array && ObjectTypes.Class(known, assembly) is { } knownClass && Passes(knownClass)))))
        {
            result = reference;
        }
        else if (!cast && kind != TirTypeDescriptorKind.Array
            && !_program.VirtualTables.Dispatch.Created.Any(type => Passes(type) && VirtualMethods.Fits(type, known)))
        {
            // no object the program makes can pass: as and is give null; a cast still stops at one that is there
            result = _ir.Zero(TirType.Ref);
        }
        else
        {
            var descriptor = kind switch
            {
                TirTypeDescriptorKind.Array => _program.HeapArrayDescriptor(plain, assembly),
                TirTypeDescriptorKind.Interface => _program.InterfaceDescriptor(plain),
                _ => _program.ClassDescriptor(targetClass!),
            };
            result = _ir.Call(_program.ObjectFunctions.TypeTest(kind, cast), reference, _ir.AddressOf(descriptor));
        }

        if (kind == TirTypeDescriptorKind.Array)
        {
            Push(ArrayToStack(result, plain));
        }
        else
        {
            PushObject(result);
        }
    }

    /// <summary>A <c>call</c> of object's own Equals or GetHashCode (<c>base.Equals(other)</c>): ChipSharp's, with no null check.</summary>
    private void CallObjectMethod(DispatchSlot slot)
    {
        var function = _program.Function(new DispatchTarget(null, slot.Name));
        var arguments = new TirValue[function.Parameters.Count];
        for (var i = arguments.Length - 1; i >= 0; i--)
        {
            arguments[i] = PopObject();
        }

        Push(ToStack(_ir.Call(function, arguments), slot.Signature.ReturnType));
    }

    /// <summary>The TIR type of a parameter or result type of a slot's method; an error that names it when there is none.</summary>
    private TirType MapType(IlType type, string what) => SupportedSubset.TryMapType(type, _method.Assembly, out var tirType)
        ? tirType
        : throw Error($"{what} has type {CSharpNames.Type(type)}, which is not supported");
}
