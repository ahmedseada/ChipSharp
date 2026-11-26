namespace ChipSharp.Ir;

/// <summary>
/// A variable of the whole program, such as a C# static field, printed as <c>@name</c>. It starts at zero, or at
/// the constant it was given. Code reaches it through its address (<see cref="TirGlobalAddress"/>), then loads and
/// stores through that. Created with <see cref="TirModule.AddGlobal"/>.
/// </summary>
public sealed class TirGlobal
{
    internal TirGlobal(TirModule module, int index, string name, TirType type, TirConst? initialValue)
    {
        Module = module;
        Index = index;
        Name = name;
        Type = type;
        InitialValue = initialValue;
    }

    public TirModule Module { get; }

    /// <summary>Position in <see cref="TirModule.Globals"/>, from 0.</summary>
    public int Index { get; }

    /// <summary>
    /// The C# display name, e.g. "Program.counter"; unique within the module. Making the symbol name is the code
    /// generator's job.
    /// </summary>
    public string Name { get; }

    /// <summary>What the global holds: any type but void, structs and arrays too.</summary>
    public TirType Type { get; }

    /// <summary>The value it starts with; null when it starts at zero (every byte zero).</summary>
    public TirConst? InitialValue { get; }

    public override string ToString() => $"@{Name}";
}
