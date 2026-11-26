using System.Reflection;

namespace ChipSharp.Frontend.Il;

/// <summary>
/// A type defined in the assembly being read: a class, struct, enum, interface or delegate, with its methods
/// and fields. Nested types are listed in <see cref="IlAssembly.Types"/> too, with their <see cref="DeclaringType"/>.
/// </summary>
public sealed class IlTypeDefinition
{
    private readonly List<IlMethod> _methods = [];
    private readonly List<IlField> _fields = [];

    internal IlTypeDefinition(IlAssembly assembly, int token, IlNamedType type, TypeAttributes attributes,
        IlType? baseType, IReadOnlyList<string> genericParameters)
    {
        Assembly = assembly;
        Token = token;
        Type = type;
        Attributes = attributes;
        BaseType = baseType;
        GenericParameters = genericParameters;
    }

    public IlAssembly Assembly { get; }

    /// <summary>The metadata token, e.g. 0x02000002.</summary>
    public int Token { get; }

    /// <summary>The type as a signature names it, to compare with the types in signatures and operands.</summary>
    public IlNamedType Type { get; }

    /// <summary>The namespace; empty for a nested type and for a type outside any namespace.</summary>
    public string Namespace => Type.Namespace;

    /// <summary>The name without namespace or enclosing type, e.g. "Inner"; a generic type keeps its arity, e.g. "Box`1".</summary>
    public string Name => Type.Name;

    /// <summary>Namespace-qualified, with "+" before a nested type's name, e.g. "Demo.Outer+Inner".</summary>
    public string FullName => Type.FullName;

    /// <summary>The type this one is nested in, or null.</summary>
    public IlTypeDefinition? DeclaringType { get; internal set; }

    /// <summary>What the type extends; null for interfaces, <c>&lt;Module&gt;</c> and System.Object itself.</summary>
    public IlType? BaseType { get; }

    public TypeAttributes Attributes { get; }

    public bool IsInterface => (Attributes & TypeAttributes.Interface) != 0;

    /// <summary>The names of the type's own generic parameters, in order (e.g. "T"); empty when it is not generic.</summary>
    public IReadOnlyList<string> GenericParameters { get; }

    /// <summary>
    /// For an instance of a generic type of the program (<c>Pair&lt;int&gt;</c>, see
    /// <see cref="Generics.Instantiations"/>), the generic type it is made from (<c>Pair&lt;T&gt;</c>); null otherwise.
    /// </summary>
    public IlTypeDefinition? GenericDefinition { get; internal init; }

    /// <summary>The type arguments of an instance of a generic type (<c>int32</c> for <c>Pair&lt;int&gt;</c>); empty otherwise.</summary>
    public IReadOnlyList<IlType> TypeArguments { get; internal init; } = [];

    /// <summary>The methods (static and instance, constructors included) in metadata order.</summary>
    public IReadOnlyList<IlMethod> Methods => _methods;

    /// <summary>The fields (static and instance) in metadata order.</summary>
    public IReadOnlyList<IlField> Fields => _fields;

    public override string ToString() => FullName;

    internal void Add(IlMethod method) => _methods.Add(method);

    internal void Add(IlField field) => _fields.Add(field);
}

/// <summary>
/// A method defined in the assembly being read. <see cref="DisplayName"/> is the C# name, e.g. "Program.Square";
/// overloads share it, so <see cref="Signature"/> (or <c>ToString()</c>, e.g. "Program.Square(int32)") tells them apart.
/// The body is decoded the first time <see cref="Body"/> is read.
/// </summary>
public sealed class IlMethod
{
    private readonly Lazy<IlMethodBody?> _body;

    internal IlMethod(IlTypeDefinition declaringType, int token, string name, MethodAttributes attributes,
        MethodImplAttributes implAttributes, IlMethodSignature signature, IReadOnlyList<IlParameter> parameters,
        IReadOnlyList<string> genericParameters, int relativeVirtualAddress)
    {
        DeclaringType = declaringType;
        Token = token;
        Name = name;
        Attributes = attributes;
        ImplAttributes = implAttributes;
        Signature = signature;
        Parameters = parameters;
        GenericParameters = genericParameters;
        HasBody = relativeVirtualAddress != 0;
        _body = new Lazy<IlMethodBody?>(() => HasBody ? declaringType.Assembly.ReadBody(this, relativeVirtualAddress) : null);
    }

