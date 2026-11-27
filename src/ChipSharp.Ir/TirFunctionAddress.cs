namespace ChipSharp.Ir;

/// <summary>
/// <c>%0 = funcaddr Program.Square</c>: the address (a ptr) of a defined function of this module, which
/// <see cref="TirCallIndirect"/> calls: what a delegate holds (see ChipSharp.Frontend's Delegates). As with a method
/// table's address, the code that calls it is the one that knows the function's parameters and result.
/// </summary>
public sealed class TirFunctionAddress : TirInstruction
{
    public TirFunctionAddress(TirFunction function) : base(TirType.Ptr)
    {
        ArgumentNullException.ThrowIfNull(function);
        Function = function;
    }

    /// <summary>The function: a defined one of the same module.</summary>
    public TirFunction Function { get; }

    public override IReadOnlyList<TirValue> Operands => [];
}
