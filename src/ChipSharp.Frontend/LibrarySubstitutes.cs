using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// Library substitutes: ChipSharp's own C# for chosen library types, in ChipSharp.Library (next to this assembly),
/// which ChipSharp compiles together with a program that uses them. A type of ChipSharp.Library marked
/// <c>[Substitute(typeof(T))]</c> stands for the library type T:
/// <list type="bullet">
/// <item>a call to a method of T, or a use of a field of T, is linked to the substitute's public member with the same
/// name and signature (in which the substitute stands for T): the reference's <see cref="IlMethodReference.Definition"/>
/// (<see cref="IlFieldReference.Definition"/>) is that member. The whole-program pass and the translation then take it
/// for a method (field) of the program: they reach it, check it and compile it, and what it uses, like the program's
/// own. Its type's initializer runs before Main like any other;</item>
/// <item>T as a type (of a local, field, parameter, ...) is the substitute (<see cref="FindType"/>): a struct T has the
/// substitute's fields, and the program's T and the substitute are one TIR struct type;</item>
/// <item>a member of T that the substitute does not have stays a library member, which the whole-program pass reports
/// as not available.</item>
/// </list>
/// The substitutes reach the runtime through ChipSharp.Library.Runtime (and ChipRuntime, the functions only a chip with
/// the hardware has): a call to one of its methods is a call to the runtime function of the same name, e.g.
/// ChipSharp.Runtime.TimeSpanOverflow (<see cref="TryMapRuntimeFunction"/>), whose .NET body is left out.
/// </summary>
/// <remarks>
/// <see cref="IlAssembly"/> links every method and field reference it reads
/// (<see cref="Link(IlAssembly, IlMethodReference)"/>), in the library as in the program: a substitute may use another
/// library type's members, which link to that type's substitute (ChipSharp.Hardware.Timing.Delay's reads
/// TimeSpan.Ticks). A new substitute is C# in ChipSharp.Library, written in the C# ChipSharp compiles, not compiler code.
/// </remarks>
internal static class LibrarySubstitutes
{
    /// <summary>The library's file, next to ChipSharp.Frontend.dll.</summary>
    public const string FileName = "ChipSharp.Library.dll";

    /// <summary>The attribute that marks a substitute.</summary>
    public const string AttributeType = "ChipSharp.Library.SubstituteAttribute";

    /// <summary>The library's class of runtime functions.</summary>
    public const string RuntimeType = "ChipSharp.Library.Runtime";

    /// <summary>The library's class of the runtime functions of a chip's hardware (ChipSharp.Hardware's buses).</summary>
    public const string ChipRuntimeType = "ChipSharp.Library.ChipRuntime";

    /// <summary>The C# operators by the names of their methods (op_Addition is +).</summary>
    private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
    {
        ["Addition"] = "+", ["UnaryPlus"] = "+", ["Subtraction"] = "-", ["UnaryNegation"] = "-", ["Multiply"] = "*",
        ["Division"] = "/", ["Modulus"] = "%", ["Equality"] = "==", ["Inequality"] = "!=", ["LessThan"] = "<",
        ["LessThanOrEqual"] = "<=", ["GreaterThan"] = ">", ["GreaterThanOrEqual"] = ">=",
    };

    private static readonly Lazy<Library> Loaded = new(() => new Library(IlAssembly.Load(FindFile())));

    /// <summary>ChipSharp.Library, as ChipSharp reads it.</summary>
    public static IlAssembly Assembly => Loaded.Value.Assembly;

    /// <summary>Every substitute, with the full name of the library type it stands for, e.g. "System.TimeSpan".</summary>
    public static IReadOnlyDictionary<IlTypeDefinition, string> Substituted => Loaded.Value.Substituted;

    /// <summary>
    /// The library members the substitutes stand for, as C# names them: "System.TimeSpan.FromSeconds(long)", a
    /// property or field without parentheses ("System.TimeSpan.Ticks"), a constructor as
    /// "System.TimeSpan.new TimeSpan(long)" and an operator as "System.TimeSpan.operator +(System.TimeSpan,
    /// System.TimeSpan)": a substitute's public members except its type initializer, in the order its C# declares them.
    /// </summary>
    public static IReadOnlyList<string> Members => Loaded.Value.Members.Value;

    /// <summary>The substitute for <paramref name="type"/>, a type of another assembly; null when it has none.</summary>
    public static IlTypeDefinition? FindType(IlNamedType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsDefinedInThisAssembly ? null : Loaded.Value.Substitutes.GetValueOrDefault(type.FullName);
    }

