using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>The LLVM instruction for each TIR operation. TIR types are signless, so the operation picks signed or unsigned.</summary>
internal static class LlvmOpcodes
{
    /// <summary>E.g. add, sdiv, udiv, ashr, lshr; fadd, fdiv, frem for floats.</summary>
    public static string Binary(TirBinaryOp op, bool isFloat) => isFloat
        ? op switch
        {
            TirBinaryOp.Add => "fadd",
            TirBinaryOp.Sub => "fsub",
            TirBinaryOp.Mul => "fmul",
            TirBinaryOp.Div => "fdiv",
            TirBinaryOp.Rem => "frem",
            _ => throw Unsupported($"{op} on floats"),
        }
        : op switch
        {
            TirBinaryOp.Add => "add",
            TirBinaryOp.Sub => "sub",
            TirBinaryOp.Mul => "mul",
            TirBinaryOp.Div => "sdiv",
            TirBinaryOp.DivUnsigned => "udiv",
            TirBinaryOp.Rem => "srem",
            TirBinaryOp.RemUnsigned => "urem",
            TirBinaryOp.And => "and",
            TirBinaryOp.Or => "or",
            TirBinaryOp.Xor => "xor",
            TirBinaryOp.Shl => "shl",
            TirBinaryOp.Shr => "ashr",
            TirBinaryOp.ShrUnsigned => "lshr",
            _ => throw Unsupported(op.ToString()),
        };

    /// <summary>
    /// <c>icmp slt</c>, <c>icmp ult</c>, ... for integers and bool. For floats, <c>fcmp</c>: eq is ordered (false when
    /// either side is NaN) and ne unordered (true when either side is NaN), as in C#; lt, le, gt, ge are ordered,
    /// and their unsigned variants are unordered (also true when either side is NaN), which is what IL's
    /// <c>clt.un</c> and <c>cgt.un</c> mean on floats.
    /// </summary>
    public static string Compare(TirCompareOp op, bool isFloat) => isFloat
        ? "fcmp " + op switch
        {
            TirCompareOp.Eq => "oeq",
            TirCompareOp.Ne => "une",
            TirCompareOp.Lt => "olt",
            TirCompareOp.Le => "ole",
            TirCompareOp.Gt => "ogt",
            TirCompareOp.Ge => "oge",
            TirCompareOp.LtUnsigned => "ult",
            TirCompareOp.LeUnsigned => "ule",
            TirCompareOp.GtUnsigned => "ugt",
            TirCompareOp.GeUnsigned => "uge",
            _ => throw Unsupported($"{op} on floats"),
        }
        : "icmp " + op switch
        {
            TirCompareOp.Eq => "eq",
            TirCompareOp.Ne => "ne",
            TirCompareOp.Lt => "slt",
            TirCompareOp.Le => "sle",
            TirCompareOp.Gt => "sgt",
            TirCompareOp.Ge => "sge",
            TirCompareOp.LtUnsigned => "ult",
            TirCompareOp.LeUnsigned => "ule",
            TirCompareOp.GtUnsigned => "ugt",
            TirCompareOp.GeUnsigned => "uge",
            _ => throw Unsupported(op.ToString()),
        };

    private static ChipSharpException Unsupported(string what) =>
        new($"The llvm code generator does not support {what} yet.");
}
