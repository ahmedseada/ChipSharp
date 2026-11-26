namespace ChipSharp.Frontend.Il;

/// <summary>
/// A method an instruction names (<c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c>, ...), resolved from its
/// token: defined in this assembly (then <see cref="Definition"/> is it) or in another one, e.g.
/// <c>void [System.Console]System.Console::WriteLine(int32)</c>. Every instruction that uses the same token gets the
/// same instance.
/// </summary>
public sealed class IlMethodReference
{
    internal IlMethodReference(IlType declaringType, string name, IlMethodSignature signature,
        IReadOnlyList<IlType> genericArguments, IlMethod? definition)
    {
        DeclaringType = declaringType;
        Name = name;
        Signature = signature;
        GenericArguments = genericArguments;
        Definition = definition;
    }

    /// <summary>
    /// The type the method belongs to: usually an <see cref="IlNamedType"/> (e.g. <c>[System.Console]System.Console</c>),
    /// an <see cref="IlGenericInstanceType"/> for a method of a generic type, or an array type for the methods of
    /// multi-dimensional arrays.
    /// </summary>
    public IlType DeclaringType { get; }

    /// <summary>The name, e.g. "WriteLine"; constructors are ".ctor".</summary>
    public string Name { get; }

    /// <summary>The signature as the call site names it (for a method of a generic type, in terms of <c>!0</c>, <c>!!0</c>).</summary>
    public IlMethodSignature Signature { get; }

    public IlType ReturnType => Signature.ReturnType;

    /// <summary>The parameter types, without <c>this</c>.</summary>
    public IReadOnlyList<IlType> ParameterTypes => Signature.ParameterTypes;

    /// <summary>True for an instance method: the call passes <c>this</c> first.</summary>
    public bool HasThis => Signature.HasThis;

    /// <summary>The type arguments of a generic method instance, e.g. <c>Id&lt;int32&gt;</c>; empty otherwise.</summary>
    public IReadOnlyList<IlType> GenericArguments { get; }

    /// <summary>The method's definition when it is defined in this assembly; null for a method of another assembly.</summary>
    public IlMethod? Definition { get; }

    /// <summary>True when the declaring type is defined in the assembly being read.</summary>
    public bool IsDefinedInThisAssembly => Definition is not null || IlTypeNames.IsDefinedInThisAssembly(DeclaringType);

    /// <summary>The declaring type's full name, a dot and the name, e.g. "System.Console.WriteLine" or "Program.Square".</summary>
    public string DisplayName => $"{IlTypeNames.DisplayName(DeclaringType)}.{Name}";

    /// <summary>In ildasm style, e.g. "instance void [System.Runtime]System.Object::.ctor()".</summary>
    public override string ToString()
    {
        var instance = HasThis ? "instance " : "";
        var arguments = GenericArguments.Count > 0 ? $"<{string.Join(", ", GenericArguments)}>" : "";
        return $"{IlMethodSignature.ConventionText(Signature.CallingConvention)}{instance}{ReturnType} " +
            $"{DeclaringType}::{Name}{arguments}({string.Join(", ", ParameterTypes)})";
    }
}

/// <summary>
/// A field an instruction names (<c>ldfld</c>, <c>stsfld</c>, ...), resolved from its token. Every instruction that
/// uses the same token gets the same instance.
/// </summary>
public sealed class IlFieldReference
{
    internal IlFieldReference(IlType declaringType, string name, IlType fieldType, IlField? definition)
    {
        DeclaringType = declaringType;
        Name = name;
        FieldType = fieldType;
        Definition = definition;
    }

    /// <summary>The type the field belongs to: an <see cref="IlNamedType"/> or an <see cref="IlGenericInstanceType"/>.</summary>
    public IlType DeclaringType { get; }

    public string Name { get; }

    public IlType FieldType { get; }

    /// <summary>The field's definition when it is defined in this assembly; null for a field of another assembly.</summary>
    public IlField? Definition { get; }

    /// <summary>True when the declaring type is defined in the assembly being read.</summary>
    public bool IsDefinedInThisAssembly => Definition is not null || IlTypeNames.IsDefinedInThisAssembly(DeclaringType);

    /// <summary>The declaring type's full name, a dot and the name, e.g. "Program.counter".</summary>
    public string DisplayName => $"{IlTypeNames.DisplayName(DeclaringType)}.{Name}";

    /// <summary>In ildasm style, e.g. "int32 Program::counter".</summary>
    public override string ToString() => $"{FieldType} {DeclaringType}::{Name}";
}

internal static class IlTypeNames
{
    public static bool IsDefinedInThisAssembly(IlType type) => type switch
    {
        IlNamedType named => named.IsDefinedInThisAssembly,
        IlGenericInstanceType { Definition: IlNamedType named } => named.IsDefinedInThisAssembly,
        _ => false,
    };

    public static string DisplayName(IlType type) => type switch
    {
        IlNamedType named => named.FullName,
        IlGenericInstanceType { Definition: IlNamedType named } => named.FullName,
        _ => type.ToString(),
    };
}
