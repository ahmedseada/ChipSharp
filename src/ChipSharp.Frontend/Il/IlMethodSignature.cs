namespace ChipSharp.Frontend.Il;

/// <summary>The calling convention a method signature names. C# methods use <see cref="Default"/>.</summary>
public enum IlCallingConvention
{
    Default,
    CDecl,
    StdCall,
    ThisCall,
    FastCall,
    VarArgs,
    Unmanaged,
}

/// <summary>
/// What a method takes and returns, as its signature blob says. <paramref name="HasThis"/> is true for an instance
/// method: its IL argument 0 is <c>this</c>, which is not in <paramref name="ParameterTypes"/>.
/// <paramref name="GenericParameterCount"/> is the number of the method's own type parameters.
/// Signatures are equal when everything in them is equal. <c>ToString()</c> writes e.g. <c>int32(int32)</c>.
/// </summary>
public sealed record IlMethodSignature(
    bool HasThis,
    IlType ReturnType,
    IReadOnlyList<IlType> ParameterTypes,
    int GenericParameterCount = 0,
    IlCallingConvention CallingConvention = IlCallingConvention.Default)
{
    public bool Equals(IlMethodSignature? other) =>
        other is not null
        && HasThis == other.HasThis
        && ReturnType == other.ReturnType
        && ParameterTypes.SequenceEqual(other.ParameterTypes)
        && GenericParameterCount == other.GenericParameterCount
        && CallingConvention == other.CallingConvention;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(HasThis);
        hash.Add(ReturnType);
        foreach (var type in ParameterTypes)
        {
            hash.Add(type);
        }

        hash.Add(GenericParameterCount);
        hash.Add(CallingConvention);
        return hash.ToHashCode();
    }

    public override string ToString() =>
        $"{ConventionText(CallingConvention)}{(HasThis ? "instance " : "")}{ReturnType}({string.Join(", ", ParameterTypes)})";

    internal static string ConventionText(IlCallingConvention convention) => convention switch
    {
        IlCallingConvention.Default => "",
        IlCallingConvention.VarArgs => "vararg ",
        IlCallingConvention.Unmanaged => "unmanaged ",
        _ => $"unmanaged {convention.ToString().ToLowerInvariant()} ",
    };
}
