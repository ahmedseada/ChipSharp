using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Fixed text (see <see cref="FixedText"/>): on the evaluation stack a string is a ptr, the address of its literal's
/// record, or null.
/// <list type="bullet">
/// <item><c>ldstr</c> is the address of the literal's record, a constant global (<see cref="ProgramFunctions.Text"/>),
/// the same for equal literals; <c>ldsfld string.Empty</c> is the address of "".</item>
/// <item><c>ldnull</c> is <c>zero ptr</c>.</item>
/// <item><c>brtrue</c> and <c>brfalse</c> on a string test it against null (C#'s <c>s != null</c>, <c>s ?? "none"</c>),
/// and <c>ceq</c>, <c>cgt.un</c>, <c>beq</c>, <c>bne.un</c> compare two strings as addresses, by reference, as .NET
/// compares objects (C#'s <c>s == null</c>).</item>
/// </list>
/// A string is passed, returned and kept in a local or a static field as the ptr it is, like a reference.
/// </summary>
internal sealed partial class MethodLowering
{
    private void LowerText(IlInstruction instruction)
    {
        switch (instruction.OpCode)
        {
            case IlOpCode.Ldstr:
                Push(Text(((IlStringOperand)instruction.Operand!).Value));
                break;

            case IlOpCode.Ldnull:
                Push(_ir.Zero(TirType.Ptr));
                break;

            default:
                throw Unsupported(instruction);
        }
    }

    /// <summary>The address of the record of <paramref name="text"/>.</summary>
    private TirGlobalAddress Text(string text) => _ir.AddressOf(_program.Text(text));

    /// <summary>
    /// The two values a comparison or a compare-and-branch takes: numbers, as <see cref="PopOperands"/> pairs them, or
    /// two strings (or references), compared as addresses, which eq, ne and the unsigned comparisons do.
    /// </summary>
    private (TirValue Left, TirValue Right) PopComparands(TirCompareOp op)
    {
        if (_stack.Count < 2 || (_stack[^1].Type != TirType.Ptr && _stack[^2].Type != TirType.Ptr))
        {
            return PopOperands();
        }

        var right = PopValue();
        var left = PopValue();
        if (left.Type != right.Type || op is TirCompareOp.Lt or TirCompareOp.Le or TirCompareOp.Gt or TirCompareOp.Ge)
        {
            throw Error($"{_instruction.Name} compares {StackName(left.Type)} with {StackName(right.Type)}; " +
                "addresses compare only with each other, for equality or as unsigned numbers");
        }

        return (Materialize(left), Materialize(right));
    }

    /// <summary>brtrue (or brfalse, when not <paramref name="jumpWhenTrue"/>) on a string or a reference: is it not null?</summary>
    private void BranchOnAddress(StackValue address, bool jumpWhenTrue) =>
        Branch(_ir.Compare(jumpWhenTrue ? TirCompareOp.Ne : TirCompareOp.Eq, Materialize(address), _ir.Zero(TirType.Ptr)));
}
