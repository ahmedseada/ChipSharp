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
    private readonly List<TirTypeDescriptor> _types = [];

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

    /// <summary>Every type descriptor (classes, arrays and interfaces of objects on the heap), in the order they were added.</summary>
    public IReadOnlyList<TirTypeDescriptor> TypeDescriptors => _types;

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
        CheckNewGlobal(name);
        ArgumentNullException.ThrowIfNull(type);
        if (type == TirType.Void)
        {
            throw new TirException($"Module '{Name}': global @{name} cannot have type void.");
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

        var global = new TirGlobal(this, _globals.Count, name, type, initialValue, data: null);
        _globals.Add(global);
        return global;
    }

    /// <summary>
    /// Adds read-only data, printed as <c>constant [5 x i8] @name = c"hello"</c>: a global of type <c>[n x i8]</c>
    /// that holds <paramref name="data"/> from the start, and that nothing may write (the verifier refuses a store
    /// it can see). A code generator puts it with the code: in flash on a chip. Code reaches it through its address,
    /// like any global's.
    /// </summary>
    /// <param name="name">A display name, unique within the module among all globals, e.g. "&lt;text 0&gt;".</param>
    /// <param name="data">Its bytes: at least one.</param>
    public TirGlobal AddConstant(string name, IEnumerable<byte> data)
    {
        CheckNewGlobal(name);
        ArgumentNullException.ThrowIfNull(data);
        byte[] bytes = [.. data];
        if (bytes.Length == 0)
        {
            throw new TirException($"Module '{Name}': constant @{name} has no bytes; it needs at least one.");
        }

        var global = new TirGlobal(this, _globals.Count, name, new TirArrayType(TirType.I8, bytes.Length), null, bytes);
        _globals.Add(global);
        return global;
    }

    /// <summary>
    /// Adds the descriptor of a class, printed as <c>type Program.Node = class Program.Node : System.Object</c>: its
    /// objects are laid out as <paramref name="objectType"/>, whose first field is the header and whose next fields are
    /// those of <paramref name="baseType"/>'s object type, in order (see <see cref="TirTypeDescriptor.ObjectStruct"/>).
    /// </summary>
    /// <param name="name">The C# name, e.g. "Program.Node"; unique among the module's descriptors.</param>
    /// <param name="objectType">The object type: the header (<see cref="TirTypeDescriptor.HeaderField"/>, a ptr), then the fields.</param>
    /// <param name="baseType">The base class, a class of this module; null for none (System.Object).</param>
    public TirTypeDescriptor AddClass(string name, TirStructType objectType, TirTypeDescriptor? baseType = null)
    {
        CheckNewType(name);
        ArgumentNullException.ThrowIfNull(objectType);
        if (objectType.Fields[0] is not { Name: TirTypeDescriptor.HeaderField } header || header.Type != TirType.Ptr)
        {
            throw new TirException($"Module '{Name}': class {name}'s object type {objectType} must start with the " +
                $"header, a ptr field named {TirTypeDescriptor.HeaderField}.");
        }

        if (baseType is not null)
        {
            if (baseType.Module != this || baseType.Kind != TirTypeDescriptorKind.Class)
            {
                throw new TirException($"Module '{Name}': the base of class {name} must be a class of this module, " +
                    $"not {baseType.Kind.ToString().ToLowerInvariant()} {baseType.Name}" +
                    (baseType.Module == this ? "." : $" of module '{baseType.Module.Name}'."));
            }

            var inherited = baseType.ObjectType!.Fields;
            if (inherited.Count > objectType.Fields.Count || inherited.Where((field, i) =>
                    field.Name != objectType.Fields[i].Name || field.Type != objectType.Fields[i].Type).Any())
            {
                throw new TirException($"Module '{Name}': class {name}'s object type {objectType} must start with the " +
                    $"fields of its base {baseType.Name}'s, {baseType.ObjectType}, in their order.");
            }
        }

        return AddType(new TirTypeDescriptor(this, _types.Count, name, TirTypeDescriptorKind.Class, objectType, null,
            baseType));
    }

    /// <summary>
    /// Adds the descriptor of an array type, printed as <c>type int[] = array of i32</c>: its objects hold a length and
    /// elements of <paramref name="elementType"/> (see <see cref="TirLayout.ArrayDataOffset"/>).
    /// </summary>
    /// <param name="name">The C# name, e.g. "int[]" or "Program.Node[]"; unique among the module's descriptors.</param>
    /// <param name="elementType">The type of each element: any type but void, ref for elements that are objects.</param>
    public TirTypeDescriptor AddArrayType(string name, TirType elementType)
    {
        CheckNewType(name);
        ArgumentNullException.ThrowIfNull(elementType);
        if (elementType == TirType.Void)
        {
            throw new TirException($"Module '{Name}': array type {name} cannot have elements of type void.");
        }

        return AddType(new TirTypeDescriptor(this, _types.Count, name, TirTypeDescriptorKind.Array, null, elementType,
            null));
    }

    /// <summary>Adds the descriptor of an interface, printed as <c>type Program.IShape = interface</c>.</summary>
    /// <param name="name">The C# name, e.g. "Program.IShape"; unique among the module's descriptors.</param>
    public TirTypeDescriptor AddInterface(string name)
    {
        CheckNewType(name);
        return AddType(new TirTypeDescriptor(this, _types.Count, name, TirTypeDescriptorKind.Interface, null, null, null));
    }

    /// <summary>
    /// Adds a global array, printed as <c>global [4 x i32] @name = [1, 2, 0, 0]</c>, or as <c>constant ...</c> when
    /// it is read-only: a table that nothing may write (the verifier refuses a store it can see), which a code
    /// generator puts with the code, in flash on a chip, like <see cref="AddConstant"/>'s bytes. Either starts with
    /// <paramref name="elements"/>, or at zero.
    /// </summary>
    /// <param name="name">The C# display name, e.g. "Program.Digits"; unique within the module.</param>
    /// <param name="type">The array, of any element type (structs too, when it starts at zero).</param>
    /// <param name="elements">
    /// One constant of the element type per element, made with <c>new TirConst(...)</c> and not added to any block (so
    /// the elements are bools, integers or floats); null for every element zero.
    /// </param>
    /// <param name="isConstant">True for read-only data.</param>
    public TirGlobal AddArray(string name, TirArrayType type, IEnumerable<TirConst>? elements, bool isConstant)
    {
        CheckNewGlobal(name);
        ArgumentNullException.ThrowIfNull(type);
        TirConst[]? values = elements is null ? null : [.. elements];
        if (values is not null && values.Length != type.Count)
        {
            throw new TirException($"Module '{Name}': global array @{name} is {type}, but it is given " +
                $"{values.Length} elements.");
        }

        foreach (var value in values ?? [])
        {
            if (value.Type != type.Element)
            {
                throw new TirException($"Module '{Name}': global array @{name} holds {type.Element}, but one of its " +
                    $"elements is {value.Type}.");
            }

            if (value.Block is { } block)
            {
                throw new TirException($"Module '{Name}': an element of global array @{name}, " +
                    $"'{TirPrinter.Print(value)}', is an instruction of block '{block.Name}' of function " +
                    $"'{block.Function.Name}'; give constants that are in no block.");
            }
        }

        var global = new TirGlobal(this, _globals.Count, name, type, null, null, values, isConstant);
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

        foreach (var type in _types)
        {
            if ((type.ObjectType ?? type.ElementType) is { } held)
            {
                Visit(held);
            }
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

                    case TirArrayData data:
                        Visit(data.ElementType);
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

    private void CheckNewGlobal(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_globals.Any(g => g.Name == name))
        {
            throw new TirException($"Module '{Name}' already has a global named @{name}.");
        }
    }

    private void CheckNewType(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_types.Any(t => t.Name == name))
        {
            throw new TirException($"Module '{Name}' already has a type descriptor named {name}.");
        }
    }

    private TirTypeDescriptor AddType(TirTypeDescriptor type)
    {
        _types.Add(type);
        return type;
    }

    private TirFunction Add(TirFunction function)
    {
        _functions.Add(function);
        return function;
    }
}
