namespace ChipSharp.Ir;

/// <summary>
/// Appends instructions to a block and returns them, so each result can be used as an operand right away.
/// Point <see cref="Block"/> at another block to continue there. It checks nothing; run <see cref="TirVerifier"/>.
/// <code>
/// var square = module.AddFunction("Program.Square", TirType.I32, ("n", TirType.I32));
/// var b = new TirBuilder(square.AddBlock("entry"));
/// var n = square.Parameters[0];
/// b.Return(b.Binary(TirBinaryOp.Mul, n, n));
/// </code>
/// </summary>
public sealed class TirBuilder(TirBlock block)
{
    private TirBlock _block = block ?? throw new ArgumentNullException(nameof(block));

    /// <summary>Where the next instruction goes (at the end).</summary>
    public TirBlock Block
    {
        get => _block;
        set => _block = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The function of the current block.</summary>
    public TirFunction Function => Block.Function;

    /// <summary>An integer or bool constant, e.g. <c>Const(TirType.I32, 42)</c>. See <see cref="TirConst"/> for the ranges.</summary>
    public TirConst Const(TirType type, long value) => Block.Append(new TirConst(type, value));

    /// <summary>A floating-point constant of type f32 or f64.</summary>
    public TirConst Const(TirType type, double value) => Block.Append(new TirConst(type, value));

    public TirConst Const(bool value) => Block.Append(new TirConst(value));

    public TirBinary Binary(TirBinaryOp op, TirValue left, TirValue right) =>
        Block.Append(new TirBinary(op, left, right));

    public TirCompare Compare(TirCompareOp op, TirValue left, TirValue right) =>
        Block.Append(new TirCompare(op, left, right));

    /// <summary>Converts to another type; <paramref name="unsigned"/> reads the integer side as unsigned.</summary>
    public TirConvert Convert(TirValue value, TirType to, bool unsigned = false) =>
        Block.Append(new TirConvert(value, to, unsigned));

    public TirLoad Load(TirSlot slot) => Block.Append(new TirLoad(slot));

    public TirStore Store(TirSlot slot, TirValue value) => Block.Append(new TirStore(slot, value));

    public TirCall Call(TirFunction callee, params IEnumerable<TirValue> arguments) =>
        Block.Append(new TirCall(callee, arguments));

    /// <summary>Ends the block, returning <paramref name="value"/> (none in a void function).</summary>
    public TirReturn Return(TirValue? value = null) => Block.Append(new TirReturn(value));

    /// <summary>Ends the block, continuing at <paramref name="target"/>.</summary>
    public TirJump Jump(TirBlock target) => Block.Append(new TirJump(target));

    /// <summary>Ends the block, continuing at <paramref name="whenTrue"/> or <paramref name="whenFalse"/>.</summary>
    public TirBranch Branch(TirValue condition, TirBlock whenTrue, TirBlock whenFalse) =>
        Block.Append(new TirBranch(condition, whenTrue, whenFalse));
}