    /// <summary>
    /// The full name of the library type that <paramref name="type"/>, a substitute as ChipSharp.Library's IL names it
    /// (<c>ChipSharp.Library.List`1</c>), stands for (<c>System.Collections.Generic.List`1</c>); null for any other type.
    /// What a descriptor of a substitute's class says its name is, as .NET's.
    /// </summary>
    public static string? SubstitutedName(IlNamedType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsDefinedInThisAssembly && type.FullName.StartsWith("ChipSharp.Library.", StringComparison.Ordinal)
            && Assembly.FindType(type.FullName) is { } definition && Substituted.TryGetValue(definition, out var name)
            ? name
            : null;
    }

    /// <summary>
    /// True for a method of the library: a substitute's member, or what it uses, also an instance of its generic code over
    /// the program's types (made in the program, see Generics.Instantiations).
    /// </summary>
    public static bool Defines(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method.Assembly == Assembly || method.GenericDefinition?.Assembly == Assembly;
    }

    /// <summary>
    /// <paramref name="reference"/>, which <paramref name="owner"/>'s IL makes, linked: to the member of a substitute
    /// that stands for the library method it names, and away from a runtime function of the library, which is then a
    /// library method, not one to compile. Any other reference as it is.
    /// </summary>
    public static IlMethodReference Link(IlAssembly owner, IlMethodReference reference)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(reference);

        if (reference.Definition is { } definition)
        {
            return definition.DeclaringType.FullName is RuntimeType or ChipRuntimeType && Defines(definition)
                ? new IlMethodReference(reference.DeclaringType, reference.Name, reference.Signature,
                    reference.GenericArguments, null)
                : reference;
        }

        if (reference.GenericArguments.Count == 0 && reference.DeclaringType is IlNamedType named
            && FindType(named) is { } type
            && type.Methods.FirstOrDefault(method => IsPublic(method.Attributes) && method.Name == reference.Name
                && SameSignature(method.Signature, reference.Signature, owner)) is { } substitute)
        {
            return new IlMethodReference(reference.DeclaringType, reference.Name, reference.Signature, [], substitute);
        }

