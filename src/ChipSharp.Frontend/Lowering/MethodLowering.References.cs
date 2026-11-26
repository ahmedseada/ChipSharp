using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// References (C#'s <c>ref</c>, <c>out</c> and <c>in</c>, ref locals and ref returns, and a struct method's
/// <c>this</c>): on the evaluation stack a reference is a ptr, the address of what it refers to.
/// <list type="bullet">
/// <item><c>ldloca</c> and <c>ldarga</c> take the address of a local's or an argument's slot (an argument whose
/// address is taken gets a slot, like one the method assigns to); <c>ldsflda</c> takes a static field's global's.</item>
/// <item><c>ldind.*</c> and <c>stind.*</c> load and store through a reference, widened and narrowed like a local of
/// their type.</item>
/// <item>A reference is passed, returned and kept in a local as it is. Nothing turns it into a number or does
/// arithmetic on it: that is unsafe code, rejected with an error.</item>
/// </list>
/// ldind.u1 and stind.i1 read and write a byte, which is a bool or a (s)byte: what the reference refers to says which.
/// That is known from the instruction that made the address (addr, fieldaddr) or from the IL type it was loaded with
/// (a <c>ref bool</c> parameter, local or return), and it travels with a reference that waits in a stack slot.
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>What each reference loaded from a parameter, local, field or call refers to, by its IL type.</summary>
    private readonly Dictionary<TirValue, TirType> _referenceTargets = [];

    /// <summary>
    /// What a reference coming into a block in a stack slot refers to, by block and stack slot; null when the ways into
    /// the block do not agree (or do not know). Inside the block it may move to another depth (dup), with its slot.
    /// </summary>
    private readonly Dictionary<(IlBlock Block, TirSlot Slot), TirType?> _enteringTargets = [];

    private void LowerReference(IlInstruction instruction)
    {
        switch (instruction.OpCode)
        {
            case IlOpCode.Ldloca_S or IlOpCode.Ldloca:
                Push(_ir.AddressOf(_localSlots[Local(instruction.LocalIndex!.Value).Index]));
                break;

            case IlOpCode.Ldarga_S or IlOpCode.Ldarga:
                LoadArgumentAddress(instruction.ArgumentIndex!.Value);
                break;

            case IlOpCode.Ldsflda:
                Push(_ir.AddressOf(_program.StaticField(_method, instruction).Global));
                break;

            case IlOpCode.Ldind_I1 or IlOpCode.Ldind_U1:
                LoadIndirect(TirType.I8, instruction.OpCode == IlOpCode.Ldind_I1 ? IlType.Int8 : IlType.UInt8);
                break;

            case IlOpCode.Ldind_I2 or IlOpCode.Ldind_U2:
                LoadIndirect(TirType.I16, instruction.OpCode == IlOpCode.Ldind_I2 ? IlType.Int16 : IlType.UInt16);
                break;

            case IlOpCode.Ldind_I4 or IlOpCode.Ldind_U4:
                LoadIndirect(TirType.I32, instruction.OpCode == IlOpCode.Ldind_I4 ? IlType.Int32 : IlType.UInt32);
                break;

            case IlOpCode.Ldind_I8:
                LoadIndirect(TirType.I64, IlType.Int64);
                break;

            case IlOpCode.Ldind_I:
                LoadIndirect(TirType.NativeInt, IlType.NativeInt);
                break;

            case IlOpCode.Stind_I1:
                StoreIndirect(TirType.I8, IlType.Int8);
                break;

            case IlOpCode.Stind_I2:
                StoreIndirect(TirType.I16, IlType.Int16);
                break;

            case IlOpCode.Stind_I4:
                StoreIndirect(TirType.I32, IlType.Int32);
                break;

            case IlOpCode.Stind_I8:
                StoreIndirect(TirType.I64, IlType.Int64);
                break;

            case IlOpCode.Stind_I:
                StoreIndirect(TirType.NativeInt, IlType.NativeInt);
                break;

            default:
                throw Unsupported(instruction);
        }
    }

    /// <summary>ldarga: the address of the argument's slot (the constructor gave it one).</summary>
    private void LoadArgumentAddress(int index)
    {
        var argument = Argument(index);
        if (_argumentSlots[index] is not { } slot || argument.Parameter!.Type == TirType.Ptr)
        {
            throw Error($"{_instruction.Name} takes the address of {argument.Name}, which is a reference itself");
        }

        Push(_ir.AddressOf(slot));
    }

    /// <summary>
    /// ldind: reads a <paramref name="type"/> (or a bool, for a byte that is one) and widens it as
    /// <paramref name="ilType"/>.
    /// </summary>
    private void LoadIndirect(TirType type, IlType ilType)
    {
        var (address, target) = PopReference();
        (type, ilType) = Access(type, ilType, target);
        Push(ToStack(_ir.Load(type, address), ilType));
    }

    /// <summary>stind: narrows the value to a <paramref name="type"/> (or a bool) and writes it.</summary>
    private void StoreIndirect(TirType type, IlType ilType)
    {
        var value = PopValue();
        var (address, target) = PopReference();
        (type, ilType) = Access(type, ilType, target);
        _ir.Store(address, FromStack(value, type, ilType, "the value it refers to"));
    }

    /// <summary>
    /// What an ldind or stind of <paramref name="type"/> reads or writes when the reference refers to
    /// <paramref name="target"/> (null when not known): a byte is a bool when the target is one.
    /// </summary>
    private (TirType Type, IlType IlType) Access(TirType type, IlType ilType, TirType? target)
    {
        if (type == TirType.I8 && target == TirType.Bool)
        {
            return (TirType.Bool, IlType.Bool);
        }

        if (target is not null && target != type)
        {
            throw Error($"{_instruction.Name} reads or writes {type} through a reference to {target}");
        }

        if (type == TirType.I8 && target is null)
        {
            throw Error($"{_instruction.Name} reads or writes a byte through a reference that may refer to a bool " +
                "or to a byte, and nothing tells which");
        }

        return (type, ilType);
    }

    /// <summary>Pops a reference: its address, and what it refers to when that is known.</summary>
    private (TirValue Address, TirType? Target) PopReference()
    {
        var reference = PopValue();
        if (reference.Type != TirType.Ptr)
        {
            throw Error($"{_instruction.Name} needs a reference, but the evaluation stack has {StackName(reference.Type)}");
        }

        return (Materialize(reference), TargetOf(reference));
    }

    /// <summary>
    /// What a reference on the stack refers to: the slot, global or field whose address it is, the IL type it was loaded
    /// with, or what came into the block with it. Null when not known.
    /// </summary>
    private TirType? TargetOf(StackValue reference) => reference switch
    {
        { Value: TirSlotAddress address } => address.Slot.Type,
        { Value: TirGlobalAddress address } => address.Global.Type,
        { Value: TirFieldAddress address } => address.Field?.Type,
        { Value: TirElementAddress address } => address.ElementType,
        { Value: { } value } => _referenceTargets.GetValueOrDefault(value),
        { Slot: { } slot } => _enteringTargets.GetValueOrDefault((_block, slot)),
        _ => null,
    };

    /// <summary>
    /// A value loaded from a parameter, local, field or call of IL type <paramref name="type"/>, a ptr, onto the stack;
    /// for a reference, what it refers to is remembered.
    /// </summary>
    private StackValue ReferenceToStack(TirValue value, IlType type)
    {
        if (ValueTypes.WithoutModifiers(type) is IlByRefType reference
            && ValueTypes.TryMapType(reference.ElementType, _method.Assembly, out var target))
        {
            _referenceTargets[value] = target;
        }

        return StackValue.Of(value);
    }

    /// <summary>
    /// For <see cref="Leave"/>: what each reference on the stack refers to, for the blocks it goes to in a stack slot.
    /// When two ways into a block disagree, that block does not know.
    /// </summary>
    private void PassReferenceTargets(IEnumerable<IlBlock> successors)
    {
        for (var depth = 0; depth < _stack.Count; depth++)
        {
            if (_stack[depth].Type != TirType.Ptr)
            {
                continue;
            }

            var (slot, target) = (StackSlot(depth, TirType.Ptr), TargetOf(_stack[depth]));
            foreach (var successor in successors)
            {
                _enteringTargets[(successor, slot)] =
                    _enteringTargets.TryGetValue((successor, slot), out var known) && known != target ? null : target;
            }
        }
    }

    /// <summary>
    /// A reference or a struct, for a location of TIR type <paramref name="type"/>, is passed as it is: it must be of
    /// that type. Null when both are numbers, which <see cref="FromStack"/> converts as usual.
    /// </summary>
    private TirValue? ReferenceOrStructFromStack(StackValue value, TirType type, IlType ilType, string destination)
    {
        if (!IsReferenceOrStruct(type) && !IsReferenceOrStruct(value.Type))
        {
            return null;
        }

        if (value.Type != type)
        {
            throw Error($"{destination} has type {ilType}, but the value on the evaluation stack is " +
                $"{StackName(value.Type)}{UnsafeHint(value.Type)}");
        }

        return Materialize(value);
    }

    /// <summary>
    /// Checks that an operand of arithmetic, a comparison, a branch or a conversion is a number, not a reference or a
    /// struct.
    /// </summary>
    private void CheckNumber(StackValue value)
    {
        if (IsReferenceOrStruct(value.Type))
        {
            throw Error($"{_instruction.Name} works on numbers, but the evaluation stack has {StackName(value.Type)}" +
                UnsafeHint(value.Type));
        }
    }

    private static bool IsReferenceOrStruct(TirType type) => type == TirType.Ptr || type.IsAggregate;

    /// <summary>A stack type as errors name it: int32, int64, native int, "a reference (&amp;)" or "a Program.Point".</summary>
    private static string StackName(TirType type) =>
        type == TirType.Ptr ? "a reference (&)" : type.IsAggregate ? $"a {type.Name}" : LoweringErrors.StackTypeName(type);

    private static string UnsafeHint(TirType type) => type == TirType.Ptr
        ? $" (turning a reference into a number is unsafe code, which {SupportedSubset.Milestone} does not compile)"
        : "";
}
