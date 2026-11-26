namespace ChipSharp.Ir;

/// <summary>
/// A whole program in ChipSharp's intermediate representation (TIR): what the shared frontend and
/// middle produce from the IL, and what every code generator consumes. Its contents arrive at milestone M0.
/// </summary>
public sealed class TirModule(string name)
{
    public string Name { get; } = name;
}
