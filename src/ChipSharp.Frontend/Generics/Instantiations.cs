using System.Runtime.CompilerServices;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// Generics without a heap: a generic method or a generic type of the program is compiled once for each list of type
/// arguments the program uses it with, like C++ templates or .NET's generics over structs. Each such instance is a
/// method (<see cref="IlMethod"/>) or a type (<see cref="IlTypeDefinition"/>) of its own, whose signature, fields,
/// locals and IL have the type arguments in place of the type parameters (<see cref="Specialization"/>), so everything
/// after the IL reader takes it for code written for those types: no boxing, no heap, no code shared between types.
/// </summary>
/// <remarks>
/// <para>
/// The instances are found where the IL names them, as the IL reader links every reference it reads
/// (<see cref="Link(IlMethodReference)"/>, after <see cref="LibrarySubstitutes"/>): a call to <c>Max&lt;int&gt;</c> or
/// to a method of <c>Pair&lt;int&gt;</c>, a field of <c>Pair&lt;int&gt;</c>, is linked to that instance, which is made
/// the first time and shared after that. An instance's IL names what its generic code names, with the type arguments in
/// place, so the instances it uses (a generic method calling another with its own T) are linked the same way when its IL
/// is made, which is when the whole-program pass reaches it: so the program has exactly the instances it reaches. A
/// <c>Pair&lt;int&gt;</c> as a type (of a local, a field, a parameter) is the same instance (<see cref="Type"/>).
/// </para>
/// <para>
/// An instance is named as C# writes it: <c>Program.Max&lt;int&gt;</c>, <c>Pair&lt;int&gt;</c>,
/// <c>Pair&lt;int&gt;.Swap</c>, so its TIR function is <c>Program.Max&lt;int&gt;</c> and its symbol
/// <c>Program_Max_int_</c>.
/// </para>
/// <para>
/// The type arguments must be values: numbers, bools, enums, structs (generic ones too) and fixed text (string). A
/// class, an interface, object or an array as a type argument is not linked, so the whole-program pass reports it where
/// it is used (<see cref="Refusal"/>), and so is a type argument nested deeper than <see cref="MostNesting"/> generic
/// types, which only a generic method or type that uses itself with a bigger type argument each time makes.
/// </para>
/// </remarks>
internal static class Instantiations
{
    /// <summary>The most generic types a type argument may nest (<c>Pair&lt;Pair&lt;int&gt;&gt;</c> nests 2).</summary>
    public const int MostNesting = 8;

    /// <summary>The namespace of ChipSharp.Library's types, with the dot after it.</summary>
    private const string LibraryNamespace = "ChipSharp.Library.";

    private static readonly ConditionalWeakTable<IlAssembly, Registry> Registries = new();

    /// <summary>
    /// <paramref name="reference"/> linked to the instance of the generic method (or of the method of a generic type) it
    /// names, when it gives every type argument and they can be compiled: its <see cref="IlMethodReference.Definition"/>
    /// is then the instance, and its signature the instance's. A call of a number's CompareTo, Equals or GetHashCode,
    /// which generic code makes most, is linked to ChipSharp's own (<see cref="ConstrainedCalls.Link"/>). Any other
    /// reference as it is.
    /// </summary>
    public static IlMethodReference Link(IlMethodReference reference) => Link(reference, null);

