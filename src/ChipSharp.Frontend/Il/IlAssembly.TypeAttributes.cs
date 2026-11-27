using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ChipSharp.Abstractions;

namespace ChipSharp.Frontend.Il;

public sealed partial class IlAssembly
{
    /// <summary>
    /// The custom attributes on <paramref name="type"/> whose type has the full name <paramref name="typeFullName"/>,
    /// e.g. "ChipSharp.Library.SubstituteAttribute", as <see cref="FindAttributes(IlMethod, string)"/> finds them on a
    /// method; empty when there are none.
    /// </summary>
    public IReadOnlyList<IlAttribute> FindAttributes(IlTypeDefinition type, string typeFullName)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(typeFullName);
        if (type.Assembly != this && IsLibrary(type.Assembly))
        {
            // a type of a library compiled with this assembly: that library's metadata
            return type.Assembly.FindAttributes(type, typeFullName);
        }

        if (type.Assembly != this)
        {
            throw new ArgumentException($"{type} is a type of {type.Assembly}, not of {this}.", nameof(type));
        }

        var attributes = new List<IlAttribute>();
        var handle = MetadataTokens.TypeDefinitionHandle(type.Token & 0xFFFFFF);
        try
        {
            foreach (var attributeHandle in _reader.GetTypeDefinition(handle).GetCustomAttributes())
            {
                var attribute = _reader.GetCustomAttribute(attributeHandle);
                var attributeType = AttributeType(attribute.Constructor);
                if (IlTypeNames.DisplayName(attributeType) != typeFullName)
                {
                    continue;
                }

                var value = attribute.DecodeValue(new AttributeTypes(this));
                attributes.Add(new IlAttribute(attributeType, [.. value.FixedArguments.Select(Argument)]));
            }
        }
        catch (BadImageFormatException e)
        {
            throw new ChipSharpException(
                $"{FilePath}: {type}: cannot read the attribute [{typeFullName}]: {e.Message}", e);
        }

        return attributes;
    }

    /// <summary>
    /// The type an attribute's constructor belongs to, read from the metadata alone: unlike a method an instruction
    /// calls (<see cref="ResolveMethod"/>), it is not linked to a library substitute, which reading the substitutes'
    /// own attributes must not need.
    /// </summary>
    private IlType AttributeType(EntityHandle constructor) => constructor.Kind switch
    {
        HandleKind.MethodDefinition => _methodsByHandle[(MethodDefinitionHandle)constructor].DeclaringType.Type,
        HandleKind.MemberReference => _types.Decode(_reader.GetMemberReference((MemberReferenceHandle)constructor).Parent),
        _ => throw new BadImageFormatException(
            $"token 0x{MetadataTokens.GetToken(constructor):X8} is not an attribute's constructor"),
    };
}
