using System.Reflection.Metadata.Ecma335;

namespace ChipSharp.Frontend.Il;

public sealed partial class IlAssembly
{
    /// <summary>
    /// The size in bytes that <paramref name="type"/>'s layout gives it (<c>[StructLayout(Size = n)]</c>, which C# gives
    /// the types of the fields it keeps constant data in, e.g. <c>__StaticArrayInitTypeSize=12</c>); null when it gives
    /// none.
    /// </summary>
    public int? ClassSize(IlTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Assembly != this)
        {
            throw new ArgumentException($"{type} is a type of {type.Assembly}, not of {this}.", nameof(type));
        }

        var layout = _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(type.Token & 0xFFFFFF)).GetLayout();
        return layout.IsDefault || layout.Size <= 0 ? null : layout.Size;
    }

    /// <summary>
    /// True when <paramref name="type"/>'s layout is not C#'s default: <c>[StructLayout(Pack = n)]</c> or
    /// <c>[StructLayout(Size = n)]</c>, which .NET lays out otherwise than every field aligned to its size.
    /// </summary>
    public bool HasCustomLayout(IlTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var definition = type.GenericDefinition ?? type;
        if (definition.Assembly != this)
        {
            throw new ArgumentException($"{type} is a type of {definition.Assembly}, not of {this}.", nameof(type));
        }

        var layout = _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(definition.Token & 0xFFFFFF)).GetLayout();
        return !layout.IsDefault && (layout.PackingSize != 0 || layout.Size != 0);
    }

    /// <summary>True when <paramref name="field"/> has data in the image (an RVA), as the fields of constant data do.</summary>
    public bool HasFieldData(IlField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.DeclaringType.Assembly == this
            && _reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(field.Token & 0xFFFFFF))
                .GetRelativeVirtualAddress() != 0;
    }
}
