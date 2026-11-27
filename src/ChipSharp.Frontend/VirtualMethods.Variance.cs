using System.Reflection;
using ChipSharp.Frontend.Generics;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// Variance of generic interfaces (<c>interface IProducer&lt;out T&gt;</c>, <c>IComparer&lt;in T&gt;</c>), in calls and
/// type tests, as .NET has it: an object of a class that implements <c>IProducer&lt;Dog&gt;</c> is an
/// <c>IProducer&lt;Animal&gt;</c> when T is <c>out</c> and a Dog is an Animal (a class derived from it, a class that
/// implements it, or <c>object</c>), and an <c>IComparer&lt;Dog&gt;</c> when it implements
/// <c>IComparer&lt;Animal&gt;</c> and T is <c>in</c>; a value type argument must be the same. A call through the
/// interface it is used as runs the methods of the interface the class implements, and its interface map has an entry
/// for each interface it is used as, so a cast and an <c>is</c> find it there.
/// </summary>
internal static partial class VirtualMethods
{
    /// <summary>The variance of the library's variant interfaces, by full name: <c>out</c> (covariant) or <c>in</c>.</summary>
    private static readonly Dictionary<string, GenericParameterAttributes> LibraryVariances = new(StringComparer.Ordinal)
    {
        ["System.Collections.Generic.IEnumerable`1"] = GenericParameterAttributes.Covariant,
        ["System.Collections.Generic.IEnumerator`1"] = GenericParameterAttributes.Covariant,
        ["System.Collections.Generic.IReadOnlyCollection`1"] = GenericParameterAttributes.Covariant,
        ["System.Collections.Generic.IReadOnlyList`1"] = GenericParameterAttributes.Covariant,
        ["System.Collections.Generic.IComparer`1"] = GenericParameterAttributes.Contravariant,
        ["System.Collections.Generic.IEqualityComparer`1"] = GenericParameterAttributes.Contravariant,
        ["System.IComparable`1"] = GenericParameterAttributes.Contravariant,
    };

