using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ChipSharp.Abstractions;

namespace ChipSharp.Frontend.Il;

public sealed partial class IlAssembly
{
    /// <summary>
    /// The interfaces <paramref name="type"/> says it implements, as its metadata lists them (for a generic type, in
    /// terms of its type parameters, <c>!0</c>); empty when there are none.
    /// </summary>
    internal IReadOnlyList<IlType> Interfaces(IlTypeDefinition type)
    {
        // a generic type of another assembly (ChipSharp.Library's), whose instance is this one's: its own metadata
        if (type.Assembly != this && type.GenericDefinition is null)
        {
            return type.Assembly.Interfaces(type);
        }

        var definition = Own(type);
        try
        {
            return
            [
                .. definition.GetInterfaceImplementations()
                    .Select(handle => _types.Decode(_reader.GetInterfaceImplementation(handle).Interface)),
            ];
        }
        catch (BadImageFormatException e)
        {
            throw new ChipSharpException($"{FilePath}: {type}: cannot read the interfaces it implements: {e.Message}", e);
        }
    }

    /// <summary>
    /// The explicit implementations of interface methods in <paramref name="type"/> (C#'s <c>void IDevice.Tick()</c>):
    /// each method of the type that implements one, with the interface method it implements, as the metadata names it
    /// (for a generic type, in terms of its type parameters).
    /// </summary>
    internal IReadOnlyList<(IlMethod Body, IlMethodReference Declaration)> MethodImplementations(IlTypeDefinition type)
    {
        if (type.Assembly != this && type.GenericDefinition is null)
        {
            return type.Assembly.MethodImplementations(type);
        }

        var definition = Own(type);
        var implementations = new List<(IlMethod, IlMethodReference)>();
        try
        {
            foreach (var handle in definition.GetMethodImplementations())
            {
                var implementation = _reader.GetMethodImplementation(handle);
                if (implementation.MethodBody.Kind == HandleKind.MethodDefinition
                    && _methodsByHandle.TryGetValue((MethodDefinitionHandle)implementation.MethodBody, out var body))
                {
                    implementations.Add((body, ResolveMethod(implementation.MethodDeclaration)));
                }
            }
        }
        catch (BadImageFormatException e)
        {
            throw new ChipSharpException(
                $"{FilePath}: {type}: cannot read the interface methods it implements: {e.Message}", e);
        }

        return implementations;
    }

    /// <summary>
    /// The variance of each of <paramref name="type"/>'s generic parameters, in order (C#'s <c>out T</c> is covariant,
    /// <c>in T</c> contravariant), as its metadata says; empty for a type that is not generic.
    /// </summary>
    internal IReadOnlyList<System.Reflection.GenericParameterAttributes> Variances(IlTypeDefinition type)
    {
        if (type.Assembly != this)
        {
            return type.Assembly.Variances(type);
        }

        var definition = Own(type);
        return
        [
            .. definition.GetGenericParameters().Select(handle => _reader.GetGenericParameter(handle).Attributes
                & System.Reflection.GenericParameterAttributes.VarianceMask),
        ];
    }

    /// <summary>
    /// The constraints of each of <paramref name="method"/>'s own generic parameters, in order: its attributes
    /// (<c>struct</c>, <c>class</c>, <c>new()</c>) and the types it must be (in terms of the generic parameters,
    /// <c>!!0</c>), as its metadata says; empty for a method that is not generic.
    /// </summary>
    internal IReadOnlyList<(System.Reflection.GenericParameterAttributes Attributes, IReadOnlyList<IlType> Types)>
        Constraints(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.DeclaringType.Assembly != this)
        {
            return method.DeclaringType.Assembly.Constraints(method);
        }

        var definition = _reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(method.Token & 0xFFFFFF));
        try
        {
            return
            [
                .. definition.GetGenericParameters().Select(handle => _reader.GetGenericParameter(handle)).Select(parameter =>
                    (parameter.Attributes, (IReadOnlyList<IlType>)[.. parameter.GetConstraints()
                        .Select(constraint => _types.Decode(_reader.GetGenericParameterConstraint(constraint).Type))])),
            ];
        }
        catch (BadImageFormatException e)
        {
            throw new ChipSharpException($"{FilePath}: {method}: cannot read its generic parameters' constraints: {e.Message}", e);
        }
    }

    /// <summary>The metadata of <paramref name="type"/>, a type of this assembly.</summary>
    private TypeDefinition Own(IlTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Assembly != this)
        {
            throw new ArgumentException($"{type} is a type of {type.Assembly}, not of {this}.", nameof(type));
        }

        return _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(type.Token & 0xFFFFFF));
    }
}
