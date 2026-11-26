using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ChipSharp.Frontend.Il;

/// <summary>
/// Turns the types in signatures and type tokens into <see cref="IlType"/>s. Generic parameters stay positional
/// (<c>!0</c>, <c>!!0</c>), so no generic context is needed. Malformed metadata throws BadImageFormatException.
/// </summary>
internal sealed class IlTypeProvider(MetadataReader reader) : ISignatureTypeProvider<IlType, object?>
{
    // deeper than any real nesting of types: a chain this long is a cycle in malformed metadata
    private const int MaxNesting = 100;

    private readonly ConcurrentDictionary<TypeDefinitionHandle, IlNamedType> _definitions = new();

    /// <summary>A TypeDef, TypeRef or TypeSpec handle as a type.</summary>
    public IlType Decode(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => Definition((TypeDefinitionHandle)handle),
        HandleKind.TypeReference => Reference((TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null),
        _ => throw new BadImageFormatException($"Token 0x{MetadataTokens.GetToken(handle):X8} is not a type."),
    };

    public IlNamedType Definition(TypeDefinitionHandle handle) => Definition(handle, 0);

    public static IlMethodSignature Signature(MethodSignature<IlType> signature) => new(
        signature.Header.IsInstance,
        signature.ReturnType,
        [.. signature.ParameterTypes],
        signature.GenericParameterCount,
        signature.Header.CallingConvention switch
        {
            SignatureCallingConvention.Default => IlCallingConvention.Default,
            SignatureCallingConvention.CDecl => IlCallingConvention.CDecl,
            SignatureCallingConvention.StdCall => IlCallingConvention.StdCall,
            SignatureCallingConvention.ThisCall => IlCallingConvention.ThisCall,
            SignatureCallingConvention.FastCall => IlCallingConvention.FastCall,
            SignatureCallingConvention.VarArgs => IlCallingConvention.VarArgs,
            SignatureCallingConvention.Unmanaged => IlCallingConvention.Unmanaged,
            var other => throw new BadImageFormatException($"Unknown calling convention {other}."),
        });

    public IlType GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => IlType.Void,
        PrimitiveTypeCode.Boolean => IlType.Bool,
        PrimitiveTypeCode.Char => IlType.Char,
        PrimitiveTypeCode.SByte => IlType.Int8,
        PrimitiveTypeCode.Byte => IlType.UInt8,
        PrimitiveTypeCode.Int16 => IlType.Int16,
        PrimitiveTypeCode.UInt16 => IlType.UInt16,
        PrimitiveTypeCode.Int32 => IlType.Int32,
        PrimitiveTypeCode.UInt32 => IlType.UInt32,
        PrimitiveTypeCode.Int64 => IlType.Int64,
        PrimitiveTypeCode.UInt64 => IlType.UInt64,
        PrimitiveTypeCode.Single => IlType.Float32,
        PrimitiveTypeCode.Double => IlType.Float64,
        PrimitiveTypeCode.IntPtr => IlType.NativeInt,
        PrimitiveTypeCode.UIntPtr => IlType.NativeUInt,
        PrimitiveTypeCode.Object => IlType.Object,
        PrimitiveTypeCode.String => IlType.String,
        PrimitiveTypeCode.TypedReference => IlType.TypedReference,
        _ => throw new BadImageFormatException($"Unknown primitive type code {typeCode}."),
    };

    public IlType GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind) =>
        Definition(handle);

    public IlType GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind) =>
        Reference(handle, 0);

    public IlType GetTypeFromSpecification(MetadataReader metadata, object? genericContext,
        TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public IlType GetSZArrayType(IlType elementType) => new IlArrayType(elementType);

    public IlType GetArrayType(IlType elementType, ArrayShape shape) => new IlMultiDimensionalArrayType(elementType, shape.Rank);

    public IlType GetByReferenceType(IlType elementType) => new IlByRefType(elementType);

    public IlType GetPointerType(IlType elementType) => new IlPointerType(elementType);

    public IlType GetPinnedType(IlType elementType) => new IlPinnedType(elementType);

    public IlType GetModifiedType(IlType modifier, IlType unmodifiedType, bool isRequired) =>
        new IlModifiedType(unmodifiedType, modifier, isRequired);

    public IlType GetFunctionPointerType(MethodSignature<IlType> signature) => new IlFunctionPointerType(Signature(signature));

    public IlType GetGenericInstantiation(IlType genericType, ImmutableArray<IlType> typeArguments) =>
        new IlGenericInstanceType(genericType, [.. typeArguments]);

    public IlType GetGenericMethodParameter(object? genericContext, int index) => new IlGenericParameterType(index, true);

    public IlType GetGenericTypeParameter(object? genericContext, int index) => new IlGenericParameterType(index, false);

    private IlNamedType Definition(TypeDefinitionHandle handle, int depth)
    {
        if (_definitions.TryGetValue(handle, out var known))
        {
            return known;
        }

        if (depth > MaxNesting)
        {
            throw new BadImageFormatException("Nested type definitions form a cycle.");
        }

        var type = reader.GetTypeDefinition(handle);
        var declaring = type.GetDeclaringType();
        var named = new IlNamedType(reader.GetString(type.Namespace), reader.GetString(type.Name),
            declaring.IsNil ? null : Definition(declaring, depth + 1), null);
        return _definitions.GetOrAdd(handle, named);
    }

    private IlNamedType Reference(TypeReferenceHandle handle, int depth)
    {
        if (depth > MaxNesting)
        {
            throw new BadImageFormatException("Nested type references form a cycle.");
        }

        var type = reader.GetTypeReference(handle);
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        var scope = type.ResolutionScope;
        switch (scope.Kind)
        {
            case HandleKind.TypeReference:
            {
                var outer = Reference((TypeReferenceHandle)scope, depth + 1);
                return new IlNamedType(ns, name, outer, outer.Assembly);
            }

            case HandleKind.AssemblyReference:
            {
                var assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
                return new IlNamedType(ns, name, null, reader.GetString(assembly.Name));
            }

            case HandleKind.ModuleReference:
            {
                var module = reader.GetModuleReference((ModuleReferenceHandle)scope);
                return new IlNamedType(ns, name, null, reader.GetString(module.Name));
            }

            default:
                // the module itself, or no scope (look it up among the exported types): this assembly
                return new IlNamedType(ns, name, null, null);
        }
    }
}
