using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// Boxing in generic code over values (see <see cref="Boxing"/>), folded where the type argument says what the box
/// would do, as .NET's JIT folds it, so no box is made:
/// <list type="bullet">
/// <item><c>box T</c>, <c>isinst X</c> and a test of the result against null (<c>brtrue</c>, <c>brfalse</c>, or
/// <c>ldnull</c> and <c>cgt.un</c> or <c>ceq</c>), C#'s <c>value is IComparable</c> and the first half of
/// <c>value is int n</c>: true or false, as a box of T is an X or not (an interface it implements, object, or exactly
/// the value type X);</item>
/// <item><c>box T</c> and <c>unbox.any X</c> where a box of T unboxes to X (the same type, or an enum and its
/// underlying type), the other half of <c>value is int n</c> and C#'s <c>(int)(object)value</c>: nothing, the value
/// stays.</item>
/// </list>
/// A box whose object is kept or called through (<c>if (value is IDevice d) d.Tick();</c>) is a real box, as in .NET; a
/// nullable T is boxed as it is (null without a value).
/// </summary>
internal static partial class Specialization
{
    /// <summary>Folds a box at <paramref name="index"/> (see the summary); true when it did.</summary>
    private static bool RewriteBox(IlAssembly owner, List<IlInstruction> instructions, int index, HashSet<int> targets)
    {
        if (instructions[index] is not { OpCode: IlOpCode.Box, Operand: IlTypeOperand { Type: var type } }
            || Substitution.IsOpen(type) || Nullables.IsNullable(type) || Boxing.Of(type, owner) is not { } boxed
            || index + 1 >= instructions.Count)
        {
            return false;
        }

        IlInstruction? At(int i) => i < instructions.Count ? instructions[i] : null;
        switch (At(index + 1))
        {
            case { OpCode: IlOpCode.Isinst, Operand: IlTypeOperand { Type: var tested } } when IsBoxTest(boxed, tested, owner) is { } passes:
                if (At(index + 2)?.OpCode is IlOpCode.Brtrue or IlOpCode.Brtrue_S or IlOpCode.Brfalse or IlOpCode.Brfalse_S)
                {
                    return Replaced(instructions, index, 2, [(IlOpCode.Pop, null), (passes ? IlOpCode.Ldc_I4_1 : IlOpCode.Ldc_I4_0, null)],
                        targets);
                }

                if (At(index + 2)?.OpCode == IlOpCode.Ldnull && At(index + 3)?.OpCode is IlOpCode.Cgt_Un or IlOpCode.Ceq)
                {
                    var result = passes == (At(index + 3)!.OpCode == IlOpCode.Cgt_Un);
                    return Replaced(instructions, index, 4, [(IlOpCode.Pop, null), (result ? IlOpCode.Ldc_I4_1 : IlOpCode.Ldc_I4_0, null)],
                        targets);
                }

                return false;

            case { OpCode: IlOpCode.Unbox_Any, Operand: IlTypeOperand { Type: var unboxed } }
                when !Nullables.IsNullable(unboxed) && Boxing.Of(unboxed, owner) is { } target && Boxing.Unboxes(target, boxed):
                return Replaced(instructions, index, 2, [], targets);

            default:
                return false;
        }
    }

    /// <summary>
    /// Whether a box of <paramref name="boxed"/> passes <c>isinst <paramref name="tested"/></c>: object, System.ValueType
    /// and the interfaces it implements, or exactly its own value type (a nullable of it too); null when not known here.
    /// </summary>
    private static bool? IsBoxTest(BoxedType boxed, IlType tested, IlAssembly owner)
    {
        if (Substitution.IsOpen(tested))
        {
            return null;
        }

        if (Boxing.Of(tested, owner) is { } value)
        {
            return value == boxed;
        }

        var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(tested));
        return Boxing.Fits(boxed, plain) ? true
            : VirtualMethods.IsInterface(plain, owner) || ObjectTypes.Class(plain, owner) is not null || plain is IlArrayType
                || FixedText.IsText(plain) ? false
            : null;
    }
}
