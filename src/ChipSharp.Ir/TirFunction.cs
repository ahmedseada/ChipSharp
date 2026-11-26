namespace ChipSharp.Ir;

public enum TirFunctionKind
{
    /// <summary>Has a body: one or more blocks.</summary>
    Defined,

    /// <summary>Has no body: the runtime implements it (e.g. a print routine). It has no blocks.</summary>
    External,
}

/// <summary>
/// A static method in TIR. A defined function has blocks; the first block is where it starts.
/// Values cross from one block to another only through <see cref="Slots"/> (or as parameters).
/// Created with <see cref="TirModule.AddFunction"/> or <see cref="TirModule.AddExternalFunction"/>.
/// </summary>
public sealed class TirFunction
{
    private readonly List<TirSlot> _slots = [];
    private readonly List<TirBlock> _blocks = [];

    internal TirFunction(TirModule module, TirFunctionKind kind, string name, TirType returnType,
        IEnumerable<(string Name, TirType Type)> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(returnType);
        ArgumentNullException.ThrowIfNull(parameters);

        Module = module;
        Kind = kind;
        Name = name;
        ReturnType = returnType;

        var list = new List<TirParameter>();
        foreach (var (parameterName, type) in parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
            ArgumentNullException.ThrowIfNull(type);
            if (type == TirType.Void)
            {
                throw new TirException($"Function '{name}': parameter %{parameterName} cannot have type void.");
            }

            if (list.Any(p => p.Name == parameterName))
            {
                throw new TirException($"Function '{name}' has two parameters named %{parameterName}.");
            }

            list.Add(new TirParameter(this, list.Count, parameterName, type));
        }

        Parameters = list;
    }

    public TirModule Module { get; }

    public TirFunctionKind Kind { get; }

    /// <summary>
    /// The C# display name, e.g. "Program.Square". Overloads share a name; making unique symbol names
    /// is the code generator's job.
    /// </summary>
    public string Name { get; }

    /// <summary>What the function returns; <see cref="TirType.Void"/> when it returns nothing.</summary>
    public TirType ReturnType { get; }

    public IReadOnlyList<TirParameter> Parameters { get; }

    /// <summary>The function's mutable variables: C# locals and any value that lives across blocks.</summary>
    public IReadOnlyList<TirSlot> Slots => _slots;

    /// <summary>
    /// The blocks in order. The first is the entry block. It may also be a jump target (a loop at the very
    /// start of a method), so a code generator that needs a block nothing jumps to (for LLVM's allocas)
    /// adds its own in front of it.
    /// </summary>
    public IReadOnlyList<TirBlock> Blocks => _blocks;

    /// <summary>The first block, or null when there are none (an external function).</summary>
    public TirBlock? EntryBlock => _blocks.Count > 0 ? _blocks[0] : null;

    /// <summary>
    /// Adds a mutable variable of any type but void (a struct or an array too), printed as <c>$name</c>. It starts
    /// at zero. Slot names are unique within the function.
    /// </summary>
    public TirSlot AddSlot(string name, TirType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(type);
        if (type == TirType.Void)
        {
            throw new TirException($"Function '{Name}': slot ${name} cannot have type void.");
        }

        if (_slots.Any(s => s.Name == name))
        {
            throw new TirException($"Function '{Name}' already has a slot named ${name}.");
        }

        var slot = new TirSlot(this, _slots.Count, name, type);
        _slots.Add(slot);
        return slot;
    }

    /// <summary>
    /// Adds an empty block after the existing ones; the first block added is the entry.
    /// Block names are unique within the function.
    /// </summary>
    public TirBlock AddBlock(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_blocks.Any(b => b.Name == name))
        {
            throw new TirException($"Function '{Name}' already has a block named '{name}'.");
        }

        var block = new TirBlock(this, name);
        _blocks.Add(block);
        return block;
    }

    public override string ToString() => Name;
}

/// <summary>
/// A function parameter: a value usable in any block of its function, printed as <c>%name</c>.
/// Parameters cannot be assigned; a C# method that assigns to one copies it into a slot first.
/// </summary>
public sealed class TirParameter : TirValue
{
    internal TirParameter(TirFunction function, int index, string name, TirType type) : base(type)
    {
        Function = function;
        Index = index;
        Name = name;
    }

    public TirFunction Function { get; }

    /// <summary>Position in the parameter list, from 0.</summary>
    public int Index { get; }

    public string Name { get; }

    public override string ToString() => $"%{Name}";
}

/// <summary>
/// A mutable variable of a function, read with <see cref="TirLoad"/> and written with <see cref="TirStore"/>,
/// printed as <c>$name</c>. C# locals live in slots, and so does any value that must cross from one block to
/// another. Its address (<see cref="TirSlotAddress"/>) reaches the parts of a struct or array in it.
/// (The LLVM generator makes each slot an alloca; LLVM's mem2reg then turns them into registers.)
/// </summary>
public sealed class TirSlot
{
    internal TirSlot(TirFunction function, int index, string name, TirType type)
    {
        Function = function;
        Index = index;
        Name = name;
        Type = type;
    }

    public TirFunction Function { get; }

    /// <summary>Position in <see cref="TirFunction.Slots"/>, from 0.</summary>
    public int Index { get; }

    public string Name { get; }

    public TirType Type { get; }

    public override string ToString() => $"${Name}";
}
