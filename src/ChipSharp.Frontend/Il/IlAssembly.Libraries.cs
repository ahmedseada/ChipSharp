using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ChipSharp.Abstractions;

namespace ChipSharp.Frontend.Il;

public sealed partial class IlAssembly
{
    /// <summary>
    /// The class libraries whose C# ChipSharp compiles together with this assembly, as if it were the assembly's own:
    /// those it references that are next to it (as <c>dotnet build</c> leaves a program's project references and
    /// packages), and theirs, each once, a library before those that use it. Their types are this assembly's types
    /// (<see cref="IlNamedType.IsDefinedInThisAssembly"/>, <see cref="FindType"/>), and a call into one of their methods
    /// is a call to that method, which the whole-program pass reaches, checks and compiles like the program's own: only
    /// what the program calls. Empty for an assembly read from its bytes alone.
    /// </summary>
    /// <remarks>
    /// .NET's own assemblies (System.*, Microsoft.*, netstandard, mscorlib) are never among them, nor ChipSharp.Hardware,
    /// whose methods are the chip's runtime functions and ChipSharp.Library's substitutes.
    /// </remarks>
    public IReadOnlyList<IlAssembly> Libraries { get; }

    /// <summary>True when ChipSharp compiles the code of the assembly named <paramref name="name"/> where a program uses it.</summary>
    private static bool IsCompiledLibrary(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name is not ("ChipSharp.Hardware" or "System" or "mscorlib" or "netstandard")
            && !name.StartsWith("System.", StringComparison.Ordinal)
            && !name.StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the assembly at <paramref name="path"/> after the class libraries it references that are next to it;
    /// <paramref name="loaded"/> holds those read so far by name (null while one is being read: a cycle).
    /// </summary>
    private static IlAssembly LoadWithLibraries(string path, Dictionary<string, IlAssembly?> loaded)
    {
        var image = ReadFile(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var libraries = new List<IlAssembly>();
        foreach (var name in ReferencedNames(image, path).Where(IsCompiledLibrary))
        {
            if (loaded.TryGetValue(name, out var known))
            {
                if (known is not null)
                {
                    libraries.Add(known);
                }

                continue;
            }

            var file = Path.Combine(folder, name + ".dll");
            if (!File.Exists(file))
            {
                continue;
            }

            loaded[name] = null;
            var library = LoadWithLibraries(file, loaded);
            loaded[name] = library;
            libraries.Add(library);
        }

        return Load(image, path, libraries);
    }

    private static byte[] ReadFile(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ChipSharpException($"Cannot read '{path}': {e.Message}", e);
        }
    }

    /// <summary>The names of the assemblies the assembly in <paramref name="image"/> references.</summary>
    private static List<string> ReferencedNames(byte[] image, string filePath)
    {
        try
        {
            using var pe = new PEReader(ImmutableArray.Create(image));
            if (!pe.HasMetadata)
            {
                return [];
            }

            var reader = pe.GetMetadataReader();
            return [.. reader.AssemblyReferences.Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))];
        }
        catch (BadImageFormatException e)
        {
            throw new ChipSharpException($"'{filePath}' is not a valid .NET assembly: {e.Message}", e);
        }
    }

    /// <summary><paramref name="libraries"/> and the libraries of each, each once, a library before those that use it.</summary>
    private static List<IlAssembly> WithTheirLibraries(IReadOnlyList<IlAssembly> libraries)
    {
        var all = new List<IlAssembly>();
        foreach (var library in libraries)
        {
            foreach (var one in library.Libraries.Append(library))
            {
                if (!all.Contains(one))
                {
                    all.Add(one);
                }
            }
        }

        return all;
    }

    /// <summary>The type of one of <see cref="Libraries"/> with this full name (its own, not an instance), or null.</summary>
    private IlTypeDefinition? FindLibraryType(string fullName)
    {
        foreach (var library in Libraries)
        {
            if (library._typesByFullName.GetValueOrDefault(fullName) is { } type)
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="assembly"/> is one of <see cref="Libraries"/>: its types are this one's.</summary>
    private bool IsLibrary(IlAssembly assembly) => Libraries.Contains(assembly);

    /// <summary>
    /// True when a library's method of signature <paramref name="definition"/> is the one this assembly names with
    /// <paramref name="reference"/>: the same signature but for the assemblies .NET's types are named from, which differ
    /// with what each was compiled against (System.Runtime for a project's build, System.Private.CoreLib or netstandard
    /// for another).
    /// </summary>
    private static bool SameByName(IlMethodSignature definition, IlMethodSignature reference) =>
        definition.HasThis == reference.HasThis
        && definition.GenericParameterCount == reference.GenericParameterCount
        && definition.CallingConvention == reference.CallingConvention
        && definition.ParameterTypes.Count == reference.ParameterTypes.Count
        && Unscoped(definition.ReturnType) == Unscoped(reference.ReturnType)
        && definition.ParameterTypes.Zip(reference.ParameterTypes).All(pair => Unscoped(pair.First) == Unscoped(pair.Second));

    /// <summary><paramref name="type"/> with every named type in it named without its assembly.</summary>
    private static IlType Unscoped(IlType type) => type switch
    {
        IlNamedType named => named with
        {
            Assembly = null,
            DeclaringType = named.DeclaringType is null ? null : (IlNamedType)Unscoped(named.DeclaringType),
        },
        IlGenericInstanceType instance => new IlGenericInstanceType(Unscoped(instance.Definition),
            [.. instance.Arguments.Select(Unscoped)]),
        IlArrayType array => new IlArrayType(Unscoped(array.ElementType)),
        IlMultiDimensionalArrayType array => array with { ElementType = Unscoped(array.ElementType) },
        IlPointerType pointer => new IlPointerType(Unscoped(pointer.ElementType)),
        IlByRefType reference => new IlByRefType(Unscoped(reference.ElementType)),
        IlPinnedType pinned => new IlPinnedType(Unscoped(pinned.ElementType)),
        IlModifiedType modified => new IlModifiedType(Unscoped(modified.UnmodifiedType), Unscoped(modified.Modifier),
            modified.IsRequired),
        _ => type,
    };
}
