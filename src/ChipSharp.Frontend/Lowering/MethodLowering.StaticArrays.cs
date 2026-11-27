using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Arrays in static fields (see <see cref="StaticArrays"/>). Reading one is the array value of its global, which every
/// other array instruction takes as it takes an array on the stack; what makes it in the type initializer is not
/// translated, as the global holds the result from the start. An array whose elements the initializer computes is
/// set when the program starts: its newarr is the global's array value, the element stores are translated as any,
/// and the stsfld only takes the array off the stack.
/// <code>
/// %0 = addr @Program.Digits                               ldsfld uint8[] Program::Digits
/// %1 = call ChipSharp.Array ChipSharp.Array.Make(%0, 10)
/// </code>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>True for an instruction at <paramref name="index"/> that makes a static array in a type initializer.</summary>
    private bool IsFoldedStaticArray(int index) => _program.StaticArrays.IsFolded(_method, index);

    /// <summary>
    /// The newarr and the stsfld of a static array set when the program starts (see
    /// <see cref="StaticArray.IsSetAtStart"/>): pushes the array value of its global, or takes it off the stack; returns
    /// true. False for any other instruction.
    /// </summary>
    private bool LowerStaticArrayMadeAtStart(int index)
    {
        if (_program.StaticArrays.IsMadeAtStart(_method, index, out var array))
        {
            PushStaticArray(array);
            return true;
        }

        if (_program.StaticArrays.IsStoredAtStart(_method, index))
        {
            PopArray();
            return true;
        }

        return false;
    }

    /// <summary>ldsfld of a static array: pushes its array value, and returns true; false for any other field.</summary>
    private bool LoadStaticArray()
    {
        if (((IlFieldOperand)_instruction.Operand!).Field.Definition is not { } field
            || !_program.StaticArrays.TryGet(field, out var array))
        {
            return false;
        }

        PushStaticArray(array);
        return true;
    }

    private void PushStaticArray(StaticArray array)
    {
        var type = StaticFields.Type(array.Field);
        PushArray(array.Length == 0
            ? EmptyArray(type)
            : _ir.Call(_program.Arrays.Make, _ir.AddressOf(_program.StaticArray(array)),
                _ir.Const(TirType.I32, array.Length)), type);
    }
}
