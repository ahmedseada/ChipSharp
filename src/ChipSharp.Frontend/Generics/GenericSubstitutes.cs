using System.Reflection;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// The groundwork of collections (generics over classes): ChipSharp.Library's generic substitutes, and the library's
/// interfaces of collections that the program's classes may implement and call.
/// <list type="bullet">
/// <item>A generic substitute (<c>[Substitute(typeof(EqualityComparer&lt;&gt;))] class EqualityComparer&lt;T&gt;</c>)
/// stands for the library's generic type like any substitute: a call of a member of <c>EqualityComparer&lt;Node&gt;</c>
/// is linked to the substitute's member with the same name and signature (<see cref="Link"/>), and then, as every
/// reference to a generic type's member, to the instance of the substitute for its type arguments
/// (<see cref="Instantiations"/>), which is made in the program when they are the program's types.</item>
/// <item>The interfaces <see cref="CollectionInterfaces"/> (<c>IEnumerable&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>,
/// <c>IComparer&lt;T&gt;</c>, ...) are interfaces like the program's own (<see cref="VirtualMethods.IsInterface"/>): a
/// class implements them, and a call through one goes through the class's interface map.</item>
/// </list>
/// </summary>
internal static class GenericSubstitutes
{
    /// <summary>The library's interfaces of collections a class of the program may implement, by full name.</summary>
    public static IReadOnlyList<string> CollectionInterfaces { get; } =
    [
        "System.Collections.IEnumerable",
        "System.Collections.IEnumerator",
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IEnumerator`1",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.IComparer`1",
        "System.Collections.Generic.IEqualityComparer`1",
    ];

    /// <summary>
    /// <paramref name="reference"/>, a method of an instance of a library generic type that has a generic substitute
    /// (<c>EqualityComparer&lt;Node&gt;.get_Default</c>), which <paramref name="owner"/>'s IL names, linked to the
    /// substitute's public method with its name and signature (still the generic one, which
    /// <see cref="Instantiations.Link(IlMethodReference, IlAssembly?)"/> then links to its instance); any other reference
    /// as it is.
    /// </summary>
    public static IlMethodReference Link(IlMethodReference reference, IlAssembly? owner)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference is not { Definition: null, DeclaringType: IlGenericInstanceType { Definition: IlNamedType named } }
            || named.IsDefinedInThisAssembly
            || LibrarySubstitutes.FindType(named) is not { GenericParameters.Count: > 0 } substitute)
        {
            return reference;
        }

        var assembly = owner ?? LibrarySubstitutes.Assembly;
        var method = substitute.Methods.FirstOrDefault(candidate => candidate.Name == reference.Name
            && (candidate.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public
            && LibrarySubstitutes.HasSignature(candidate, reference.Signature, assembly));
        return method is null
            ? reference
            : new IlMethodReference(reference.DeclaringType, reference.Name, reference.Signature, reference.GenericArguments,
                method);
    }
}
