using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The TIR functions that divide whole numbers as .NET does, each added to the module when first needed. .NET throws
/// DivideByZeroException for <c>x / 0</c> and <c>x % 0</c>, and OverflowException for the smallest value divided by -1
/// (<c>int.MinValue / -1</c>, and <c>%</c> too); LLVM's sdiv, udiv, srem and urem make all of these undefined, and the
/// program went on with any number. These check first, and stop the program with .NET's message:
/// <code>
/// function i32 ChipSharp.Division.Div(i32 %left, i32 %right)    (Div, DivUnsigned, Rem, RemUnsigned of each type)
/// external function void ChipSharp.Runtime.DivideByZero()
/// external function void ChipSharp.Runtime.Overflow()
/// </code>
/// A division by a constant other than 0 and -1, the usual <c>x / 10</c>, needs neither check and stays a plain
/// division (<see cref="NeedsCheck"/>). LLVM inlines the functions into their callers.
/// </summary>
internal sealed class DivisionFunctions(TirModule module)
{
    private readonly Dictionary<(TirBinaryOp, TirType), TirFunction> _functions = [];
    private TirFunction? _divideByZero;
    private TirFunction? _overflow;

    /// <summary>True for a division or remainder of whole numbers whose divisor may be 0 (or, signed, -1).</summary>
    public static bool NeedsCheck(TirBinaryOp op, TirValue left, TirValue right) =>
        op is TirBinaryOp.Div or TirBinaryOp.DivUnsigned or TirBinaryOp.Rem or TirBinaryOp.RemUnsigned
        && !left.Type.IsFloat
        && right is not TirConst { IntegerValue: not 0 and not -1 };

    /// <summary>The function that does <paramref name="op"/> on two values of <paramref name="type"/> as .NET does.</summary>
    public TirFunction For(TirBinaryOp op, TirType type)
    {
        if (!_functions.TryGetValue((op, type), out var function))
        {
            function = Define(op, type);
            _functions.Add((op, type), function);
        }

        return function;
    }

    private TirFunction Define(TirBinaryOp op, TirType type)
    {
        var function = module.AddFunction($"ChipSharp.Division.{op}", type, ("left", type), ("right", type));
        var (left, right) = (function.Parameters[0], function.Parameters[1]);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var byZero = function.AddBlock("by_zero");
        var divide = function.AddBlock("divide");
        var signed = op is TirBinaryOp.Div or TirBinaryOp.Rem;
        var notZero = signed ? function.AddBlock("not_zero") : divide;

        ir.Branch(ir.Compare(TirCompareOp.Eq, right, ir.Const(type, 0)), byZero, notZero);

        ir.Block = byZero;
        ir.Call(_divideByZero ??= External("ChipSharp.Runtime.DivideByZero"));
        ir.Return(ir.Const(type, 0));

        if (signed)
        {
            // by -1, only the smallest value overflows: the one number other than 0 that is its own negative
            var byMinusOne = function.AddBlock("by_minus_one");
            var ownNegative = function.AddBlock("own_negative");
            var overflow = function.AddBlock("overflow");

            ir.Block = notZero;
            ir.Branch(ir.Compare(TirCompareOp.Eq, right, ir.Const(type, -1)), byMinusOne, divide);

            ir.Block = byMinusOne;
            var negative = ir.Binary(TirBinaryOp.Sub, ir.Const(type, 0), left);
            ir.Branch(ir.Compare(TirCompareOp.Eq, left, negative), ownNegative, divide);

            ir.Block = ownNegative;
            ir.Branch(ir.Compare(TirCompareOp.Eq, left, ir.Const(type, 0)), divide, overflow);

            ir.Block = overflow;
            ir.Call(_overflow ??= External("ChipSharp.Runtime.Overflow"));
            ir.Return(ir.Const(type, 0));
        }

        ir.Block = divide;
        ir.Return(ir.Binary(op, left, right));
        return function;
    }

    /// <summary>A runtime function that stops the program, declared once (other parts of the translation may have).</summary>
    private TirFunction External(string name) =>
        module.Functions.FirstOrDefault(f => f is { Kind: TirFunctionKind.External } && f.Name == name)
        ?? module.AddExternalFunction(name, TirType.Void);
}
