namespace ChipSharp.Ir;

/// <summary>
/// A whole program in ChipSharp's intermediate representation (TIR): what the shared frontend and
/// middle produce from the IL, and what every code generator consumes.
/// Check it with <see cref="TirVerifier"/> and dump it with <see cref="TirPrinter"/>.
/// </summary>
public sealed class TirModule
{
    private readonly List<TirFunction> _functions = [];

    public TirModule(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    /// <summary>Every function, defined and external, in the order they were added.</summary>
    public IReadOnlyList<TirFunction> Functions => _functions;

    /// <summary>The function the program starts with (C#'s <c>Main</c>). It must be a defined function of this module.</summary>
    public TirFunction? EntryPoint { get; set; }

    /// <summary>
    /// Adds a function with a body; add its blocks with <see cref="TirFunction.AddBlock"/>.
    /// The function exists before its body, so calls to it (even from itself) can be built first.
    /// </summary>
    /// <param name="name">The C# display name, e.g. "Program.Square". The code generator makes the symbol name.</param>
    /// <param name="returnType">What it returns; <see cref="TirType.Void"/> for nothing.</param>
    /// <param name="parameters">Name and type of each parameter, e.g. <c>("n", TirType.I32)</c>.</param>
    public TirFunction AddFunction(string name, TirType returnType,
        params IEnumerable<(string Name, TirType Type)> parameters) =>
        Add(new TirFunction(this, TirFunctionKind.Defined, name, returnType, parameters));

    /// <summary>Adds a function with no body that the runtime implements, e.g. a print routine.</summary>
    /// <param name="name">The C# display name, e.g. "ChipSharp.Runtime.Print".</param>
    /// <param name="returnType">What it returns; <see cref="TirType.Void"/> for nothing.</param>
    /// <param name="parameters">Name and type of each parameter, e.g. <c>("value", TirType.I32)</c>.</param>
    public TirFunction AddExternalFunction(string name, TirType returnType,
        params IEnumerable<(string Name, TirType Type)> parameters) =>
        Add(new TirFunction(this, TirFunctionKind.External, name, returnType, parameters));

    public override string ToString() => Name;

    private TirFunction Add(TirFunction function)
    {
        _functions.Add(function);
        return function;
    }
}
