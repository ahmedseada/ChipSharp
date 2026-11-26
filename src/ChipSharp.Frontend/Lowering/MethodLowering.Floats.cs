using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Floats and doubles, computed as .NET computes them, bit for bit.
/// <list type="bullet">
/// <item>ECMA-335 has one stack type F for both; the evaluation stack here keeps float32 (f32) and float64 (f64)
/// apart, as RyuJIT does, so that float arithmetic is done in float precision: <c>ldc.r4</c> and a float local,
/// parameter or result push an f32, and an operation on two f32 values gives an f32. A float met with a double
/// (which C# never writes: it converts first) is widened to double. A double stored, passed or returned as a float
/// is rounded to float.</item>
/// <item>The arithmetic, the comparisons and the branches that compare are translated as for integers, into TIR
/// operations on floats; the <c>.un</c> comparisons and branches are the unordered ones, true when either side is
/// NaN, which is what they mean on floats. <c>neg</c> is <c>-0.0 - x</c>, which flips the sign of zeros too.</item>
/// <item>A float becomes an integer as .NET 9 and later do it on every platform: rounded toward zero and saturated,
/// so a value beyond the type's range gives its smallest or largest value and NaN gives 0 (TIR's conversion does
/// that). For int8, uint8, int16 and uint16 RyuJIT converts to int32 first and keeps the low bits:
/// <c>(byte)300.5</c> is 44 and <c>(byte)1e10</c> is 255.</item>
/// <item><c>conv.r.un</c> reads an integer as unsigned and makes a double, except right before <c>conv.r4</c>:
/// RyuJIT makes that pair one conversion to float, rounded once (a ulong rounded to double first could round twice).</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>
    /// Translates <paramref name="instruction"/> when floats need something of their own: the float constants and
    /// conversions, <c>neg</c> and the conversions to integers of a float, and a float meeting a double. Returns false
    /// for everything else, which <see cref="Lower(IlInstruction)"/> translates as for integers.
    /// </summary>
    private bool LowerFloat(IlInstruction instruction)
    {
        var opCode = instruction.OpCode;
        switch (opCode)
        {
            case IlOpCode.Ldc_R4:
                Push(_ir.Const(TirType.F32, ((IlFloat32Operand)instruction.Operand!).Value));
                return true;

            case IlOpCode.Ldc_R8:
                Push(_ir.Const(TirType.F64, ((IlFloat64Operand)instruction.Operand!).Value));
                return true;

            case IlOpCode.Conv_R4:
                Push(ToFloat(PopValue(), TirType.F32, unsigned: false));
                return true;

            case IlOpCode.Conv_R8:
                Push(ToFloat(PopValue(), TirType.F64, unsigned: false));
                return true;

            case IlOpCode.Conv_R_Un:
                Push(ToFloat(PopValue(), ConvR4IsNext() ? TirType.F32 : TirType.F64, unsigned: true));
                return true;
        }

        if (_stack.Count == 0 || !_stack[^1].Type.IsFloat)
        {
            return false;
        }

        var type = _stack[^1].Type;
        switch (opCode)
        {
            case IlOpCode.Neg:
                var value = Pop();
                Push(_ir.Binary(TirBinaryOp.Sub, _ir.Const(type, -0.0), value));
                return true;

            case IlOpCode.Conv_I1 or IlOpCode.Conv_U1:
                Push(_ir.Convert(Pop(), TirType.I32));
                ConvertToSmall(TirType.I8, unsigned: opCode == IlOpCode.Conv_U1);
                return true;

            case IlOpCode.Conv_I2 or IlOpCode.Conv_U2:
                Push(_ir.Convert(Pop(), TirType.I32));
                ConvertToSmall(TirType.I16, unsigned: opCode == IlOpCode.Conv_U2);
                return true;

            case IlOpCode.Conv_I4 or IlOpCode.Conv_U4:
                Push(_ir.Convert(Pop(), TirType.I32, unsigned: opCode == IlOpCode.Conv_U4));
                return true;

            case IlOpCode.Conv_I8 or IlOpCode.Conv_U8:
                Push(_ir.Convert(Pop(), TirType.I64, unsigned: opCode == IlOpCode.Conv_U8));
                return true;

            case IlOpCode.Conv_I or IlOpCode.Conv_U:
                Push(_ir.Convert(Pop(), TirType.NativeInt, unsigned: opCode == IlOpCode.Conv_U));
                return true;

            case IlOpCode.Div_Un or IlOpCode.Rem_Un or IlOpCode.And or IlOpCode.Or or IlOpCode.Xor or IlOpCode.Not
                or IlOpCode.Shl or IlOpCode.Shr or IlOpCode.Shr_Un or IlOpCode.Switch or IlOpCode.Brtrue
                or IlOpCode.Brtrue_S or IlOpCode.Brfalse or IlOpCode.Brfalse_S:
                throw Error($"{instruction.Name} takes an integer, not {LoweringErrors.StackTypeName(type)}");

            default:
                // a float and a double: the float is widened, then the operation is done in double
                var other = _stack.Count >= 2 ? _stack[^2].Type : null;
                var takesTwo = ArithmeticOp(opCode) is not null || CompareOp(opCode) is not null
                    || BranchOp(opCode) is not null;
                if (takesTwo && other is { IsFloat: true } && other != type)
                {
                    var index = other == TirType.F32 ? _stack.Count - 2 : _stack.Count - 1;
                    _stack[index] = StackValue.Of(_ir.Convert(Materialize(_stack[index]), TirType.F64));
                }

                return false;
        }
    }

    /// <summary>
    /// <paramref name="value"/> as a float of type <paramref name="to"/>: an integer rounded to nearest (read as
    /// unsigned when <paramref name="unsigned"/>), a double rounded to float, a float widened to double.
    /// </summary>
    private StackValue ToFloat(StackValue value, TirType to, bool unsigned)
    {
        if (value.Type == to)
        {
            return value;
        }

        if (value.Type.IsFloat)
        {
            return StackValue.Of(_ir.Convert(Materialize(value), to));
        }

        return StackValue.Of(value.Condition is { } condition
            ? _ir.Convert(condition, to)
            : _ir.Convert(Materialize(value), to, unsigned));
    }

    /// <summary>True when the instruction after the current one is a <c>conv.r4</c> in the same block.</summary>
    private bool ConvR4IsNext()
    {
        var next = _body.IndexOf(_instruction.Offset) + 1;
        return next < _block.End && _body.Instructions[next].OpCode == IlOpCode.Conv_R4;
    }
}
