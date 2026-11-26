using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// A value on IL's evaluation stack, of stack type i32, i64 or nint. Three kinds are not written as TIR until they
/// are used, so each use gets the form it needs:
/// <list type="bullet">
/// <item>a constant, written as a constant of the type it ends up as (e.g. <c>const i64</c> after <c>conv.i8</c>,
/// <c>const bool</c> for a bool);</item>
/// <item>an int32 that is a bool widened to 0 or 1: a branch, a bool local or a bool result takes the bool itself,
/// with no conversion and no compare;</item>
/// <item>a value that came into the block in a stack slot: it is loaded only if the block uses it, and a block that
/// passes it on untouched leaves the slot as it is.</item>
/// </list>
/// </summary>
internal readonly record struct StackValue
{
    private StackValue(TirType type, TirValue? value = null, long constant = 0, TirValue? condition = null,
        TirSlot? slot = null)
    {
        Type = type;
        Value = value;
        Constant = constant;
        Condition = condition;
        Slot = slot;
    }

    /// <summary>The stack type: i32, i64 or nint (only a conversion in progress has another).</summary>
    public TirType Type { get; }

    /// <summary>The TIR value, of <see cref="Type"/>; null while the value is pending.</summary>
    public TirValue? Value { get; }

    /// <summary>The value of a pending constant, sign-extended from its type's width.</summary>
    public long Constant { get; }

    /// <summary>The bool this int32 holds as 0 or 1; null for other values.</summary>
    public TirValue? Condition { get; }

    /// <summary>The stack slot the value waits in; null for other values.</summary>
    public TirSlot? Slot { get; }

    public bool IsConstant => Value is null && Condition is null && Slot is null;

    /// <summary>True for a reference to an object on the heap: a value of type ref (see MethodLowering.Objects.cs).</summary>
    public bool IsObject => Type == TirType.Ref;

    /// <summary>
    /// True for null as <c>ldnull</c> pushes it (<see cref="OfNull"/>): a pending ptr constant, which becomes a null ptr
    /// where text or a reference is wanted, and a null ref where an object is.
    /// </summary>
    public bool IsNull => IsConstant && Type == TirType.Ptr;

    /// <summary>Null, pending until a use says whether it is a null ptr (fixed text) or a null ref (an object).</summary>
    public static StackValue OfNull() => new(TirType.Ptr);

    public static StackValue Of(TirValue value) => new(value.Type, value);

    public static StackValue OfConstant(TirType type, long value) => new(type, constant: value);

    /// <summary>An int32 that is 1 when <paramref name="condition"/> (a bool) is true and 0 when it is false.</summary>
    public static StackValue OfBool(TirValue condition) => new(TirType.I32, condition: condition);

    /// <summary>The value in a stack slot at the start of a block (the slot is written only at the end of a block).</summary>
    public static StackValue InSlot(TirSlot slot) => new(slot.Type, slot: slot);
}
