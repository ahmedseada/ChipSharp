using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Objects on the heap (<see cref="ObjectTypes"/> says how types map to TIR, <see cref="TirTypeDescriptor"/> how objects
/// are laid out): the helpers every translation of objects uses. Classes, virtual calls, interfaces, type tests and
/// arrays on the heap build on them as they are; one that needs more adds a helper here rather than reshaping these.
/// <list type="bullet">
/// <item>On the evaluation stack an object is a value of type ref (<see cref="StackValue.IsObject"/>): what
/// <see cref="NewObject"/> made, or a load of a local, parameter, field or element of a reference type, or a call's
/// result. It goes into a local, a parameter, a field and a return value as it is (<see cref="FromStack"/> takes it, and
/// <see cref="ObjectFromStack"/> is that rule). <see cref="PushObject"/> pushes one; <see cref="PopObject"/> pops one,
/// <see cref="PopObjectNotNull"/> with its null check.</item>
/// <item>Null, as <c>ldnull</c> pushes it, is pending (<see cref="StackValue.IsNull"/>): where an object is wanted it is
/// <c>zero ref</c>, where text is <c>zero ptr</c>, as before. A null that goes through a stack slot to another block is
/// a ptr there, so a block that one way reaches with null and another with an object (C#'s <c>c ? x : null</c>) is not
/// translated yet.</item>
/// <item><c>brtrue</c> and <c>brfalse</c> test an object against null; <c>ceq</c>, <c>cgt.un</c>, <c>beq</c> and
/// <c>bne.un</c> compare two objects, or an object and null, by reference (<see cref="PopObjectComparands"/>).</item>
/// <item><see cref="NullCheck"/> comes before every use of an object's fields, elements, header or methods, as .NET checks
/// them: <c>nullcheck</c>, once per value in a block, and never for what <see cref="NewObject"/> just made.
/// <see cref="ObjectFieldAddress"/> is a field's address with its check.</item>
/// <item><see cref="ProgramFunctions.ClassDescriptor"/>, <see cref="ProgramFunctions.ObjectDescriptor"/> and
/// <see cref="ProgramFunctions.ArrayDescriptor"/> give the descriptors, made once per module.</item>
/// </list>
/// What this file translates by itself is <c>new object()</c> (<c>newobj System.Object::.ctor()</c>: System.Object has
/// nothing to construct), which is how a program makes an object until its own classes compile. An interrupt handler
/// never makes one: the runtime stops the program there.
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>The refs known not to be null: checked already, or just made.</summary>
    private readonly HashSet<TirValue> _notNull = [];

    /// <summary>True for the instructions <see cref="LowerObjectInstruction"/> translates: <c>new object()</c>.</summary>
    private static bool IsObjectInstruction(IlInstruction instruction) => instruction is
    {
        OpCode: IlOpCode.Newobj,
        Operand: IlMethodOperand { Method: { Name: ".ctor", Definition: null, ParameterTypes.Count: 0 } constructor },
    } && ObjectTypes.IsSystemObject(constructor.DeclaringType);

    private void LowerObjectInstruction(IlInstruction instruction)
    {
        if (!IsObjectInstruction(instruction))
        {
            throw Unsupported(instruction);
        }

        PushObject(NewObject(_program.ObjectDescriptor));
    }

    /// <summary>A new object of the class <paramref name="type"/> (<c>newobject</c>), zeroed: known not to be null.</summary>
    private TirValue NewObject(TirTypeDescriptor type)
    {
        var made = _ir.NewObject(type);
        _notNull.Add(made);
        return made;
    }

    /// <summary>Pushes an object (a ref) onto the evaluation stack.</summary>
    private void PushObject(TirValue reference)
    {
        if (reference.Type != TirType.Ref)
        {
            throw new ArgumentException($"An object is a ref, not {reference.Type}.", nameof(reference));
        }

        Push(reference);
    }

    /// <summary>Pops an object, or null as a null ref; anything else is an error that names the instruction.</summary>
    private TirValue PopObject() => ObjectFromStack(PopValue(), _instruction.Name);

    /// <summary>Pops an object and checks that it is not null (see <see cref="NullCheck"/>).</summary>
    private TirValue PopObjectNotNull() => NullCheck(PopObject());

    /// <summary>
    /// Stops the program with .NET's NullReferenceException message when <paramref name="reference"/> is null:
    /// <c>nullcheck</c>, unless the value is known not to be null. Returns the reference, for what uses it next.
    /// </summary>
    private TirValue NullCheck(TirValue reference)
    {
        if (_notNull.Add(reference))
        {
            _ir.NullCheck(reference);
        }

        return reference;
    }

    /// <summary>The address of an instance field of a class in <paramref name="reference"/>'s object, after its null check.</summary>
    private TirFieldAddress ObjectFieldAddress(TirValue reference, IlField field)
    {
        var (objectType, index) = ObjectTypes.FieldOf(field);
        return _ir.FieldAddress(NullCheck(reference), objectType, index);
    }

    /// <summary>
    /// A stack value for a location that holds an object (named <paramref name="destination"/> in errors): an object as
    /// it is, null as <c>zero ref</c>.
    /// </summary>
    private TirValue ObjectFromStack(StackValue value, string destination) => value switch
    {
        { IsObject: true } => Materialize(value),
        { IsNull: true } => _ir.Zero(TirType.Ref),
        _ => throw Error($"{destination} takes an object, but the evaluation stack has {StackName(value.Type)}" +
            UnsafeHint(value.Type)),
    };

    /// <summary>
    /// For <see cref="PopComparands"/>: the two values when one of them is an object, the other an object or null,
    /// compared as addresses (equal, not equal, or unsigned, as C#'s <c>x != null</c> is); null when neither is an object.
    /// </summary>
    private (TirValue Left, TirValue Right)? PopObjectComparands(TirCompareOp op)
    {
        if (_stack.Count < 2 || (!_stack[^1].IsObject && !_stack[^2].IsObject))
        {
            return null;
        }

        var right = PopValue();
        var left = PopValue();
        if (op is TirCompareOp.Lt or TirCompareOp.Le or TirCompareOp.Gt or TirCompareOp.Ge
            || !(left.IsObject || left.IsNull) || !(right.IsObject || right.IsNull))
        {
            throw Error($"{_instruction.Name} compares {StackName(left.Type)} with {StackName(right.Type)}; an object " +
                "compares only with an object or null, for equality");
        }

        return (ObjectFromStack(left, _instruction.Name), ObjectFromStack(right, _instruction.Name));
    }
}
