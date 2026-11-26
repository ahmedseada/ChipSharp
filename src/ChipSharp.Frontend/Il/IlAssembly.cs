using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ChipSharp.Abstractions;

namespace ChipSharp.Frontend.Il;

/// <summary>
/// A compiled .NET assembly (a .dll or .exe that Roslyn wrote), read with System.Reflection.Metadata: its types,
/// their methods and fields, and the entry point. Method bodies are decoded when first asked for
/// (<see cref="IlMethod.Body"/>). The whole file is read into memory, so nothing needs disposing.
/// Problems are reported as <see cref="ChipSharpException"/>s that name the file (and the method and offset).
/// </summary>
public sealed partial class IlAssembly
{
    private readonly PEReader _pe;
    private readonly MetadataReader _reader;
    private readonly IlTypeProvider _types;
    private readonly Dictionary<MethodDefinitionHandle, IlMethod> _methodsByHandle = [];
    private readonly Dictionary<FieldDefinitionHandle, IlField> _fieldsByHandle = [];
    private readonly Dictionary<string, IlTypeDefinition> _typesByFullName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<EntityHandle, IlMethodReference> _methodReferences = new();
    private readonly ConcurrentDictionary<EntityHandle, IlFieldReference> _fieldReferences = new();

    private IlAssembly(string filePath, PEReader pe)
    {
        FilePath = filePath;
        _pe = pe;
        _reader = pe.GetMetadataReader();
        _types = new IlTypeProvider(_reader);
        Name = _reader.IsAssembly
            ? _reader.GetString(_reader.GetAssemblyDefinition().Name)
            : Path.GetFileNameWithoutExtension(filePath);

        var types = new List<IlTypeDefinition>();
        var typesByHandle = new Dictionary<TypeDefinitionHandle, IlTypeDefinition>();
        foreach (var handle in _reader.TypeDefinitions)
        {
            var definition = _reader.GetTypeDefinition(handle);
            var type = new IlTypeDefinition(this, MetadataTokens.GetToken(handle), _types.Definition(handle),
                definition.Attributes, definition.BaseType.IsNil ? null : _types.Decode(definition.BaseType),
                GenericParameterNames(definition.GetGenericParameters()));
            types.Add(type);
            typesByHandle.Add(handle, type);
            _typesByFullName.TryAdd(type.FullName, type);
        }

        var methods = new List<IlMethod>();
        foreach (var handle in _reader.TypeDefinitions)
        {
            var type = typesByHandle[handle];
            var definition = _reader.GetTypeDefinition(handle);
            var declaring = definition.GetDeclaringType();
            type.DeclaringType = declaring.IsNil ? null : typesByHandle[declaring];

            foreach (var fieldHandle in definition.GetFields())
            {
                var field = _reader.GetFieldDefinition(fieldHandle);
                var il = new IlField(type, MetadataTokens.GetToken(fieldHandle), _reader.GetString(field.Name),
                    field.Attributes, field.DecodeSignature(_types, null));
                type.Add(il);
                _fieldsByHandle.Add(fieldHandle, il);
            }

            foreach (var methodHandle in definition.GetMethods())
            {
                var method = ReadMethod(type, methodHandle);
                type.Add(method);
                methods.Add(method);
                _methodsByHandle.Add(methodHandle, method);
            }
        }

        Types = types;
        Methods = methods;
        EntryPoint = FindEntryPoint();
    }

    /// <summary>The path (or name) the assembly was loaded from, as messages show it.</summary>
    public string FilePath { get; }

    /// <summary>The assembly's simple name, e.g. "Demo".</summary>
    public string Name { get; }

    /// <summary>Every type defined in the assembly, nested types included, in metadata order (the first is &lt;Module&gt;).</summary>
    public IReadOnlyList<IlTypeDefinition> Types { get; }

    /// <summary>Every method of every type, in metadata order.</summary>
    public IReadOnlyList<IlMethod> Methods { get; }

    /// <summary>The method the program starts with (C#'s <c>Main</c>), or null for a library.</summary>
    public IlMethod? EntryPoint { get; }

