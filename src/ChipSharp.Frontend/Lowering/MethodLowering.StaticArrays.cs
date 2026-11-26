using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Arrays in static fields (see <see cref="StaticArrays"/>). Reading one is the array value of its global, which every
/// other array instruction takes as it takes an array on the stack; what makes it in the type initializer is not
/// translated, as the global holds the result from the start.
/// <code>
/// %0 = addr @Program.Digits                               ldsfld uint8[] Program::Digits
/// %1 = call ChipSharp.Array ChipSharp.Array.Make(%0, 10)
/// </code>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>True for an instruction at <paramref name="index"/> that makes a static array in a type initializer.</summary>
    private bool IsFoldedStaticArray(int index) => _program.StaticArrays.IsFolded(_method, index);

    /// <summary>ldsfld of a static array: pushes its array value, and returns true; false for any other field.</summary>
    private bool LoadStaticArray()
    {
        if (((IlFieldOperand)_instruction.Operand!).Field.Definition is not { } field
            || !_program.StaticArrays.TryGet(field, out var array))
        {
            return false;
        }

        Push(array.Length == 0
            ? _ir.Zero(StackArrays.ArrayType)
            : _ir.Call(_program.Arrays.Make, _ir.AddressOf(_program.StaticArray(array)),
                _ir.Const(TirType.I32, array.Length)));
        return true;
    }
}