    /// <summary>
    /// <paramref name="reference"/>, which <paramref name="owner"/>'s IL makes, linked as <see cref="Link(IlMethodReference)"/>
    /// links it; an instance of another assembly's generic code (ChipSharp.Library's) over <paramref name="owner"/>'s
    /// types is made in <paramref name="owner"/> (<see cref="Home"/>), whose IL names those types.
    /// </summary>
    public static IlMethodReference Link(IlMethodReference reference, IlAssembly? owner)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Definition is null)
        {
            // a member of a library generic type with a generic substitute (EqualityComparer<T>) is the substitute's
            reference = GenericSubstitutes.Link(reference, owner);
            if (reference.Definition is null)
            {
                return ConstrainedCalls.Link(reference);
            }
        }

        if (reference.Definition is not { } definition
            || TypeArguments(definition, reference.DeclaringType, reference.GenericArguments) is not { } typeArguments)
        {
            return reference;
        }

        IlType[] arguments = [.. typeArguments, .. reference.GenericArguments];
        var home = Home(definition.Assembly, arguments, owner);
        if (Refusal(home, arguments) is not null)
        {
            return reference;
        }

        var instance = Method(definition, typeArguments, reference.GenericArguments, home);
        return new IlMethodReference(reference.DeclaringType, reference.Name, instance.Signature,
            reference.GenericArguments, instance);
    }

    /// <summary>
    /// <paramref name="reference"/> linked to the field of the instance of the generic type it names (with its type in
    /// that instance), when its type arguments can be compiled; any other reference as it is.
    /// </summary>
    public static IlFieldReference Link(IlFieldReference reference) => Link(reference, null);

    /// <summary><paramref name="reference"/>, which <paramref name="owner"/>'s IL makes, linked as a method's is.</summary>
    public static IlFieldReference Link(IlFieldReference reference, IlAssembly? owner)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Definition is not { DeclaringType.GenericParameters.Count: > 0 } field
            || reference.DeclaringType is not IlGenericInstanceType { Arguments: var arguments }
            || arguments.Count != field.DeclaringType.GenericParameters.Count || arguments.Any(Substitution.IsOpen))
        {
            return reference;
        }

        var home = Home(field.DeclaringType.Assembly, arguments, owner);
        if (Refusal(home, arguments) is not null)
        {
            return reference;
        }

        var type = TypeOf(field.DeclaringType, arguments, home);
        var instance = type.Fields[IndexOf(field.DeclaringType.Fields, field)];
        return new IlFieldReference(reference.DeclaringType, reference.Name, instance.Type, instance);
    }

    /// <summary>
    /// The instance <paramref name="type"/> names (<c>Pair`1&lt;int32&gt;</c> is <c>Pair&lt;int&gt;</c>): of a generic
    /// type of <paramref name="assembly"/> or a library substitute, when its type arguments can be compiled; else null.
    /// </summary>
    public static IlTypeDefinition? Type(IlGenericInstanceType type, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(assembly);
        if (Definition(type, assembly) is not { } definition
            || definition.GenericParameters.Count != type.Arguments.Count || type.Arguments.Any(Substitution.IsOpen))
        {
            return null;
        }

        var home = Home(definition.Assembly, type.Arguments, assembly);
        return Refusal(home, type.Arguments) is null ? TypeOf(definition, type.Arguments, home) : null;
    }

    /// <summary>
    /// The instance of a generic type of <paramref name="assembly"/> made so far with this full name, e.g. "Pair&lt;int&gt;";
    /// else a type of ChipSharp.Library by its full name, which the IL of an instance of the library's generic code over
    /// <paramref name="assembly"/>'s types names (see <see cref="Home"/>), and the signatures of the library's instances.
    /// </summary>
    public static IlTypeDefinition? FindType(IlAssembly assembly, string fullName)
    {
        if (Registries.TryGetValue(assembly, out var registry))
        {
            lock (registry)
            {
                if (registry.TypesByName.GetValueOrDefault(fullName) is { } type)
                {
                    return type;
                }
            }
        }

        return fullName.StartsWith(LibraryNamespace, StringComparison.Ordinal) && assembly != LibrarySubstitutes.Assembly
            ? LibrarySubstitutes.Assembly.FindType(fullName)
            : null;
    }

    /// <summary>True for a generic method, or a method of a generic type: one that is compiled only as its instances.</summary>
    public static bool IsGeneric(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method.GenericParameters.Count > 0 || method.DeclaringType.GenericParameters.Count > 0;
    }

    /// <summary>
    /// Why the generic method, type or field that <paramref name="reference"/> names with these type arguments is not
    /// linked to an instance: a class as a type argument, type arguments nested too deep, ...; null when it is.
    /// </summary>
    public static Unsupported? Refusal(IlMethodReference reference) => Refusal(reference, null);

    /// <summary>Like <see cref="Refusal(IlMethodReference)"/>, for a reference <paramref name="owner"/>'s IL makes.</summary>
    public static Unsupported? Refusal(IlMethodReference reference, IlAssembly? owner)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Definition is not { } definition
            || TypeArguments(definition, reference.DeclaringType, reference.GenericArguments) is not { } typeArguments)
        {
            return null;
        }

        IlType[] arguments = [.. typeArguments, .. reference.GenericArguments];
        return Refusal(Home(definition.Assembly, arguments, owner), arguments);
    }

    /// <summary>Why the generic type instance <paramref name="type"/> cannot be compiled, or null when it can.</summary>
    public static Unsupported? Refusal(IlGenericInstanceType type, IlAssembly assembly) =>
        Definition(type, assembly) is { } definition
            ? Refusal(Home(definition.Assembly, type.Arguments, assembly), type.Arguments)
            : null;

    /// <summary>
    /// Why <paramref name="arguments"/>, closed type arguments that code of <paramref name="assembly"/> gives, cannot be
    /// the type arguments of an instance: one nests generic types too deep, or is not a value; null when they can be.
    /// </summary>
    public static Unsupported? Refusal(IlAssembly assembly, IReadOnlyList<IlType> arguments)
    {
        foreach (var argument in arguments)
        {
            if (Nesting(argument) > MostNesting)
            {
                return Unsupported.GenericNesting(MostNesting);
            }

            if (ForArgument(argument, assembly) is { } unsupported)
            {
                return unsupported;
            }
        }

        return null;
    }

    /// <summary>
    /// The instance of <paramref name="definition"/> for these closed type arguments (of its type, then its own), made
    /// the first time it is asked for.
    /// </summary>
    public static IlMethod Method(IlMethod definition, IReadOnlyList<IlType> typeArguments, IReadOnlyList<IlType> methodArguments) =>
        Method(definition, typeArguments, methodArguments, null);

    /// <summary>
    /// Like <see cref="Method(IlMethod, IReadOnlyList{IlType}, IReadOnlyList{IlType})"/>, made in <paramref name="home"/>
    /// (see <see cref="Home"/>; null: the definition's assembly).
    /// </summary>
    public static IlMethod Method(IlMethod definition, IReadOnlyList<IlType> typeArguments, IReadOnlyList<IlType> methodArguments,
        IlAssembly? home)
    {
        home ??= definition.Assembly;
        var registry = Registries.GetValue(home, _ => new Registry());
        lock (registry)
        {
            var key = (definition, Key(typeArguments), Key(methodArguments));
            if (registry.Methods.TryGetValue(key, out var known))
            {
                return known;
            }

            if (typeArguments.Count > 0)
            {
                // the type's instance makes its methods that are not generic themselves
                var type = TypeOf(definition.DeclaringType, typeArguments, home);
                if (methodArguments.Count == 0)
                {
                    return registry.Methods[key];
                }

                return registry.Methods[key] = Create(type, definition, typeArguments, methodArguments);
            }

            return registry.Methods[key] = Create(definition.DeclaringType, definition, [], methodArguments,
                home == definition.Assembly ? null : home);
        }
    }

    /// <summary>
    /// The instance of the generic type <paramref name="definition"/> for these closed type arguments, with its fields and
    /// its methods that are not generic themselves, made the first time it is asked for.
    /// </summary>
    public static IlTypeDefinition TypeOf(IlTypeDefinition definition, IReadOnlyList<IlType> arguments) =>
        TypeOf(definition, arguments, null);

    /// <summary>
    /// Like <see cref="TypeOf(IlTypeDefinition, IReadOnlyList{IlType})"/>, made in <paramref name="home"/> (see
    /// <see cref="Home"/>; null: the definition's assembly), which is the instance's <see cref="IlTypeDefinition.Assembly"/>.
    /// A type nested in a generic type has the type arguments of the type it is in first (C# gives it them), so its
    /// instance is nested in that type's instance: <c>List&lt;int&gt;.Cell</c>.
    /// </summary>
    public static IlTypeDefinition TypeOf(IlTypeDefinition definition, IReadOnlyList<IlType> arguments, IlAssembly? home)
    {
        home ??= definition.Assembly;
        var registry = Registries.GetValue(home, _ => new Registry());
        lock (registry)
        {
            var key = (definition, Key(arguments));
            if (registry.Types.TryGetValue(key, out var known))
            {
                return known;
            }

            var outer = definition.DeclaringType is { GenericParameters.Count: > 0 and var inherited } declaring
                && inherited <= arguments.Count
                ? TypeOf(declaring, [.. arguments.Take(inherited)], home)
                : null;
            IReadOnlyList<IlType> own = outer is null ? arguments : [.. arguments.Skip(outer.TypeArguments.Count)];
            var tick = definition.Name.IndexOf('`', StringComparison.Ordinal);
            var plain = tick < 0 ? definition.Name : definition.Name[..tick];
            var name = own.Count == 0 ? plain : $"{plain}<{Names(own)}>";
            var named = new IlNamedType(definition.Namespace, name, outer?.Type ?? definition.Type.DeclaringType,
                definition.Type.Assembly);
            for (var n = 2; registry.TypesByName.ContainsKey(named.FullName); n++)
            {
                named = named with { Name = $"{name}#{n}" };
            }

            var substitution = new Substitution(arguments, []);
            var type = new IlTypeDefinition(home, definition.Token, named, definition.Attributes,
                definition.BaseType is { } baseType ? substitution.Apply(baseType) : null, [])
            {
                GenericDefinition = definition,
                TypeArguments = arguments,
                DeclaringType = outer ?? definition.DeclaringType,
            };
            registry.Types.Add(key, type);
            registry.TypesByName.Add(named.FullName, type);

            foreach (var field in definition.Fields)
            {
                type.Add(new IlField(type, field.Token, field.Name, field.Attributes, substitution.Apply(field.Type)));
            }

            foreach (var method in definition.Methods.Where(method => method.GenericParameters.Count == 0))
            {
                var instance = Create(type, method, arguments, []);
                type.Add(instance);
                registry.Methods.Add((method, key.Item2, ""), instance);
            }

            return type;
        }
    }

    /// <summary>
    /// The method of <paramref name="type"/> (an instance of a generic type, or a type that is not generic) that stands
    /// for <paramref name="method"/>, a method of its generic type.
    /// </summary>
    public static IlMethod MethodOf(IlTypeDefinition type, IlMethod method) =>
        type.GenericDefinition is null ? method : Method(method, type.TypeArguments, [], type.Assembly);

    /// <summary>The type arguments as C# writes them, e.g. "int, Program.Point".</summary>
    public static string Names(IEnumerable<IlType> arguments) =>
        string.Join(", ", arguments.Select(argument => CSharpNames.Type(argument)));

    /// <summary>
    /// The type IL names <paramref name="type"/> with: an instance of a generic type as the generic type with its type
    /// arguments (<c>Box`1&lt;Program+Lane&gt;</c>, which .NET names <c>Box`1[Program+Lane]</c>), any other type as it is.
    /// </summary>
    public static IlType Reference(IlTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GenericDefinition is { } generic ? new IlGenericInstanceType(generic.Type, type.TypeArguments) : type.Type;
    }

    /// <summary>
    /// Where the instance of generic code of <paramref name="definitions"/> for <paramref name="arguments"/>, which
    /// <paramref name="owner"/>'s IL names, is made: in <paramref name="owner"/> when the arguments name its own types and
    /// the code is another assembly's (ChipSharp.Library's generic code over the program's types: the instance's IL then
    /// names the program's types as the program does, and the library's by their full names, see
    /// <see cref="FindType"/>); else, as for every instance over values and the library's own types, in the assembly
    /// of the generic code, so the program's instances are made once.
    /// </summary>
    private static IlAssembly Home(IlAssembly definitions, IReadOnlyList<IlType> arguments, IlAssembly? owner) =>
        owner is not null && owner != definitions && arguments.Any(NamesOwnType) ? owner : definitions;

    /// <summary>True when <paramref name="type"/> names a type of the assembly whose IL it is in (or one in it).</summary>
    private static bool NamesOwnType(IlType type) => type switch
    {
        IlNamedType named => named.IsDefinedInThisAssembly,
        IlGenericInstanceType instance => NamesOwnType(instance.Definition) || instance.Arguments.Any(NamesOwnType),
        IlArrayType array => NamesOwnType(array.ElementType),
        IlMultiDimensionalArrayType array => NamesOwnType(array.ElementType),
        IlByRefType reference => NamesOwnType(reference.ElementType),
        IlPointerType pointer => NamesOwnType(pointer.ElementType),
        IlModifiedType modified => NamesOwnType(modified.UnmodifiedType),
        _ => false,
    };

    private static IlMethod Create(IlTypeDefinition type, IlMethod definition, IReadOnlyList<IlType> typeArguments,
        IReadOnlyList<IlType> methodArguments, IlAssembly? context = null)
    {
        var substitution = new Substitution(typeArguments, methodArguments);
        var name = methodArguments.Count == 0 ? definition.Name : $"{definition.Name}<{Names(methodArguments)}>";
        var signature = substitution.Apply(definition.Signature) with { GenericParameterCount = 0 };
        IlParameter[] parameters = [.. definition.Parameters.Select(p => p with { Type = substitution.Apply(p.Type) })];
        return new IlMethod(type, definition, name, signature, parameters, methodArguments,
            instance => Specialization.Body(instance, definition, substitution), context);
    }

    /// <summary>
    /// The type arguments a reference to <paramref name="definition"/> gives its type (none when the type is not generic),
    /// when it gives them all, and its own, and none of them is a type parameter; else null.
    /// </summary>
    private static IReadOnlyList<IlType>? TypeArguments(IlMethod definition, IlType declaringType,
        IReadOnlyList<IlType> methodArguments)
    {
        if (!IsGeneric(definition) || methodArguments.Count != definition.GenericParameters.Count
            || methodArguments.Any(Substitution.IsOpen))
        {
            return null;
        }

        var count = definition.DeclaringType.GenericParameters.Count;
        if (count == 0)
        {
            return [];
        }

        return declaringType is IlGenericInstanceType { Arguments: var arguments } && arguments.Count == count
            && !arguments.Any(Substitution.IsOpen)
            ? arguments
            : null;
    }

    /// <summary>The generic type (of <paramref name="assembly"/>, or a library substitute) <paramref name="type"/> is an instance of.</summary>
    internal static IlTypeDefinition? Definition(IlGenericInstanceType type, IlAssembly assembly) => type.Definition switch
    {
        IlNamedType { IsDefinedInThisAssembly: true } named => assembly.FindType(named.FullName),
        IlNamedType named => LibrarySubstitutes.FindType(named),
        _ => null,
    };

    /// <summary>
    /// Why one type argument cannot be one, or null when it is a value (a number, a bool, an enum, a struct, text) or a
    /// reference (<see cref="ReferenceArguments"/>: object, a class, an interface, an array).
    /// </summary>
    private static Unsupported? ForArgument(IlType argument, IlAssembly assembly)
    {
        if (ReferenceArguments.IsReference(argument, assembly, out var reference))
        {
            return reference;
        }

        switch (ValueTypes.WithoutModifiers(argument))
        {
            case IlPrimitiveType { Kind: IlPrimitiveKind.Void or IlPrimitiveKind.TypedReference } primitive:
                return Unsupported.TypeNamed(CSharpNames.Type(primitive));

            case IlPrimitiveType:
                return null;

            case IlNamedType named:
                var definition = named.IsDefinedInThisAssembly
                    ? assembly.FindType(named.FullName)
                    : LibrarySubstitutes.FindType(named);
                return definition is null ? Unsupported.TypeNamed(CSharpNames.Type(named))
                    : ValueTypes.IsStruct(definition) || ValueTypes.IsEnum(definition) ? null
                    : Unsupported.GenericsOverClasses;

            case IlGenericInstanceType instance:
                return Definition(instance, assembly) is not { } generic ? Unsupported.TypeNamed(CSharpNames.Type(instance))
                    : !ValueTypes.IsStruct(generic) ? Unsupported.GenericsOverClasses
                    : Refusal(assembly, instance.Arguments);

            default:
                return Unsupported.TypeNamed(CSharpNames.Type(argument));
        }
    }

    /// <summary>How many generic types <paramref name="type"/> nests: 0 for <c>int</c>, 2 for <c>Pair&lt;Pair&lt;int&gt;&gt;</c>.</summary>
    private static int Nesting(IlType type) => type switch
    {
        IlGenericInstanceType instance => 1 + instance.Arguments.Select(Nesting).DefaultIfEmpty(0).Max(),
        IlArrayType array => Nesting(array.ElementType),
        IlModifiedType modified => Nesting(modified.UnmodifiedType),
        _ => 0,
    };

    /// <summary>A text that tells lists of type arguments apart: their IL names.</summary>
    private static string Key(IEnumerable<IlType> arguments) => string.Join("|", arguments);

    private static int IndexOf(IReadOnlyList<IlField> fields, IlField field)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i] == field)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"{field} is not a field of {field.DeclaringType}.");
    }

    /// <summary>The instances of one assembly's generic methods and types, made so far.</summary>
    private sealed class Registry
    {
        public Dictionary<(IlTypeDefinition, string), IlTypeDefinition> Types { get; } = [];

        public Dictionary<string, IlTypeDefinition> TypesByName { get; } = new(StringComparer.Ordinal);

        public Dictionary<(IlMethod, string, string), IlMethod> Methods { get; } = [];
    }
}
