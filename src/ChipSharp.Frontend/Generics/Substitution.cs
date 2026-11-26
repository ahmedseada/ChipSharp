using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// Type arguments put in place of type parameters: <c>!0</c>, <c>!1</c>, ... (the type's) by <see cref="TypeArguments"/>
/// and <c>!!0</c>, <c>!!1</c>, ... (the method's) by <see cref="MethodArguments"/>, in a type, a signature, or a reference
/// that code of a generic method or type makes.
/// </summary>
internal sealed class Substitution(IReadOnlyList<IlType> typeArguments, IReadOnlyList<IlType> methodArguments)
{
    public IReadOnlyList<IlType> TypeArguments { get; } = typeArguments;

    public IReadOnlyList<IlType> MethodArguments { get; } = methodArguments;

    /// <summary><paramref name="type"/> with the arguments in place of the parameters (a parameter it has no argument for stays).</summary>
    public IlType Apply(IlType type) => type switch
    {
        IlGenericParameterType { IsMethodParameter: true, Index: var index } parameter =>
            index < MethodArguments.Count ? MethodArguments[index] : parameter,
        IlGenericParameterType { Index: var index } parameter =>
            index < TypeArguments.Count ? TypeArguments[index] : parameter,
        IlGenericInstanceType instance => new IlGenericInstanceType(Apply(instance.Definition), [.. instance.Arguments.Select(Apply)]),
        IlArrayType array => new IlArrayType(Apply(array.ElementType)),
        IlMultiDimensionalArrayType array => new IlMultiDimensionalArrayType(Apply(array.ElementType), array.Rank),
        IlPointerType pointer => new IlPointerType(Apply(pointer.ElementType)),
        IlByRefType reference => new IlByRefType(Apply(reference.ElementType)),
        IlPinnedType pinned => new IlPinnedType(Apply(pinned.ElementType)),
        IlModifiedType modified => new IlModifiedType(Apply(modified.UnmodifiedType), Apply(modified.Modifier), modified.IsRequired),
        IlFunctionPointerType pointer => new IlFunctionPointerType(Apply(pointer.Signature)),
        _ => type,
    };

    /// <summary>A signature (of a method of the generic code, or of a function pointer) with the arguments in place.</summary>
    public IlMethodSignature Apply(IlMethodSignature signature) => signature with
    {
        ReturnType = Apply(signature.ReturnType),
        ParameterTypes = [.. signature.ParameterTypes.Select(Apply)],
    };

    /// <summary>
    /// A method the generic code names, with the arguments in place in its type and its own type arguments (not in its
    /// signature, which the method's own type parameters are about), and linked as <see cref="Instantiations.Link(IlMethodReference)"/>
    /// links what <paramref name="owner"/>'s IL names.
    /// </summary>
    public IlMethodReference Apply(IlMethodReference reference, IlAssembly owner)
    {
        var declaringType = Apply(reference.DeclaringType);
        IlType[] arguments = [.. reference.GenericArguments.Select(Apply)];
        if (declaringType == reference.DeclaringType && arguments.SequenceEqual(reference.GenericArguments))
        {
            return reference;
        }

        return Instantiations.Link(LibrarySubstitutes.Link(owner,
            new IlMethodReference(declaringType, reference.Name, reference.Signature, arguments, reference.Definition)));
    }

    /// <summary>A field the generic code names, with the arguments in place in its type, linked like a method.</summary>
    public IlFieldReference Apply(IlFieldReference reference, IlAssembly owner)
    {
        var declaringType = Apply(reference.DeclaringType);
        return declaringType == reference.DeclaringType
            ? reference
            : Instantiations.Link(LibrarySubstitutes.Link(owner,
                new IlFieldReference(declaringType, reference.Name, reference.FieldType, reference.Definition)));
    }

    /// <summary>True when <paramref name="type"/> names a type parameter: it is in the code of a generic method or type.</summary>
    public static bool IsOpen(IlType type) => type switch
    {
        IlGenericParameterType => true,
        IlGenericInstanceType instance => IsOpen(instance.Definition) || instance.Arguments.Any(IsOpen),
        IlArrayType array => IsOpen(array.ElementType),
        IlMultiDimensionalArrayType array => IsOpen(array.ElementType),
        IlPointerType pointer => IsOpen(pointer.ElementType),
        IlByRefType reference => IsOpen(reference.ElementType),
        IlPinnedType pinned => IsOpen(pinned.ElementType),
        IlModifiedType modified => IsOpen(modified.UnmodifiedType) || IsOpen(modified.Modifier),
        IlFunctionPointerType pointer => IsOpen(pointer.Signature.ReturnType) || pointer.Signature.ParameterTypes.Any(IsOpen),
        _ => false,
    };
}
