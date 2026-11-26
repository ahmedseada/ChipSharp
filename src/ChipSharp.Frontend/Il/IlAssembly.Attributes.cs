using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ChipSharp.Abstractions;

namespace ChipSharp.Frontend.Il;

/// <summary>
/// A custom attribute on a method as C# wrote it: its type and the arguments its constructor was called with, e.g.
/// <c>[Interrupt("timer")]</c> is ChipSharp.Hardware.InterruptAttribute with "timer". Named arguments
/// (<c>Name = value</c>) are left out.
/// </summary>
/// <param name="Type">The attribute's type.</param>
/// <param name="Arguments">
/// The constructor's arguments, in order: a bool, number, char or string as that .NET value, an enum as its underlying
/// number, a <c>typeof</c> as an <see cref="IlType"/>, an array as a list of these; null for null.
/// </param>
public sealed record IlAttribute(IlType Type, IReadOnlyList<object?> Arguments);

public sealed partial class IlAssembly
{
    /// <summary>
    /// The custom attributes on <paramref name="method"/> whose type has the full name <paramref name="typeFullName"/>,
    /// e.g. "ChipSharp.Hardware.InterruptAttribute", whichever assembly defines that type; empty when there are none.
    /// Only these are decoded: the arguments of another attribute may be enums of an assembly this one only refers to.
    /// </summary>
    public IReadOnlyList<IlAttribute> FindAttributes(IlMethod method, string typeFullName)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrEmpty(typeFullName);
        if (method.Assembly != this)
        {
            throw new ArgumentException($"{method} is a method of {method.Assembly}, not of {this}.", nameof(method));
        }

        var attributes = new List<IlAttribute>();
        var handle = MetadataTokens.MethodDefinitionHandle(method.Token & 0xFFFFFF);
        try
        {
            foreach (var attributeHandle in _reader.GetMethodDefinition(handle).GetCustomAttributes())
            {
                var attribute = _reader.GetCustomAttribute(attributeHandle);
                var type = ResolveMethod(attribute.Constructor).DeclaringType;
                if (IlTypeNames.DisplayName(type) != typeFullName)
                {
                    continue;
                }

                var value = attribute.DecodeValue(new AttributeTypes(this));
                attributes.Add(new IlAttribute(type, [.. value.FixedArguments.Select(Argument)]));
            }
        }
        catch (BadImageFormatException e)
        {
            throw new ChipSharpException($"{FilePath}: {method}: cannot read the attribute [{typeFullName}]: {e.Message}", e);
        }

        return attributes;
    }

    private static object? Argument(CustomAttributeTypedArgument<IlType> argument) => argument.Value switch
    {
        ImmutableArray<CustomAttributeTypedArgument<IlType>> array => array.Select(Argument).ToList(),
        var value => value,
    };

    /// <summary>The types in a custom attribute's arguments, for System.Reflection.Metadata's decoder.</summary>
    private sealed class AttributeTypes(IlAssembly assembly) : ICustomAttributeTypeProvider<IlType>
    {
        private static readonly IlNamedType SystemType = new("System", "Type", null, "System.Runtime");

        public IlType GetPrimitiveType(PrimitiveTypeCode typeCode) => assembly._types.GetPrimitiveType(typeCode);

        public IlType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            assembly._types.GetTypeFromDefinition(reader, handle, rawTypeKind);

        public IlType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            assembly._types.GetTypeFromReference(reader, handle, rawTypeKind);

        public IlType GetSZArrayType(IlType elementType) => new IlArrayType(elementType);

        public IlType GetSystemType() => SystemType;

        public bool IsSystemType(IlType type) => type is IlNamedType { Namespace: "System", Name: "Type", DeclaringType: null };

        public IlType GetTypeFromSerializedName(string name) => new IlNamedType("", name, null, "");

        /// <summary>An enum of this assembly: the type of its value__ field. Others cannot be looked into.</summary>
        public PrimitiveTypeCode GetUnderlyingEnumType(IlType type)
        {
            var field = type is IlNamedType { IsDefinedInThisAssembly: true } named
                ? assembly.FindType(named.FullName)?.Fields.FirstOrDefault(f => f is { Name: "value__", IsStatic: false })
                : null;
            return field?.Type switch
            {
                IlPrimitiveType { Kind: IlPrimitiveKind.Bool } => PrimitiveTypeCode.Boolean,
                IlPrimitiveType { Kind: IlPrimitiveKind.Char } => PrimitiveTypeCode.Char,
                IlPrimitiveType { Kind: IlPrimitiveKind.Int8 } => PrimitiveTypeCode.SByte,
                IlPrimitiveType { Kind: IlPrimitiveKind.UInt8 } => PrimitiveTypeCode.Byte,
                IlPrimitiveType { Kind: IlPrimitiveKind.Int16 } => PrimitiveTypeCode.Int16,
                IlPrimitiveType { Kind: IlPrimitiveKind.UInt16 } => PrimitiveTypeCode.UInt16,
                IlPrimitiveType { Kind: IlPrimitiveKind.Int32 } => PrimitiveTypeCode.Int32,
                IlPrimitiveType { Kind: IlPrimitiveKind.UInt32 } => PrimitiveTypeCode.UInt32,
                IlPrimitiveType { Kind: IlPrimitiveKind.Int64 } => PrimitiveTypeCode.Int64,
                IlPrimitiveType { Kind: IlPrimitiveKind.UInt64 } => PrimitiveTypeCode.UInt64,
                _ => throw new BadImageFormatException($"the enum {type} is not one of {assembly.Name}'s"),
            };
        }
    }
}
