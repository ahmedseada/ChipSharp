namespace ChipSharp.Ir;

/// <summary>
/// A variable of the whole program, such as a C# static field, printed as <c>@name</c>. It starts at zero, or at
/// the constant it was given. Code reaches it through its address (<see cref="TirGlobalAddress"/>), then loads and
/// stores through that. Created with <see cref="TirModule.AddGlobal"/>; read-only data, such as the text of a C#
/// string literal, is a constant global, created with <see cref="TirModule.AddConstant"/>.
/// </summary>
public sealed class TirGlobal
{
    internal TirGlobal(TirModule module, int index, string name, TirType type, TirConst? initialValue, byte[]? data)
    {
        Module = module;
        Index = index;
        Name = name;
        Type = type;
        InitialValue = initialValue;
        Data = data is null ? null : Array.AsReadOnly(data);
    }

    public TirModule Module { get; }

    /// <summary>Position in <see cref="TirModule.Globals"/>, from 0.</summary>
    public int Index { get; }

    /// <summary>
    /// The C# display name, e.g. "Program.counter"; unique within the module. Making the symbol name is the code
    /// generator's job.
    /// </summary>
    public string Name { get; }

    /// <summary>What the global holds: any type but void, structs and arrays too; <c>[n x i8]</c> for a constant.</summary>
    public TirType Type { get; }

    /// <summary>The value it starts with; null when it starts at zero (every byte zero), and for a constant.</summary>
    public TirConst? InitialValue { get; }

    /// <summary>
    /// The bytes of a constant global, which it holds from the start and forever (nothing writes it); null for a
    /// variable.
    /// </summary>
    public IReadOnlyList<byte>? Data { get; }

    /// <summary>True for read-only data (<see cref="Data"/>): a code generator puts it with the code, in flash on a chip.</summary>
    public bool IsConstant => Data is not null;

    public override string ToString() => $"@{Name}";
}