    /// <summary>Reads the assembly at <paramref name="path"/>.</summary>
    public static IlAssembly Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] image;
        try
        {
            image = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ChipSharpException($"Cannot read '{path}': {e.Message}", e);
        }

        return Load(image, path);
    }

    /// <summary>Reads an assembly from its bytes; <paramref name="filePath"/> is the name messages use for it.</summary>
    public static IlAssembly Load(byte[] image, string filePath)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            var pe = new PEReader(ImmutableArray.Create(image));
            if (!pe.HasMetadata)
            {
                throw new ChipSharpException($"'{filePath}' is not a .NET assembly: it has no metadata.");
            }

            return new IlAssembly(filePath, pe);
        }
        catch (BadImageFormatException e)
        {
            throw new ChipSharpException($"'{filePath}' is not a valid .NET assembly: {e.Message}", e);
        }
    }

    /// <summary>The methods whose <see cref="IlMethod.DisplayName"/> is <paramref name="displayName"/>: one, several overloads, or none.</summary>
    public IReadOnlyList<IlMethod> FindMethods(string displayName) =>
        [.. Methods.Where(m => string.Equals(m.DisplayName, displayName, StringComparison.Ordinal))];

    /// <summary>The type with this full name (e.g. "Demo.Outer+Inner"), or null.</summary>
    public IlTypeDefinition? FindType(string fullName) => _typesByFullName.GetValueOrDefault(fullName);

    /// <summary>
    /// The first <paramref name="length"/> bytes of the data <paramref name="field"/> has in the image (at its RVA),
    /// e.g. the constants of an array initializer, which C# keeps in a field of &lt;PrivateImplementationDetails&gt;;
    /// null when the field has no data there, or less than that.
    /// </summary>
    public byte[]? ReadFieldData(IlField field, int length)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (field.DeclaringType.Assembly != this)
        {
            throw new ArgumentException($"{field} is a field of {field.DeclaringType.Assembly}, not of {this}.", nameof(field));
        }

        var rva = _reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(field.Token & 0xFFFFFF))
            .GetRelativeVirtualAddress();
        if (rva == 0)
        {
            return null;
        }

        var data = _pe.GetSectionData(rva);
        return data.Length >= length ? [.. data.GetContent(0, length)] : null;
    }

    public override string ToString() => Name;

    internal IlMethodBody ReadBody(IlMethod method, int relativeVirtualAddress)
    {
        MethodBodyBlock block;
        try
        {
            block = _pe.GetMethodBody(relativeVirtualAddress);
        }
        catch (BadImageFormatException e)
        {
            throw new ChipSharpException($"{FilePath}: {method}: cannot read the method body: {e.Message}", e);
        }

        return IlBodyReader.Read(this, method, block);
    }

    /// <summary>An error in a method body, at an offset.</summary>
    internal ChipSharpException Error(IlMethod method, int offset, string message, Exception? inner = null) =>
        new($"{FilePath}: {method} at {IlPrinter.Label(offset)}: {message}.", inner);

    /// <summary>The handle for <paramref name="token"/>, which must be in one of <paramref name="tables"/> and name a row that exists.</summary>
    internal EntityHandle Handle(int token, string what, params TableIndex[] tables)
    {
        var table = (TableIndex)(token >>> 24);
        var row = token & 0xFFFFFF;
        if (!tables.Contains(table))
        {
            throw new BadImageFormatException($"token 0x{token:X8} is not {what}");
        }

        if (row == 0 || row > _reader.GetTableRowCount(table))
        {
            throw new BadImageFormatException($"token 0x{token:X8} names a row that does not exist");
        }

        return MetadataTokens.EntityHandle(token);
    }

    internal IlType ResolveType(EntityHandle handle) => _types.Decode(handle);

    internal IlMethodReference ResolveMethod(EntityHandle handle) => _methodReferences.GetOrAdd(handle, CreateMethodReference);

    internal IlFieldReference ResolveField(EntityHandle handle) => _fieldReferences.GetOrAdd(handle, CreateFieldReference);

    internal bool IsField(MemberReferenceHandle handle) =>
        _reader.GetMemberReference(handle).GetKind() == MemberReferenceKind.Field;

    internal string ResolveString(int token)
    {
        var offset = token & 0xFFFFFF;
        if (token >>> 24 != 0x70)
        {
            throw new BadImageFormatException($"token 0x{token:X8} is not a string");
        }

        if (offset == 0 || offset >= _reader.GetHeapSize(HeapIndex.UserString))
        {
            throw new BadImageFormatException($"token 0x{token:X8} names a string that does not exist");
        }

        return _reader.GetUserString(MetadataTokens.UserStringHandle(offset));
    }

    internal IlMethodSignature ResolveSignature(EntityHandle handle)
    {
        var signature = _reader.GetStandaloneSignature((StandaloneSignatureHandle)handle);
        if (signature.GetKind() != StandaloneSignatureKind.Method)
        {
            throw new BadImageFormatException(
                $"token 0x{MetadataTokens.GetToken(handle):X8} is a local variable signature, not a method signature");
        }

        return IlTypeProvider.Signature(signature.DecodeMethodSignature(_types, null));
    }

    internal IReadOnlyList<IlType> ResolveLocals(StandaloneSignatureHandle handle) =>
        handle.IsNil ? [] : [.. _reader.GetStandaloneSignature(handle).DecodeLocalSignature(_types, null)];

    private IlMethod ReadMethod(IlTypeDefinition type, MethodDefinitionHandle handle)
    {
        var definition = _reader.GetMethodDefinition(handle);
        var signature = IlTypeProvider.Signature(definition.DecodeSignature(_types, null));

        var names = new string?[signature.ParameterTypes.Count];
        foreach (var parameterHandle in definition.GetParameters())
        {
            var parameter = _reader.GetParameter(parameterHandle);
            if (parameter.SequenceNumber >= 1 && parameter.SequenceNumber <= names.Length)
            {
                names[parameter.SequenceNumber - 1] = _reader.GetString(parameter.Name);
            }
        }

        var thisCount = signature.HasThis ? 1 : 0;
        var parameters = signature.ParameterTypes
            .Select((parameterType, i) => new IlParameter(i,
                string.IsNullOrEmpty(names[i]) ? string.Create(CultureInfo.InvariantCulture, $"A_{i + thisCount}") : names[i]!,
                parameterType))
            .ToArray();

        return new IlMethod(type, MetadataTokens.GetToken(handle), _reader.GetString(definition.Name),
            definition.Attributes, definition.ImplAttributes, signature, parameters,
            GenericParameterNames(definition.GetGenericParameters()), definition.RelativeVirtualAddress);
    }

    private string[] GenericParameterNames(GenericParameterHandleCollection handles) =>
        [.. handles.Select(h => _reader.GetString(_reader.GetGenericParameter(h).Name))];

    private IlMethod? FindEntryPoint()
    {
        var header = _pe.PEHeaders.CorHeader;
        if (header is null || (header.Flags & CorFlags.NativeEntryPoint) != 0)
        {
            return null;
        }

        var token = header.EntryPointTokenOrRelativeVirtualAddress;
        if (token >>> 24 != (int)TableIndex.MethodDef)
        {
            return null;
        }

        return _methodsByHandle.GetValueOrDefault(MetadataTokens.MethodDefinitionHandle(token & 0xFFFFFF));
    }

    private IlMethodReference CreateMethodReference(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                var method = _methodsByHandle[(MethodDefinitionHandle)handle];
                return new IlMethodReference(method.DeclaringType.Type, method.Name, method.Signature, [], method);
            }

            case HandleKind.MemberReference:
            {
                var member = _reader.GetMemberReference((MemberReferenceHandle)handle);
                if (member.GetKind() != MemberReferenceKind.Method)
                {
                    throw new BadImageFormatException($"token 0x{MetadataTokens.GetToken(handle):X8} names a field, not a method");
                }

                var name = _reader.GetString(member.Name);
                var signature = IlTypeProvider.Signature(member.DecodeMethodSignature(_types, null));
                switch (member.Parent.Kind)
                {
                    case HandleKind.MethodDefinition:
                    {
                        // a call site of a vararg method defined here
                        var method = _methodsByHandle[(MethodDefinitionHandle)member.Parent];
                        return new IlMethodReference(method.DeclaringType.Type, name, signature, [], method);
                    }

                    case HandleKind.ModuleReference:
                    {
                        var module = _reader.GetModuleReference((ModuleReferenceHandle)member.Parent);
                        var global = new IlNamedType("", "<Module>", null, _reader.GetString(module.Name));
                        return new IlMethodReference(global, name, signature, [], null);
                    }

                    default:
                    {
                        var declaringType = _types.Decode(member.Parent);
                        var definition = LocalType(declaringType)?.Methods
                            .FirstOrDefault(m => m.Name == name && m.Signature == signature);
                        return new IlMethodReference(declaringType, name, signature, [], definition);
                    }
                }
            }

            case HandleKind.MethodSpecification:
            {
                var specification = _reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                var generic = ResolveMethod(specification.Method);
                return new IlMethodReference(generic.DeclaringType, generic.Name, generic.Signature,
                    [.. specification.DecodeSignature(_types, null)], generic.Definition);
            }

            default:
                throw new BadImageFormatException($"token 0x{MetadataTokens.GetToken(handle):X8} is not a method");
        }
    }

    private IlFieldReference CreateFieldReference(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.FieldDefinition:
            {
                var field = _fieldsByHandle[(FieldDefinitionHandle)handle];
                return new IlFieldReference(field.DeclaringType.Type, field.Name, field.Type, field);
            }

            case HandleKind.MemberReference:
            {
                var member = _reader.GetMemberReference((MemberReferenceHandle)handle);
                if (member.GetKind() != MemberReferenceKind.Field)
                {
                    throw new BadImageFormatException($"token 0x{MetadataTokens.GetToken(handle):X8} names a method, not a field");
                }

                var name = _reader.GetString(member.Name);
                var fieldType = member.DecodeFieldSignature(_types, null);
                var declaringType = _types.Decode(member.Parent);
                var definition = LocalType(declaringType)?.Fields.FirstOrDefault(f => f.Name == name && f.Type == fieldType);
                return new IlFieldReference(declaringType, name, fieldType, definition);
            }

            default:
                throw new BadImageFormatException($"token 0x{MetadataTokens.GetToken(handle):X8} is not a field");
        }
    }

    /// <summary>The definition of a type (or of a generic type's instance) defined in this assembly, or null.</summary>
    private IlTypeDefinition? LocalType(IlType type) => type switch
    {
        IlNamedType { IsDefinedInThisAssembly: true } named => FindType(named.FullName),
        IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: true } named } => FindType(named.FullName),
        _ => null,
    };
}
