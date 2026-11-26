namespace ChipSharp.Ir;

/// <summary>
/// A whole program in ChipSharp's intermediate representation (TIR): what the shared frontend and
/// middle produce from the IL, and what every code generator consumes: its functions and its globals.
/// Check it with <see cref="TirVerifier"/> and dump it with <see cref="TirPrinter"/>.
/// </summary>
public sealed class TirModule
{
    private readonly List<TirFunction> _functions = [];
    private readonly List<TirGlobal> _globals = [];

    public TirModule(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    /// <summary>Every function, defined and external, in the order they were added.</summary>
    public IReadOnlyList<TirFunction> Functions => _functions;

    /// <summary>Every global variable, in the order they were added.</summary>
    public IReadOnlyList<TirGlobal> Globals => _globals;

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

    /// <summary>
    /// Adds a global variable, printed as <c>@name</c>. It starts at zero (every byte zero, for structs and arrays
    /// too) unless <paramref name="initialValue"/> is given.
    /// </summary>
    /// <param name="name">The C# display name, e.g. "Program.counter"; unique within the module.</param>
    /// <param name="type">What it holds: any type but void.</param>
    /// <param name="initialValue">
    /// The value it starts with: a constant of the global's type (so a scalar, never a ptr), made with
    /// <c>new TirConst(...)</c> and not added to any block. Null for zero.
    /// </param>
    public TirGlobal AddGlobal(string name, TirType type, TirConst? initialValue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(type);
        if (type == TirType.Void)
        {
            throw new TirException($"Module '{Name}': global @{name} cannot have type void.");
        }

        if (_globals.Any(g => g.Name == name))
        {
            throw new TirException($"Module '{Name}' already has a global named @{name}.");
        }

        if (initialValue is not null && initialValue.Type != type)
        {
            throw new TirException($"Module '{Name}': global @{name} holds {type}, but its initial value is " +
                $"{initialValue.Type}.");
        }

        if (initialValue is { Block: { } block })
        {
            throw new TirException($"Module '{Name}': the initial value of global @{name}, " +
                $"'{TirPrinter.Print(initialValue)}', is an instruction of block '{block.Name}' of function " +
                $"'{block.Function.Name}'; give a constant that is in no block.");
        }

        var global = new TirGlobal(this, _globals.Count, name, type, initialValue);
        _globals.Add(global);
        return global;
    }

    /// <summary>
    /// Every struct type the module uses (in its globals, function signatures, slots and instructions, also inside
    /// arrays and other structs), each once, in the order first met, with the structs a struct's fields hold before
    /// it. A code generator declares them in this order; <see cref="TirPrinter"/> lists them so.
    /// </summary>
    public IReadOnlyList<TirStructType> FindStructTypes()
    {
        var found = new List<TirStructType>();
        var seen = new HashSet<TirStructType>();

        void Visit(TirType type)
        {
            switch (type)
            {
                case TirArrayType array:
                    Visit(array.Element);
                    break;

                case TirStructType structType when seen.Add(structType):
                    foreach (var field in structType.Fields)
                    {
                        Visit(field.Type);
                    }

                    found.Add(structType);
                    break;
            }
        }

        foreach (var global in _globals)
        {
            Visit(global.Type);
        }

        foreach (var function in _functions)
        {
            Visit(function.ReturnType);
            foreach (var parameter in function.Parameters)
            {
                Visit(parameter.Type);
            }

            foreach (var slot in function.Slots)
            {
                Visit(slot.Type);
            }

            foreach (var instruction in function.Blocks.SelectMany(b => b.Instructions))
            {
                Visit(instruction.Type);
                switch (instruction)
                {
                    case TirFieldAddress field:
                        Visit(field.StructType);
                        break;

                    case TirElementAddress element:
                        Visit(element.ElementType);
                        break;

                    case TirSizeOf size:
                        Visit(size.OfType);
                        break;
                }

                foreach (var operand in instruction.Operands)
                {
                    Visit(operand.Type);
                }
            }
        }

        return found;
    }

    public override string ToString() => Name;

    private TirFunction Add(TirFunction function)
    {
        _functions.Add(function);
        return function;
    }
}
