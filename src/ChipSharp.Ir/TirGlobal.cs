namespace ChipSharp.Ir;

/// <summary>
/// A variable of the whole program, such as a C# static field, printed as <c>@name</c>. It starts at zero, or at
/// the constant it was given. Code reaches it through its address (<see cref="TirGlobalAddress"/>), then loads and
/// stores through that. Created with <see cref="TirModule.AddGlobal"/>; read-only data, such as the text of a C#
/// string literal, is a constant global, created with <see cref="TirModule.AddConstant"/>. A global array may start
/// with a constant for each element (<see cref="Elements"/>), a variable one or a constant one (a table, such as a C#
/// <c>static readonly byte[]</c> whose elements nothing writes): see <see cref="TirModule.AddArray"/>.
/// </summary>
public sealed class TirGlobal
{
    internal TirGlobal(TirModule module, int index, string name, TirType type, TirConst? initialValue, byte[]? data,
        TirConst[]? elements = null, bool isConstant = false, TirTypeDescriptor? objectType = null, int objectLength = 0)
    {
        ObjectType = objectType;
        ObjectLength = objectLength;
        Module = module;
        Index = index;
        Name = name;
        Type = type;
        InitialValue = initialValue;
        Data = data is null ? null : Array.AsReadOnly(data);
        Elements = elements is null ? null : Array.AsReadOnly(elements);
        IsConstant = data is not null || isConstant;
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

    /// <summary>
    /// The values a global array starts with, one constant per element, in order; null when it starts at zero (and
    /// for any global that is not an array).
    /// </summary>
    public IReadOnlyList<TirConst>? Elements { get; }

    /// <summary>
    /// True for read-only data (<see cref="Data"/>, or a constant array): a code generator puts it with the code, in
    /// flash on a chip.
    /// </summary>
    public bool IsConstant { get; }

    /// <summary>
    /// For a constant object (<see cref="TirModule.AddConstantObject"/>), the array type of the object: its header is
    /// this descriptor's address, then come <see cref="ObjectLength"/> and, from <see cref="TirLayout.ArrayDataOffset"/>
    /// on, the elements' bytes, <see cref="Data"/>. Null for any other global.
    /// </summary>
    public TirTypeDescriptor? ObjectType { get; }

    /// <summary>The length of a constant object, as its length field holds it; 0 for any other global.</summary>
    public int ObjectLength { get; }

    public override string ToString() => $"@{Name}";
}
