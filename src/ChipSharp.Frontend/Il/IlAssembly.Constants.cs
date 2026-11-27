using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ChipSharp.Frontend.Il;

public sealed partial class IlAssembly
{
    /// <summary>
    /// The value of a constant field of a whole-number type (or bool or char), such as an enum's member, as its bits
    /// zero-extended to 64 bits: a <c>sbyte</c> -1 is 0xFF. Null for a field without a constant of such a type.
    /// </summary>
    public ulong? FieldConstant(IlField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.DeclaringType.Assembly != this && IsLibrary(field.DeclaringType.Assembly))
        {
            // a type of a library compiled with this assembly: that library's metadata
            return field.DeclaringType.Assembly.FieldConstant(field);
        }

        if (field.DeclaringType.Assembly != this)
        {
            throw new ArgumentException($"{field.Name} is a field of {field.DeclaringType.Assembly}, not of {this}.",
                nameof(field));
        }

        var definition = _reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(field.Token & 0xFFFFFF));
        if (definition.GetDefaultValue() is not { IsNil: false } handle)
        {
            return null;
        }

        var constant = _reader.GetConstant(handle);
        var blob = _reader.GetBlobReader(constant.Value);
        return constant.TypeCode switch
        {
            ConstantTypeCode.Boolean or ConstantTypeCode.Byte => blob.ReadByte(),
            ConstantTypeCode.SByte => (byte)blob.ReadSByte(),
            ConstantTypeCode.Char or ConstantTypeCode.UInt16 => blob.ReadUInt16(),
            ConstantTypeCode.Int16 => (ushort)blob.ReadInt16(),
            ConstantTypeCode.Int32 => (uint)blob.ReadInt32(),
            ConstantTypeCode.UInt32 => blob.ReadUInt32(),
            ConstantTypeCode.Int64 => (ulong)blob.ReadInt64(),
            ConstantTypeCode.UInt64 => blob.ReadUInt64(),
            _ => null,
        };
    }
}