    /// <summary>
    /// An instance of <paramref name="generic"/>, a generic method or a method of a generic type, for type arguments
    /// (see <see cref="Generics.Instantiations"/>): a method of its own, of <paramref name="declaringType"/>, whose
    /// signature and parameters have the arguments in place of the type parameters, and whose IL
    /// <paramref name="body"/> makes the first time it is asked for.
    /// </summary>
    internal IlMethod(IlTypeDefinition declaringType, IlMethod generic, string name, IlMethodSignature signature,
        IReadOnlyList<IlParameter> parameters, IReadOnlyList<IlType> methodArguments, Func<IlMethod, IlMethodBody?> body)
    {
        DeclaringType = declaringType;
        Token = generic.Token;
        Name = name;
        Attributes = generic.Attributes;
        ImplAttributes = generic.ImplAttributes;
        Signature = signature;
        Parameters = parameters;
        GenericParameters = [];
        GenericDefinition = generic;
        MethodArguments = methodArguments;
        HasBody = generic.HasBody;
        _body = new Lazy<IlMethodBody?>(() => HasBody ? body(this) : null);
    }

    public IlTypeDefinition DeclaringType { get; }

    public IlAssembly Assembly => DeclaringType.Assembly;

    /// <summary>The metadata token, e.g. 0x06000001.</summary>
    public int Token { get; }

    /// <summary>The name, e.g. "Square"; constructors are ".ctor" and ".cctor".</summary>
    public string Name { get; }

    /// <summary>The declaring type's full name, a dot and the method's name, e.g. "Program.Square" or "Demo.Outer+Inner.Run".</summary>
    public string DisplayName => $"{DeclaringType.FullName}.{Name}";

    public IlMethodSignature Signature { get; }

    public IlType ReturnType => Signature.ReturnType;

    /// <summary>
    /// The declared parameters, without <c>this</c>. In an instance method parameter i is IL argument i + 1,
    /// because argument 0 is <c>this</c>; in a static method parameter i is argument i.
    /// </summary>
    public IReadOnlyList<IlParameter> Parameters { get; }

    /// <summary>The names of the method's own generic parameters, in order; empty when it is not generic.</summary>
    public IReadOnlyList<string> GenericParameters { get; }

    /// <summary>
    /// For an instance of a generic method (<c>Program.Max&lt;int&gt;</c>) or of a method of a generic type
    /// (<c>Pair&lt;int&gt;.Swap</c>), the method it is made from; null otherwise. Its type arguments are
    /// <see cref="MethodArguments"/> and its type's <see cref="IlTypeDefinition.TypeArguments"/>.
    /// </summary>
    public IlMethod? GenericDefinition { get; }

    /// <summary>The type arguments of an instance of a generic method (<c>int32</c> for <c>Max&lt;int&gt;</c>); empty otherwise.</summary>
    public IReadOnlyList<IlType> MethodArguments { get; } = [];

    public MethodAttributes Attributes { get; }

    public MethodImplAttributes ImplAttributes { get; }

    public bool IsStatic => (Attributes & MethodAttributes.Static) != 0;

    /// <summary>True when this is the assembly's entry point (C#'s <c>Main</c>).</summary>
    public bool IsEntryPoint => ReferenceEquals(Assembly.EntryPoint, this);

    /// <summary>False for a method without IL: abstract, extern, or implemented by the runtime (delegates).</summary>
    public bool HasBody { get; }

    /// <summary>
    /// The decoded IL, or null when <see cref="HasBody"/> is false. Decoded on first use; malformed IL throws a
    /// <see cref="ChipSharp.Abstractions.ChipSharpException"/> that names the file, the method and the offset.
    /// </summary>
    public IlMethodBody? Body => _body.Value;

    /// <summary>The display name and the parameter types, e.g. "Program.Square(int32)".</summary>
    public override string ToString() => $"{DisplayName}({string.Join(", ", Signature.ParameterTypes)})";
}

/// <summary>
/// A declared parameter: its position among the parameters (from 0, not counting <c>this</c>), name and type.
/// A parameter without a name in the metadata gets ildasm's name for it, A_ and its argument number.
/// </summary>
public sealed record IlParameter(int Index, string Name, IlType Type)
{
    public override string ToString() => $"{Type} {Name}";
}

/// <summary>A field defined in the assembly being read.</summary>
public sealed class IlField
{
    internal IlField(IlTypeDefinition declaringType, int token, string name, FieldAttributes attributes, IlType type)
    {
        DeclaringType = declaringType;
        Token = token;
        Name = name;
        Attributes = attributes;
        Type = type;
    }

    public IlTypeDefinition DeclaringType { get; }

    /// <summary>The metadata token, e.g. 0x04000001.</summary>
    public int Token { get; }

    public string Name { get; }

    public FieldAttributes Attributes { get; }

    public IlType Type { get; }

    public bool IsStatic => (Attributes & FieldAttributes.Static) != 0;

    /// <summary>E.g. "int32 Program::counter".</summary>
    public override string ToString() => $"{Type} {DeclaringType.FullName}::{Name}";
}
