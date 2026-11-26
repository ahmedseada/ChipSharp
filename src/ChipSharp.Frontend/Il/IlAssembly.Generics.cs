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
