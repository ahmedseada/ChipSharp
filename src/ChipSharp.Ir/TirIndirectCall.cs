namespace ChipSharp.Ir;

/// <summary>
/// <c>%3 = call indirect i32 %2(%0, %1)</c>: calls the function whose address is %2 (a ptr), with the arguments, and
/// produces what it returns, of the given type (nothing for void). The address is that of a defined function of the
/// module, as a type descriptor's method tables hold them (<see cref="TirTypeDescriptor.VirtualMethods"/>,
/// <see cref="TirTypeDescriptor.Interfaces"/>), read through the descriptor: a virtual call, or a call through an
/// interface. The function must take exactly the types of the arguments, in order, and return the call's type; nothing
/// can check that, so the code that reads the address from a method table is the one that knows it.
/// </summary>
public sealed class TirCallIndirect : TirInstruction
{
    public TirCallIndirect(TirType returnType, TirValue callee, IEnumerable<TirValue> arguments) : base(returnType)
    {
        ArgumentNullException.ThrowIfNull(callee);
        ArgumentNullException.ThrowIfNull(arguments);
        Callee = callee;
        Arguments = [.. arguments];
        if (Arguments.Any(a => a is null))
        {
            throw new ArgumentException("An argument is null.", nameof(arguments));
        }
    }

    /// <summary>The address of the function: a ptr.</summary>
    public TirValue Callee { get; }

    /// <summary>One value per parameter of the function, in order.</summary>
    public IReadOnlyList<TirValue> Arguments { get; }

    public override IReadOnlyList<TirValue> Operands => [Callee, .. Arguments];
}
