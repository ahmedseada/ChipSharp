using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Types and methods written the way C# writes them, for messages to people who wrote C#, not IL:
/// <c>int</c> rather than <c>int32</c>, <c>List&lt;int&gt;</c> rather than <c>List`1&lt;int32&gt;</c>,
/// <c>ref int</c> rather than <c>int32&amp;</c>.
/// </summary>
public static class CSharpNames
{
    private static readonly Dictionary<string, string> KeywordTypes = new(StringComparer.Ordinal)
    {
        ["Void"] = "void", ["Boolean"] = "bool", ["Char"] = "char", ["SByte"] = "sbyte", ["Byte"] = "byte",
        ["Int16"] = "short", ["UInt16"] = "ushort", ["Int32"] = "int", ["UInt32"] = "uint", ["Int64"] = "long",
        ["UInt64"] = "ulong", ["Single"] = "float", ["Double"] = "double", ["Decimal"] = "decimal",
        ["IntPtr"] = "nint", ["UIntPtr"] = "nuint", ["Object"] = "object", ["String"] = "string",
    };

    /// <summary>
    /// A type as C# writes it, e.g. "int", "string[]", "int?", "System.Collections.Generic.List&lt;int&gt;".
    /// <paramref name="genericParameter"/> names a generic parameter (<c>!0</c>, <c>!!0</c>); without it, or when it
    /// returns null, the parameter is written the IL way.
    /// </summary>
    public static string Type(IlType type, Func<IlGenericParameterType, string?>? genericParameter = null)
    {
        ArgumentNullException.ThrowIfNull(type);

        string Name(IlType inner) => Type(inner, genericParameter);

        return type switch
        {
            IlPrimitiveType primitive => Keyword(primitive.Kind),
            IlNamedType named => Named(named),
            IlGenericInstanceType { Definition: IlNamedType { Namespace: "System", Name: "Nullable`1" }, Arguments: [var value] } =>
                $"{Name(value)}?",
            IlGenericInstanceType instance =>
                $"{(instance.Definition is IlNamedType named ? Named(named) : Name(instance.Definition))}" +
                $"<{string.Join(", ", instance.Arguments.Select(Name))}>",
            IlArrayType array => $"{Name(array.ElementType)}[]",
            IlMultiDimensionalArrayType array => $"{Name(array.ElementType)}[{new string(',', array.Rank - 1)}]",
            IlPointerType pointer => $"{Name(pointer.ElementType)}*",
            IlByRefType byRef => $"ref {Name(byRef.ElementType)}",
            IlPinnedType pinned => Name(pinned.ElementType),
            IlModifiedType modified => Name(modified.UnmodifiedType),
            IlFunctionPointerType pointer =>
                $"delegate*<{string.Join(", ", pointer.Signature.ParameterTypes.Append(pointer.Signature.ReturnType).Select(Name))}>",
            IlGenericParameterType parameter => genericParameter?.Invoke(parameter) ?? parameter.ToString(),
            _ => type.ToString(),
        };
    }

    /// <summary>
    /// A type in the signature or body of <paramref name="method"/>, its generic parameters named as the method
    /// and its type declare them, e.g. "T[]".
    /// </summary>
    public static string Type(IlType type, IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);

        return Type(type, parameter =>
        {
            var names = parameter.IsMethodParameter ? method.GenericParameters : method.DeclaringType.GenericParameters;
            return parameter.Index < names.Count ? names[parameter.Index] : null;
        });
    }

    /// <summary>
    /// A method of the program: its display name, its own type parameters and its parameter types,
    /// e.g. "Program.Square(int)" or "Program.Id&lt;T&gt;(T)".
    /// </summary>
    public static string Method(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);

        var typeParameters = method.GenericParameters.Count > 0 ? $"<{string.Join(", ", method.GenericParameters)}>" : "";
        var parameters = method.Parameters.Select(p => Type(p.Type, method));
        return $"{method.DisplayName}{typeParameters}({string.Join(", ", parameters)})";
    }

    /// <summary>
    /// A method an instruction names, usually a library method, with the type arguments of the call filled in,
    /// e.g. "System.Console.WriteLine(string)" or "System.Collections.Generic.List&lt;int&gt;.Add(int)".
    /// </summary>
    public static string Method(IlMethodReference method)
    {
        ArgumentNullException.ThrowIfNull(method);

        string? Argument(IlGenericParameterType parameter)
        {
            var arguments = parameter.IsMethodParameter
                ? method.GenericArguments
                : (method.DeclaringType as IlGenericInstanceType)?.Arguments ?? [];
            return parameter.Index < arguments.Count ? Type(arguments[parameter.Index]) : null;
        }

        var typeArguments = method.GenericArguments.Count > 0
            ? $"<{string.Join(", ", method.GenericArguments.Select(a => Type(a)))}>"
            : "";
        var parameters = method.ParameterTypes.Select(p => Type(p, Argument));
        return $"{Type(method.DeclaringType)}.{method.Name}{typeArguments}({string.Join(", ", parameters)})";
    }

    private static string Keyword(IlPrimitiveKind kind) => kind switch
    {
        IlPrimitiveKind.Void => "void",
        IlPrimitiveKind.Bool => "bool",
        IlPrimitiveKind.Char => "char",
        IlPrimitiveKind.Int8 => "sbyte",
        IlPrimitiveKind.UInt8 => "byte",
        IlPrimitiveKind.Int16 => "short",
        IlPrimitiveKind.UInt16 => "ushort",
        IlPrimitiveKind.Int32 => "int",
        IlPrimitiveKind.UInt32 => "uint",
        IlPrimitiveKind.Int64 => "long",
        IlPrimitiveKind.UInt64 => "ulong",
        IlPrimitiveKind.Float32 => "float",
        IlPrimitiveKind.Float64 => "double",
        IlPrimitiveKind.NativeInt => "nint",
        IlPrimitiveKind.NativeUInt => "nuint",
        IlPrimitiveKind.Object => "object",
        IlPrimitiveKind.String => "string",
        IlPrimitiveKind.TypedReference => "System.TypedReference",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Namespace-qualified, nested types after a dot, without the arity: "Demo.Outer.Inner", "System.Span";
    /// a library type C# has a keyword for by that keyword, e.g. "string" for System.String.
    /// </summary>
    private static string Named(IlNamedType type)
    {
        if (type is { Namespace: "System", DeclaringType: null, IsDefinedInThisAssembly: false }
            && KeywordTypes.TryGetValue(type.Name, out var keyword))
        {
            return keyword;
        }

        var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        var name = tick < 0 ? type.Name : type.Name[..tick];
        return type.DeclaringType is not null ? $"{Named(type.DeclaringType)}.{name}"
            : type.Namespace.Length == 0 ? name
            : $"{type.Namespace}.{name}";
    }
}
