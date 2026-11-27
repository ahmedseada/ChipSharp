using System.Reflection;
using ChipSharp.Frontend.Generics;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// Generic virtual methods (<c>virtual void Visit&lt;T&gt;(T item)</c>, an override of one, a generic method of an
/// interface): the whole program is known, so each instance the program calls (<c>Visit&lt;int&gt;</c>,
/// <c>Visit&lt;Node&gt;</c>, see <see cref="Instantiations"/>) is a slot of its own, as if it were a virtual method
/// written for that type. A call of <c>Visit&lt;int&gt;</c> goes through the slot of <c>Visit&lt;int&gt;</c>; on each class
/// the program makes it runs that class's override, made for the same type arguments: the instance of the most derived
/// generic method that overrides the one that opens the slot (the same name, as many type parameters and the same
/// signature in terms of them), or for an interface's, the class's implementation of it, explicit or implicit. So only
/// the instances a call can run are compiled, and a call with one target is a plain call, like any other virtual call.
/// </summary>
internal static partial class VirtualMethods
{
    /// <summary>
    /// The slot a callvirt of <paramref name="callee"/>, an instance of a generic virtual method (of a class) or of an
    /// interface's generic method, goes through: the instance's own slot, which the instance of the generic method it
    /// overrides opens; for an interface's, a slot of the interface named for its type arguments (<c>Visit&lt;int&gt;</c>),
    /// whose <see cref="DispatchSlot.Opener"/> is the interface method's instance. Null when the call is not linked to an
    /// instance (its type arguments cannot be compiled), or calls a method that is not virtual.
    /// </summary>
    private static DispatchSlot? GenericSlotCalled(IlMethodReference callee)
    {
        if (callee.Definition is not { MethodArguments.Count: > 0, GenericDefinition: not null } instance)
        {
            return null;
        }

        var type = instance.DeclaringType;
        if (type.IsInterface)
        {
            return new DispatchSlot(DispatchSlotKind.Interface, ValueTypes.WithoutModifiers(callee.DeclaringType),
                instance.Name, instance.Signature, instance);
        }

        return ObjectTypes.IsClass(type) && ClassRules.IsOverridable(instance) ? SlotOf(instance) : null;
    }

    /// <summary>
    /// The slot of <paramref name="method"/>, an instance of a generic virtual method of a class: the slot of the instance,
    /// for the same type arguments, of the base class's generic method it overrides, or its own when it opens one.
    /// </summary>
    private static DispatchSlot GenericSlot(IlMethod method)
    {
        var type = method.DeclaringType;
        var generic = method.GenericDefinition!;
        if ((method.Attributes & MethodAttributes.NewSlot) == 0)
        {
            var wanted = GenericSignature(type, generic);
            for (var baseClass = BaseOf(type); baseClass is not null; baseClass = BaseOf(baseClass))
            {
                var overridden = GenericMethods(baseClass).FirstOrDefault(candidate =>
                    (candidate.Attributes & MethodAttributes.Virtual) != 0 && candidate.Name == generic.Name
                    && candidate.GenericParameters.Count == generic.GenericParameters.Count
                    && GenericSignature(baseClass, candidate) == wanted);
                if (overridden is not null)
                {
                    return SlotOf(Instantiations.Method(overridden, baseClass.TypeArguments, method.MethodArguments,
                        baseClass.Assembly));
                }
            }
        }

        return new DispatchSlot(DispatchSlotKind.Class, type.Type, method.Name, method.Signature, method);
    }

    /// <summary>
    /// What <paramref name="slot"/>, the slot of an instance of a generic virtual method or of an interface's generic
    /// method, runs on an object of <paramref name="type"/>: the instance, for the slot's type arguments, of the generic
    /// method of the class (or of a base) that overrides or implements it; null when there is none, or it is abstract.
    /// </summary>
    private static DispatchTarget? ResolveGeneric(IlTypeDefinition type, DispatchSlot slot)
    {
        var opener = slot.Opener!;
        var generic = opener.GenericDefinition!;
        var arguments = opener.MethodArguments;
        if (slot.Kind == DispatchSlotKind.Interface)
        {
            return ResolveGenericInterface(type, slot, generic, arguments);
        }

        for (IlTypeDefinition? current = type; current is not null; current = BaseOf(current))
        {
            foreach (var candidate in GenericMethods(current))
            {
                if ((candidate.Attributes & MethodAttributes.Virtual) == 0 || candidate.Name != generic.Name
                    || candidate.GenericParameters.Count != generic.GenericParameters.Count)
                {
                    continue;
                }

                var instance = Instantiations.Method(candidate, current.TypeArguments, arguments, current.Assembly);
                if (SlotOf(instance) == slot)
                {
                    return instance.HasBody ? DispatchTarget.Of(instance) : null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The class's implementation of an interface's generic method (<paramref name="generic"/>, for
    /// <paramref name="arguments"/>): in the most derived class that lists the interface, its explicit implementation,
    /// else its (or a base class's) public generic method of that name and signature; then, when that is virtual, what
    /// its slot runs on the class.
    /// </summary>
    private static DispatchTarget? ResolveGenericInterface(IlTypeDefinition type, DispatchSlot slot, IlMethod generic,
        IReadOnlyList<IlType> arguments)
    {
        var wanted = Arguments(slot.Owner).Apply(generic.Signature);
        for (IlTypeDefinition? lister = type; lister is not null; lister = BaseOf(lister))
        {
            if (!InterfacesOf(lister).Contains(slot.Owner))
            {
                continue;
            }

            var ofLister = new Substitution(lister.TypeArguments, []);
            foreach (var (body, declaration) in lister.Assembly.MethodImplementations(lister.GenericDefinition ?? lister))
            {
                var declared = ValueTypes.WithoutModifiers(ofLister.Apply(declaration.DeclaringType));
                if (declaration.Name == generic.Name && declared == slot.Owner
                    && body.GenericParameters.Count == generic.GenericParameters.Count
                    && Arguments(declared).Apply(declaration.Signature) == wanted)
                {
                    return Final(type, Instantiations.Method(body, lister.TypeArguments, arguments, lister.Assembly));
                }
            }

            for (IlTypeDefinition? current = lister; current is not null; current = BaseOf(current))
            {
                var implicitly = GenericMethods(current).FirstOrDefault(method => method is { IsStatic: false }
                    && (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public
                    && method.Name == generic.Name && method.GenericParameters.Count == generic.GenericParameters.Count
                    && GenericSignature(current, method) == wanted);
                if (implicitly is not null)
                {
                    return Final(type, Instantiations.Method(implicitly, current.TypeArguments, arguments, current.Assembly));
                }
            }
        }

        return null;
    }

    /// <summary>The generic methods of a class (of its generic type, for an instance of one), as its metadata declares them.</summary>
    private static IEnumerable<IlMethod> GenericMethods(IlTypeDefinition type) =>
        (type.GenericDefinition ?? type).Methods.Where(method => method.GenericParameters.Count > 0);

    /// <summary>
    /// The signature of <paramref name="method"/>, a generic method of <paramref name="type"/>'s (generic) type, with
    /// the type's type arguments in place and its own type parameters (<c>!!0</c>) as they are.
    /// </summary>
    private static IlMethodSignature GenericSignature(IlTypeDefinition type, IlMethod method) =>
        new Substitution(type.TypeArguments, []).Apply(method.Signature);
}