        return reference;
    }

    /// <summary>
    /// True when <paramref name="method"/>, a substitute's, has the signature of the library method that
    /// <paramref name="owner"/>'s IL names with <paramref name="signature"/> (a substitute in it standing for its type).
    /// </summary>
    public static bool HasSignature(IlMethod method, IlMethodSignature signature, IlAssembly owner)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(owner);
        return SameSignature(method.Signature, signature, owner);
    }

    /// <summary>
    /// <paramref name="reference"/>, which <paramref name="owner"/>'s IL makes, linked to the field of a substitute
    /// that stands for the library field it names (e.g. TimeSpan.Zero); any other reference as it is.
    /// </summary>
    public static IlFieldReference Link(IlAssembly owner, IlFieldReference reference)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(reference);

        // (a field of a generic type, ValueTuple<int, int>.Item1, is the generic substitute's, which Instantiations then
        // links to its instance)
        var declaring = reference.DeclaringType is IlGenericInstanceType { Definition: IlNamedType generic } ? generic
            : reference.DeclaringType as IlNamedType;
        if (reference.Definition is null && declaring is { } named && FindType(named) is { } type
            && type.Fields.FirstOrDefault(field => IsPublic(field.Attributes) && field.Name == reference.Name
                && Key(field.Type, Assembly) == Key(reference.FieldType, owner)) is { } substitute)
        {
            return new IlFieldReference(reference.DeclaringType, reference.Name, reference.FieldType, substitute);
        }

        return reference;
    }

    /// <summary>
    /// The runtime function a call to a method of ChipSharp.Library.Runtime (or ChipRuntime) becomes, e.g.
    /// ChipSharp.Runtime.TimeSpanOverflow for Runtime.TimeSpanOverflow(); false for any other method.
    /// </summary>
    public static bool TryMapRuntimeFunction(IlMethodReference method, [NotNullWhen(true)] out ExternalFunction? function)
    {
        ArgumentNullException.ThrowIfNull(method);
        function = null;
        if (method is not { HasThis: false, Definition: null, GenericArguments.Count: 0 }
            || method.DeclaringType is not IlNamedType
            {
                IsDefinedInThisAssembly: true, FullName: RuntimeType or ChipRuntimeType,
            } runtime
            || Assembly.FindType(runtime.FullName)?.Methods.FirstOrDefault(m => IsPublic(m.Attributes) && m.IsStatic
                && m.Name == method.Name && m.Signature == method.Signature) is not { } definition)
        {
            return false;
        }

        // as for KnownExternals, a bool and a char are passed and returned as an i32, a string as a ref; a reference
        // (ref char, of the chars of a string) as a ptr
        var parameters = new List<TirType>();
        foreach (var parameter in definition.Signature.ParameterTypes)
        {
            if (RuntimeFunctionType(parameter) is not { } type)
            {
                return false;
            }

            parameters.Add(type);
        }

        if (RuntimeFunctionType(definition.ReturnType) is not { } returns)
        {
            return false;
        }

        function = new ExternalFunction($"ChipSharp.Runtime.{definition.Name}", returns, parameters,
            CSharpNames.Method(definition));
        return true;
    }

    /// <summary>The TIR type a runtime function takes or returns for <paramref name="type"/>; null when it cannot.</summary>
    private static TirType? RuntimeFunctionType(IlType type) => type switch
    {
        IlPrimitiveType { Kind: IlPrimitiveKind.Bool or IlPrimitiveKind.Char } => TirType.I32,
        IlPrimitiveType { Kind: IlPrimitiveKind.String } => TirType.Ref,
        IlByRefType reference when RuntimeFunctionType(reference.ElementType) is not null => TirType.Ptr,
        _ => SupportedSubset.TryMapType(type, out var mapped) ? mapped : null,
    };

    /// <summary>The members of <paramref name="type"/>, the substitute for <paramref name="library"/>: see Members.</summary>
    private static IEnumerable<string> MembersOf(IlTypeDefinition type, string library)
    {
        // a generic type as C# writes it: System.Collections.Generic.EqualityComparer<T>, and a type nested in one:
        // System.Collections.Generic.Dictionary<TKey, TValue>.KeyCollection
        var given = 0;
        var segments = library.Split('+');
        for (var s = 0; s < segments.Length; s++)
        {
            if (segments[s].IndexOf('`', StringComparison.Ordinal) is > 0 and var tick
                && int.TryParse(segments[s][(tick + 1)..], out var arity))
            {
                segments[s] = $"{segments[s][..tick]}<{string.Join(", ", type.GenericParameters.Skip(given).Take(arity))}>";
                given += arity;
            }
        }

        library = string.Join(".", segments);
        var name = segments[^1].Split('<')[0];
        name = name[(name.LastIndexOf('.') + 1)..];
        foreach (var field in type.Fields.Where(field => IsPublic(field.Attributes)))
        {
            yield return $"{library}.{field.Name}";
        }

        // (a property's setter, the indexer's too, is the property's)
        foreach (var method in type.Methods.Where(method => IsPublic(method.Attributes)
            && method.Name != ".cctor" && !(method.Name.StartsWith("set_", StringComparison.Ordinal)
                && (method.Attributes & MethodAttributes.SpecialName) != 0)))
        {
            var parameters = string.Join(", ", method.Parameters.Select(parameter => CSharpName(parameter.Type, method)));
            var special = (method.Attributes & MethodAttributes.SpecialName) != 0;

            // a property's setter is the property, as its getter names it (StringBuilder.Length)
            if (special && method.Name.StartsWith("set_", StringComparison.Ordinal)
                && type.Methods.Any(getter => getter.Name == "get_" + method.Name[4..]))
            {
                continue;
            }

            yield return method.Name switch
            {
                ".ctor" => $"{library}.new {name}({parameters})",
                ['g', 'e', 't', '_', ..] when special && method.Parameters.Count > 0 => $"{library}.this[{parameters}]",
                ['g', 'e', 't', '_', .. var property] when special && method.Parameters.Count == 0 =>
                    $"{library}.{property}",
                "get_Item" when special => $"{library}.Item", // the indexer, a property named Item
                ['o', 'p', '_', .. var symbol] when special && Operators.TryGetValue(symbol, out var op) =>
                    $"{library}.operator {op}({parameters})",
                _ => $"{library}.{method.Name}({parameters})",
            };
        }
    }

    /// <summary>
    /// A type of the library's signatures as C# writes it, a substitute as the type it stands for, and a generic parameter
    /// by its name in <paramref name="method"/> (T).
    /// </summary>
    private static string CSharpName(IlType type, IlMethod method) => type switch
    {
        IlNamedType { IsDefinedInThisAssembly: true } named when SubstitutedName(named) is { } library => library,

        // an instance of a generic substitute: Dictionary<TKey, TValue> for ChipSharp.Library.Collections.Dictionary`2
        IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: true } generic } instance
            when SubstitutedName(generic) is { } library && library.IndexOf('`', StringComparison.Ordinal) is > 0 and var tick =>
            $"{library[..tick]}<{string.Join(", ", instance.Arguments.Select(argument => CSharpName(argument, method)))}>",
        _ => CSharpNames.Type(type, method),
    };

    private static bool IsPublic(MethodAttributes attributes) =>
        (attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;

    private static bool IsPublic(FieldAttributes attributes) =>
        (attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;

    /// <summary>
    /// True when a substitute's method of signature <paramref name="substitute"/> stands for the library method that
    /// <paramref name="owner"/> calls with <paramref name="reference"/>: the same signature, a substitute in it
    /// standing for the type it substitutes.
    /// </summary>
    private static bool SameSignature(IlMethodSignature substitute, IlMethodSignature reference, IlAssembly owner) =>
        substitute.HasThis == reference.HasThis
        && substitute.GenericParameterCount == reference.GenericParameterCount
        && substitute.CallingConvention == reference.CallingConvention
        && substitute.ParameterTypes.Count == reference.ParameterTypes.Count
        && Key(substitute.ReturnType, Assembly) == Key(reference.ReturnType, owner)
        && substitute.ParameterTypes.Zip(reference.ParameterTypes)
            .All(pair => Key(pair.First, Assembly) == Key(pair.Second, owner));

    /// <summary>
    /// A type of <paramref name="owner"/>'s signatures as a text to compare: named types by full name, whatever
    /// assembly they come from, and a substitute (in the library's signatures) by the name of the type it stands for.
    /// </summary>
    private static string Key(IlType type, IlAssembly owner) => type switch
    {
        IlNamedType { IsDefinedInThisAssembly: true } named when owner == Assembly
            && Assembly.FindType(named.FullName) is { } definition
            && Substituted.TryGetValue(definition, out var library) => library,
        IlNamedType named => named.FullName,
        IlByRefType reference => Key(reference.ElementType, owner) + "&",
        IlArrayType array => Key(array.ElementType, owner) + "[]",
        IlPointerType pointer => Key(pointer.ElementType, owner) + "*",
        IlModifiedType modified => $"{Key(modified.UnmodifiedType, owner)} " +
            $"{(modified.IsRequired ? "modreq" : "modopt")}({Key(modified.Modifier, owner)})",
        IlGenericInstanceType instance => $"{Key(instance.Definition, owner)}" +
            $"<{string.Join(", ", instance.Arguments.Select(argument => Key(argument, owner)))}>",
        _ => type.ToString(),
    };

    private static string FindFile()
    {
        var folder = Path.GetDirectoryName(typeof(LibrarySubstitutes).Assembly.Location);
        var path = Path.Combine(string.IsNullOrEmpty(folder) ? AppContext.BaseDirectory : folder, FileName);
        return File.Exists(path)
            ? path
            : throw new ChipSharpException($"ChipSharp's library is missing: there is no {path}. It comes with " +
                "ChipSharp, next to ChipSharp.Frontend.dll; build or install ChipSharp again.");
    }

    /// <summary>The library and its substitutes, read once.</summary>
    private sealed class Library
    {
        public Library(IlAssembly assembly)
        {
            Assembly = assembly;
            var substituted = new Dictionary<IlTypeDefinition, string>();
            var substitutes = new Dictionary<string, IlTypeDefinition>(StringComparer.Ordinal);
            foreach (var type in assembly.Types)
            {
                foreach (var attribute in assembly.FindAttributes(type, AttributeType))
                {
                    // typeof(T) is the name of T with its assembly's: "System.TimeSpan, System.Runtime, Version=..."
                    var name = attribute.Arguments is [IlNamedType { Name: var serialized }]
                        ? serialized.Split(',')[0].Trim()
                        : throw new ChipSharpException(
                            $"{assembly.FilePath}: {type}: [Substitute] needs typeof(the library type).");
                    if (!substitutes.TryAdd(name, type))
                    {
                        throw new ChipSharpException(
                            $"{assembly.FilePath}: {type} and {substitutes[name]} are both substitutes for {name}.");
                    }

                    substituted.Add(type, name);
                }
            }

            Substituted = substituted;
            Substitutes = substitutes;
            // a collection's struct enumerator is what its foreach uses, not a type a program names
            Members = new(() => [.. substituted.Where(pair => !pair.Value.EndsWith("+Enumerator", StringComparison.Ordinal))
                .SelectMany(pair => MembersOf(pair.Key, pair.Value))]);
        }

        public IlAssembly Assembly { get; }

        /// <summary>The library type each substitute stands for.</summary>
        public IReadOnlyDictionary<IlTypeDefinition, string> Substituted { get; }

        /// <summary>The substitute of each library type that has one, by the library type's full name.</summary>
        public IReadOnlyDictionary<string, IlTypeDefinition> Substitutes { get; }

        /// <summary>See <see cref="LibrarySubstitutes.Members"/>; made when first asked for.</summary>
        public Lazy<IReadOnlyList<string>> Members { get; }
    }
}