    /// <summary>
    /// The interface <paramref name="type"/> (or a base class) implements that makes it a <paramref name="wanted"/> by
    /// variance (it does not implement <paramref name="wanted"/> itself); null when there is none.
    /// </summary>
    private static IlType? VariantInterface(IlTypeDefinition type, IlType wanted)
    {
        if (wanted is not IlGenericInstanceType { Definition: var definition } instance
            || Variances(definition, type.Assembly) is not { } variances
            || variances.All(variance => variance == GenericParameterAttributes.None))
        {
            return null;
        }

        for (IlTypeDefinition? current = type; current is not null; current = BaseOf(current))
        {
            foreach (var listed in InterfacesOf(current))
            {
                if (listed is IlGenericInstanceType { Definition: var same } candidate && same == definition
                    && candidate.Arguments.Count == variances.Count
                    && candidate.Arguments.Select((argument, i) => Fits(argument, instance.Arguments[i], variances[i], type.Assembly))
                        .All(fits => fits))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What <paramref name="slot"/>, a method of an interface <paramref name="type"/> is by variance only, runs on it: its
    /// method of the interface it implements, <paramref name="implemented"/>, the one in the same place.
    /// </summary>
    private static DispatchTarget? ResolveVariant(IlTypeDefinition type, DispatchSlot slot, IlType implemented)
    {
        var signature = slot.Signature;
        var assembly = type.Assembly;
        if (InterfaceDefinition(slot.Owner, assembly) is { } wanted && InterfaceDefinition(implemented, assembly) is { } instance)
        {
            // the program's interface: the method in the same place in the instance the class implements
            var index = wanted.Methods.ToList().FindIndex(method => method.Name == slot.Name && method.Signature == signature);
            if (index < 0 || index >= instance.Methods.Count)
            {
                return null;
            }

            signature = instance.Methods[index].Signature;
        }
        else
        {
            // a library interface: its type arguments as the implemented interface has them
            var from = ((IlGenericInstanceType)slot.Owner).Arguments;
            var to = ((IlGenericInstanceType)implemented).Arguments;
            signature = signature with
            {
                ReturnType = Replace(signature.ReturnType, from, to),
                ParameterTypes = [.. signature.ParameterTypes.Select(parameter => Replace(parameter, from, to))],
            };
        }

        return ResolveInterface(type, slot with { Owner = implemented, Signature = signature });
    }

    /// <summary>
    /// True when a type argument <paramref name="have"/> of an implemented interface makes it one with
    /// <paramref name="wanted"/> in its place, for a parameter of <paramref name="variance"/>.
    /// </summary>
    private static bool Fits(IlType have, IlType wanted, GenericParameterAttributes variance, IlAssembly assembly) =>
        have == wanted || variance switch
        {
            GenericParameterAttributes.Covariant => IsReferenceConversion(have, wanted, assembly),
            GenericParameterAttributes.Contravariant => IsReferenceConversion(wanted, have, assembly),
            _ => false,
        };

    /// <summary>
    /// True when an object of type <paramref name="from"/> is one of type <paramref name="to"/>: object, a base class,
    /// an interface the class implements (of a class of the program).
    /// </summary>
    private static bool IsReferenceConversion(IlType from, IlType to, IlAssembly assembly)
    {
        if (!ObjectTypes.TryMapType(from, assembly, out _) && ValueTypes.WithoutModifiers(from) is not IlArrayType)
        {
            return false;
        }

        if (ObjectTypes.IsSystemObject(to))
        {
            return true;
        }

        return ObjectTypes.Class(from, assembly) is { } type
            && (ObjectTypes.Class(to, assembly) is { } ancestor ? IsDerived(type, ancestor)
                : IsInterface(to, assembly) && Implements(type, to));
    }

    /// <summary>The variance of each type parameter of the generic interface <paramref name="definition"/>; null when it is not known.</summary>
    private static IReadOnlyList<GenericParameterAttributes>? Variances(IlType definition, IlAssembly assembly) =>
        definition switch
        {
            IlNamedType { IsDefinedInThisAssembly: true } named when assembly.FindType(named.FullName) is { } type =>
                type.Assembly.Variances(type),
            IlNamedType named when LibraryVariances.TryGetValue(named.FullName, out var variance) => [variance],
            _ => null,
        };

    /// <summary>
    /// <paramref name="type"/> (modifiers looked through) as interfaces and slots compare it: a type of .NET's own is the
    /// same whichever of its assemblies names it (System.Runtime in a program compiled against the reference assemblies,
    /// System.Private.CoreLib against the implementation, netstandard), so the library's code, compiled against one, and
    /// the program's, against another, call through the same interfaces.
    /// </summary>
    public static IlType Canonical(IlType type) => ValueTypes.WithoutModifiers(type) switch
    {
        IlNamedType named => CanonicalNamed(named),
        IlGenericInstanceType instance => new IlGenericInstanceType(Canonical(instance.Definition),
            [.. instance.Arguments.Select(Canonical)]),
        IlArrayType array => new IlArrayType(Canonical(array.ElementType)),
        IlByRefType reference => new IlByRefType(Canonical(reference.ElementType)),
        var other => other,
    };

    /// <summary>
    /// <paramref name="signature"/> with its types <see cref="Canonical"/>: how an interface's slot and the methods that
    /// fill it are compared, whichever of .NET's assemblies the program's and the library's IL name the types with.
    /// </summary>
    public static IlMethodSignature CanonicalSignature(IlMethodSignature signature) => signature with
    {
        ReturnType = Canonical(signature.ReturnType),
        ParameterTypes = [.. signature.ParameterTypes.Select(Canonical)],
    };

    private static IlNamedType CanonicalNamed(IlNamedType type) => type with
    {
        Assembly = type.Assembly is { } assembly && (assembly is "mscorlib" or "netstandard" or "System"
            || assembly.StartsWith("System.", StringComparison.Ordinal)) ? "System.Runtime" : type.Assembly,
        DeclaringType = type.DeclaringType is { } declaring ? CanonicalNamed(declaring) : null,
    };

    /// <summary><paramref name="type"/> with each of <paramref name="from"/> (and in what it is made of) replaced by the one of <paramref name="to"/> in its place.</summary>
    private static IlType Replace(IlType type, IReadOnlyList<IlType> from, IReadOnlyList<IlType> to)
    {
        for (var i = 0; i < from.Count; i++)
        {
            if (type == from[i])
            {
                return to[i];
            }
        }

        return type is IlGenericInstanceType instance
            ? new IlGenericInstanceType(instance.Definition, [.. instance.Arguments.Select(argument => Replace(argument, from, to))])
            : type;
    }
}
