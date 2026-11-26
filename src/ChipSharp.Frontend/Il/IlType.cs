namespace ChipSharp.Frontend.Il;

/// <summary>
/// A type as IL writes it: in a signature (parameters, return type, locals, fields) or as an instruction's
/// operand. Types are represented, not interpreted: C#'s <c>int</c> is <see cref="Int32"/>, a class or struct is
/// an <see cref="IlNamedType"/> that names it. Types are records, so two types are equal when they are written
/// the same way. <c>ToString()</c> writes the type in ildasm style, e.g. <c>int32</c>, <c>string[]</c>,
/// <c>[System.Console]System.Console</c>.
/// </summary>
public abstract record IlType
{
    public static IlPrimitiveType Void { get; } = new(IlPrimitiveKind.Void);

    public static IlPrimitiveType Bool { get; } = new(IlPrimitiveKind.Bool);

    public static IlPrimitiveType Char { get; } = new(IlPrimitiveKind.Char);

    public static IlPrimitiveType Int8 { get; } = new(IlPrimitiveKind.Int8);

    public static IlPrimitiveType UInt8 { get; } = new(IlPrimitiveKind.UInt8);

    public static IlPrimitiveType Int16 { get; } = new(IlPrimitiveKind.Int16);

    public static IlPrimitiveType UInt16 { get; } = new(IlPrimitiveKind.UInt16);

    public static IlPrimitiveType Int32 { get; } = new(IlPrimitiveKind.Int32);

    public static IlPrimitiveType UInt32 { get; } = new(IlPrimitiveKind.UInt32);

    public static IlPrimitiveType Int64 { get; } = new(IlPrimitiveKind.Int64);

    public static IlPrimitiveType UInt64 { get; } = new(IlPrimitiveKind.UInt64);

    public static IlPrimitiveType Float32 { get; } = new(IlPrimitiveKind.Float32);

    public static IlPrimitiveType Float64 { get; } = new(IlPrimitiveKind.Float64);

    public static IlPrimitiveType NativeInt { get; } = new(IlPrimitiveKind.NativeInt);

    public static IlPrimitiveType NativeUInt { get; } = new(IlPrimitiveKind.NativeUInt);

    public static IlPrimitiveType Object { get; } = new(IlPrimitiveKind.Object);

    public static IlPrimitiveType String { get; } = new(IlPrimitiveKind.String);

    public static IlPrimitiveType TypedReference { get; } = new(IlPrimitiveKind.TypedReference);
}

/// <summary>The types a signature names with a single element type code rather than a type token.</summary>
public enum IlPrimitiveKind
{
    Void,
    Bool,
    Char,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
    NativeInt,
    NativeUInt,
    Object,
    String,
    TypedReference,
}

