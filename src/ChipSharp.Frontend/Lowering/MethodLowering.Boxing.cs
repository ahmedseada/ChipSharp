using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Boxing, unboxing and <c>Nullable&lt;T&gt;</c> (see <see cref="Boxing"/>, <see cref="Nullables"/> and
/// <see cref="BoxingFunctions"/>):
/// <list type="bullet">
/// <item><c>box T</c>: a new box with the value (<c>newobject</c> of T's box, a store into its field), known not to be
/// null; of a nullable, the box of its value or null; of a reference (generic code over objects), the object itself.</item>
/// <item><c>unbox.any T</c>: the value in a box of a type that unboxes to T, loaded from the address
/// <c>ChipSharp.Box.Unbox&lt;T&gt;</c> gives (<c>unbox T</c>: that address), which stops the program for null and for
/// any other object as .NET does; to a nullable, no value for null; to a reference, a cast.</item>
/// <item><c>isinst T</c> of a value type T: the box when it is one of exactly T, else null; null at once when the
/// program never boxes a T.</item>
/// <item><c>constrained. T</c> and the <c>callvirt</c> after it: what the call runs on a box of T, called on the address
/// the call is made on, with no box (a struct's own method, ChipSharp's ToString, Equals or GetHashCode of a value); on
/// a nullable, its ToString, Equals or GetHashCode.</item>
/// <item>The members of <c>Nullable&lt;T&gt;</c>, on its struct: <c>HasValue</c> and <c>GetValueOrDefault()</c> read a
/// field, the constructor writes both, <c>Value</c> stops the program with .NET's message when there is no value.</item>
/// <item><c>object.Equals(a, b)</c>: <c>ChipSharp.Object.EqualsOfTwo</c>.</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>True for the instructions <see cref="LowerBoxingInstruction"/> translates.</summary>
    private bool IsBoxingInstruction(IlInstruction instruction)
    {
        var instructions = _body.Instructions;
        var index = _body.IndexOf(instruction.Offset);
        return instruction switch
        {
            { OpCode: IlOpCode.Box or IlOpCode.Unbox or IlOpCode.Unbox_Any } => true,
            { OpCode: IlOpCode.Isinst, Operand: IlTypeOperand { Type: var type } } => Boxing.Of(type, _method.Assembly) is not null,
            { OpCode: IlOpCode.Constrained } => index + 1 < instructions.Count
                && Boxing.ConstrainedAt(instructions, index + 1, _method.Assembly) is not null,
            { OpCode: IlOpCode.Callvirt } => Boxing.ConstrainedAt(instructions, index, _method.Assembly) is not null,
            { OpCode: IlOpCode.Call or IlOpCode.Newobj, Operand: IlMethodOperand { Method: var callee } } =>
                Nullables.MemberOf(callee) is not null || Boxing.IsObjectEquals(instruction),
            _ => false,
        };
    }

    private void LowerBoxingInstruction(IlInstruction instruction)
    {
        switch (instruction.OpCode)
        {
            case IlOpCode.Box:
                BoxValue(((IlTypeOperand)instruction.Operand!).Type);
                break;

            case IlOpCode.Unbox_Any or IlOpCode.Unbox:
                UnboxValue(((IlTypeOperand)instruction.Operand!).Type, address: instruction.OpCode == IlOpCode.Unbox);
                break;

            case IlOpCode.Isinst:
                BoxTest(((IlTypeOperand)instruction.Operand!).Type);
                break;

            case IlOpCode.Constrained:
                // the callvirt after it does it all
                break;

            case IlOpCode.Callvirt:
                ConstrainedCall();
                break;

            case var _ when Boxing.IsObjectEquals(instruction):
                var b = PopObject();
                var a = PopObject();
                Push(StackValue.OfBool(_ir.Call(_program.Boxes.ObjectEquals, a, b)));
                break;

            default:
                NullableCall(Nullables.MemberOf(((IlMethodOperand)instruction.Operand!).Method)!.Value);
                break;
        }
    }

    /// <summary><c>box</c> of a value of <paramref name="type"/> (see the summary).</summary>
    private void BoxValue(IlType type)
    {
        var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(type));
        if (Boxing.Of(plain, _method.Assembly) is not { } boxed)
        {
            if (IsReferenceType(plain))
            {
                return; // an object is one already
            }

            throw Error($"box of {CSharpNames.Type(plain, _method)} is not supported: ChipSharp boxes numbers, bools, " +
                "chars, enums and the program's structs");
        }

        if (Nullables.IsNullable(plain))
        {
            var nullable = MapType(plain, "the nullable boxed");
            var slot = TemporarySlot(nullable);
            _ir.Store(slot, FromStack(PopValue(), nullable, plain, "the nullable boxed"));
            PushObject(_ir.Call(_program.Boxes.BoxNullable(boxed), _ir.AddressOf(slot)));
            return;
        }

        var made = _program.Boxes.Box(_ir, boxed, FromStack(PopValue(), boxed.ValueType, plain, "the value boxed"));
        _notNull.Add(made);
        PushObject(made);
    }

    /// <summary><c>unbox.any</c> (or <c>unbox</c>, <paramref name="address"/>) to <paramref name="type"/>.</summary>
    private void UnboxValue(IlType type, bool address)
    {
        var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(type));
        if (IsReferenceType(plain) && !address)
        {
            TypeTest(plain, cast: true); // (T)o of a reference type: a cast
            return;
        }

        if (Boxing.Of(plain, _method.Assembly) is not { } boxed || (address && Nullables.IsNullable(plain)))
        {
            throw Error($"{_instruction.Name} to {CSharpNames.Type(plain, _method)} is not supported: ChipSharp unboxes " +
                "numbers, bools, chars, enums, the program's structs and nullables of them");
        }

        var value = ObjectFromStack(PopValue(), _instruction.Name);
        if (Nullables.IsNullable(plain))
        {
            Push(ToStack(_ir.Call(_program.Boxes.UnboxNullable(boxed, plain), value), plain));
            return;
        }

        var pointer = _ir.Call(_program.Boxes.Unbox(boxed), value);
        if (address)
        {
            _referenceTargets[pointer] = boxed.ValueType;
            Push(pointer);
            return;
        }

        Push(ToStack(_ir.Load(boxed.ValueType, pointer), plain));
    }

    /// <summary><c>isinst</c> of a value type (or a nullable of one): the box when it is one of exactly the type, else null.</summary>
    private void BoxTest(IlType type)
    {
        var boxed = Boxing.Of(type, _method.Assembly)!;
        var value = ObjectFromStack(PopValue(), _instruction.Name);
        PushObject(_program.VirtualTables.Dispatch.Boxed.Contains(boxed)
            ? _ir.Call(_program.Boxes.As(boxed), value)
            : _ir.Zero(TirType.Ref));
    }

    /// <summary>The <c>callvirt</c> after <c>constrained. T</c> of a value type: what it runs on the value, on its address.</summary>
    private void ConstrainedCall()
    {
        var index = _body.IndexOf(_instruction.Offset);
        var (boxed, target) = Boxing.ConstrainedAt(_body.Instructions, index, _method.Assembly)!.Value;
        var type = ((IlTypeOperand)_body.Instructions[index - 1].Operand!).Type;
        var callee = ((IlMethodOperand)_instruction.Operand!).Method;
        var signature = VirtualMethods.SlotCalled(callee)!.Signature;
        var boxes = _program.Boxes;
        var function = Nullables.IsNullable(type)
            ? callee.Name switch
            {
                "ToString" => boxes.NullableToString(boxed),
                "GetHashCode" => boxes.NullableHashCode(boxed),
                _ => boxes.NullableEquals(boxed),
            }
            : target.Method is { } method ? _program.Function(DispatchTarget.Of(method))
            : target.ObjectMethod switch
            {
                "ToString" => boxes.ToStringOf(boxed),
                "Equals" => boxes.EqualsOf(boxed),
                "GetHashCode" => boxes.HashCodeOf(boxed),
                _ => boxes.CompareToOf(boxed),
            };

        var values = new StackValue[signature.ParameterTypes.Count];
        for (var i = values.Length - 1; i >= 0; i--)
        {
            values[i] = PopValue();
        }

        var (receiver, _) = PopReference();
        var arguments = new List<TirValue> { receiver };
        for (var i = 0; i < values.Length; i++)
        {
            arguments.Add(FromStack(values[i], function.Parameters[i + 1].Type, signature.ParameterTypes[i],
                $"argument {i + 1} of {callee.Name}"));
        }

        var call = _ir.Call(function, arguments);
        if (call.HasResult)
        {
            Push(ToStack(call, signature.ReturnType));
        }
    }

    /// <summary>A call of a member of <c>Nullable&lt;T&gt;</c> (see <see cref="NullableMember"/>), on its struct.</summary>
    private void NullableCall(NullableMember member)
    {
        var callee = ((IlMethodOperand)_instruction.Operand!).Method;
        var nullableType = ValueTypes.WithoutModifiers(callee.DeclaringType);
        var nullable = (TirStructType)MapType(nullableType, $"the {CSharpNames.Type(nullableType)}");
        var valueType = Nullables.ValueOf(nullableType)!;
        var valueTir = nullable.Fields[Nullables.ValueField].Type;
        var boxes = _program.Boxes;
        TirValue ValueOf(StackValue value) => FromStack(value, valueTir, valueType, $"the value of a {nullable.Name}");

        switch (member)
        {
            case NullableMember.Constructor or NullableMember.Implicit when _instruction.OpCode != IlOpCode.Call || member == NullableMember.Implicit:
                // newobj T?(value), (T?)value: the nullable as a value
                var made = ValueOf(PopValue());
                var slot = TemporarySlot(nullable);
                SetNullable(_ir.AddressOf(slot), nullable, made);
                Push(_ir.Load(slot));
                break;

            case NullableMember.Constructor:
                var value = ValueOf(PopValue());
                SetNullable(PopAddress(), nullable, value);
                break;

            case NullableMember.HasValue:
                Push(ToStack(_ir.Load(TirType.Bool, _ir.FieldAddress(PopAddress(), nullable, Nullables.HasValueField)), IlType.Bool));
                break;

            case NullableMember.GetValueOrDefault:
                Push(ToStack(_ir.Load(valueTir, _ir.FieldAddress(PopAddress(), nullable, Nullables.ValueField)), valueType));
                break;

            case NullableMember.GetValueOrDefaultOr:
                var other = ValueOf(PopValue());
                Push(ToStack(_ir.Call(boxes.NullableValueOr(nullable), PopAddress(), other), valueType));
                break;

            case NullableMember.Value:
                Push(ToStack(_ir.Call(boxes.NullableValue(nullable), PopAddress()), valueType));
                break;

            case NullableMember.Explicit:
                var copy = TemporarySlot(nullable);
                _ir.Store(copy, FromStack(PopValue(), nullable, nullableType, "the nullable"));
                Push(ToStack(_ir.Call(boxes.NullableValue(nullable), _ir.AddressOf(copy)), valueType));
                break;

            default:
                var boxed = Boxing.Of(nullableType, _method.Assembly)
                    ?? throw Error($"{callee.Name} of {CSharpNames.Type(nullableType)} is not supported: its value cannot be boxed");
                var arguments = new List<TirValue>();
                if (member == NullableMember.EqualsObject)
                {
                    arguments.Add(ObjectFromStack(PopValue(), "argument 1 of Equals"));
                }

                arguments.Insert(0, PopAddress());
                var function = member switch
                {
                    NullableMember.Text => boxes.NullableToString(boxed),
                    NullableMember.HashCode => boxes.NullableHashCode(boxed),
                    _ => boxes.NullableEquals(boxed),
                };
                Push(ToStack(_ir.Call(function, arguments), callee.ReturnType));
                break;
        }
    }

    /// <summary>Writes a nullable with <paramref name="value"/> at <paramref name="address"/>.</summary>
    private void SetNullable(TirValue address, TirStructType nullable, TirValue value)
    {
        _ir.Store(_ir.FieldAddress(address, nullable, Nullables.HasValueField), _ir.Const(true));
        _ir.Store(_ir.FieldAddress(address, nullable, Nullables.ValueField), value);
    }

    /// <summary>True for a reference type, which boxing leaves as it is: an object, text, an array.</summary>
    private bool IsReferenceType(IlType type) =>
        ObjectTypes.TryMapType(type, _method.Assembly, out _) || FixedText.IsText(type) || type is IlArrayType;
}