/// <summary>A primitive: <c>void</c>, <c>bool</c>, <c>char</c>, <c>int8</c> to <c>uint64</c>, <c>float32</c>,
/// <c>float64</c>, <c>native int</c>, <c>native uint</c>, <c>object</c>, <c>string</c> or <c>typedref</c>.</summary>
public sealed record IlPrimitiveType(IlPrimitiveKind Kind) : IlType
{
    public override string ToString() => Kind switch
    {
        IlPrimitiveKind.Void => "void",
        IlPrimitiveKind.Bool => "bool",
        IlPrimitiveKind.Char => "char",
        IlPrimitiveKind.Int8 => "int8",
        IlPrimitiveKind.UInt8 => "uint8",
        IlPrimitiveKind.Int16 => "int16",
        IlPrimitiveKind.UInt16 => "uint16",
        IlPrimitiveKind.Int32 => "int32",
        IlPrimitiveKind.UInt32 => "uint32",
        IlPrimitiveKind.Int64 => "int64",
        IlPrimitiveKind.UInt64 => "uint64",
        IlPrimitiveKind.Float32 => "float32",
        IlPrimitiveKind.Float64 => "float64",
        IlPrimitiveKind.NativeInt => "native int",
        IlPrimitiveKind.NativeUInt => "native uint",
        IlPrimitiveKind.Object => "object",
        IlPrimitiveKind.String => "string",
        IlPrimitiveKind.TypedReference => "typedref",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// A class, struct, enum, interface or delegate, by name. <paramref name="Namespace"/> is empty for a nested type,
/// whose enclosing type is <paramref name="DeclaringType"/>. <paramref name="Assembly"/> is the assembly (or module)
/// the type lives in, e.g. "System.Console", or null for a type defined in the assembly being read.
/// A generic type keeps its arity in its name, e.g. <c>List`1</c>.
/// </summary>
public sealed record IlNamedType(string Namespace, string Name, IlNamedType? DeclaringType, string? Assembly) : IlType
{
    /// <summary>Namespace-qualified, with "+" before a nested type's name, e.g. "Demo.Outer+Inner".</summary>
    public string FullName => DeclaringType is not null
        ? $"{DeclaringType.FullName}+{Name}"
        : Namespace.Length == 0 ? Name : $"{Namespace}.{Name}";

    /// <summary>True for a type defined in the assembly being read.</summary>
    public bool IsDefinedInThisAssembly => Assembly is null;

    public override string ToString() => Assembly is null ? FullName : $"[{Assembly}]{FullName}";
}

/// <summary>A single-dimensional array with a zero lower bound (C#'s <c>T[]</c>): <c>int32[]</c>.</summary>
public sealed record IlArrayType(IlType ElementType) : IlType
{
    public override string ToString() => $"{ElementType}[]";
}

/// <summary>A multi-dimensional array (C#'s <c>T[,]</c>): <c>int32[,]</c>. Sizes and lower bounds are not kept.</summary>
public sealed record IlMultiDimensionalArrayType(IlType ElementType, int Rank) : IlType
{
    public override string ToString() => Rank == 1 ? $"{ElementType}[*]" : $"{ElementType}[{new string(',', Rank - 1)}]";
}

/// <summary>An unmanaged pointer: <c>int32*</c>.</summary>
public sealed record IlPointerType(IlType ElementType) : IlType
{
    public override string ToString() => $"{ElementType}*";
}

/// <summary>A managed reference (C#'s <c>ref</c>, <c>out</c>, <c>in</c>): <c>int32&amp;</c>.</summary>
public sealed record IlByRefType(IlType ElementType) : IlType
{
    public override string ToString() => $"{ElementType}&";
}

/// <summary>A local that pins what it holds (C#'s <c>fixed</c>): <c>uint8&amp; pinned</c>.</summary>
public sealed record IlPinnedType(IlType ElementType) : IlType
{
    public override string ToString() => $"{ElementType} pinned";
}

/// <summary>
/// A generic type with its type arguments, e.g. <c>[System.Collections]System.Collections.Generic.List`1&lt;int32&gt;</c>.
/// </summary>
public sealed record IlGenericInstanceType(IlType Definition, IReadOnlyList<IlType> Arguments) : IlType
{
    public bool Equals(IlGenericInstanceType? other) =>
        other is not null && Definition == other.Definition && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Definition);
        foreach (var argument in Arguments)
        {
            hash.Add(argument);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"{Definition}<{string.Join(", ", Arguments)}>";
}

/// <summary>
/// A generic parameter by position: of the enclosing type (<c>!0</c>) or, when
/// <paramref name="IsMethodParameter"/>, of the method (<c>!!0</c>).
/// </summary>
public sealed record IlGenericParameterType(int Index, bool IsMethodParameter) : IlType
{
    public override string ToString() => $"{(IsMethodParameter ? "!!" : "!")}{Index}";
}

/// <summary>A function pointer (C#'s <c>delegate*</c>): <c>method int32 *(int32)</c>.</summary>
public sealed record IlFunctionPointerType(IlMethodSignature Signature) : IlType
{
    public override string ToString()
    {
        var conventionText = IlMethodSignature.ConventionText(Signature.CallingConvention);
        var instance = Signature.HasThis ? "instance " : "";
        return $"method {conventionText}{instance}{Signature.ReturnType} *({string.Join(", ", Signature.ParameterTypes)})";
    }
}

/// <summary>
/// A type with a custom modifier, e.g. <c>int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsVolatile)</c>.
/// <paramref name="IsRequired"/> tells modreq from modopt.
/// </summary>
public sealed record IlModifiedType(IlType UnmodifiedType, IlType Modifier, bool IsRequired) : IlType
{
    public override string ToString() => $"{UnmodifiedType} {(IsRequired ? "modreq" : "modopt")}({Modifier})";
}
