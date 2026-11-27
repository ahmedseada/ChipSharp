using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ChipSharp.Tests.Support;

/// <summary>
/// Compiles C# source text with Roslyn, in memory, the way <c>dotnet build -c Release</c> would: optimized, with
/// unsafe code allowed, against the .NET assemblies the tests run on (the shared-framework entries of
/// TRUSTED_PLATFORM_ASSEMBLIES). Unless told otherwise, source with a <c>Main</c> method or top-level statements
/// becomes a program and anything else a library. Compile errors throw, listing every error.
/// </summary>
public static class CSharpCompiler
{
    private static readonly Lazy<MetadataReference[]> References = new(LoadReferences);

    /// <summary>Every assembly compiled so far, by a hash of what went into it.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<byte[]>> Compiled = new(StringComparer.Ordinal);

    /// <summary>Compiles one source file and returns the assembly's bytes (optimized like a Release build unless told otherwise).</summary>
    public static byte[] Compile(string source, string assemblyName = "Test", OutputKind? outputKind = null,
        OptimizationLevel optimization = OptimizationLevel.Release) =>
        Compile([source], assemblyName, outputKind, optimization);

    /// <summary>Compiles several source files into one assembly and returns its bytes (optimized like a Release build unless told otherwise).</summary>
    public static byte[] Compile(IEnumerable<string> sources, string assemblyName = "Test", OutputKind? outputKind = null,
        OptimizationLevel optimization = OptimizationLevel.Release) =>
        Compile(sources, [], assemblyName, outputKind, optimization);

    /// <summary>
    /// Compiles several source files into one assembly that also references the assemblies at
    /// <paramref name="references"/> (e.g. ChipSharp.Hardware.dll), and returns its bytes.
    /// </summary>
    public static byte[] Compile(IEnumerable<string> sources, IEnumerable<string> references, string assemblyName = "Test",
        OutputKind? outputKind = null, OptimizationLevel optimization = OptimizationLevel.Release)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);

        // the compilation is deterministic: the same source compiles once per test run (the tests of the PC and of the
        // Pico, and the run on .NET, compile the same programs), and each caller gets its own copy of the bytes
        string[] texts = [.. sources];
        string[] paths = [.. references];
        var key = string.Join('\0', [assemblyName, outputKind?.ToString() ?? "", optimization.ToString(),
            .. paths.Select(path => $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}"), .. texts]);
        var compiled = Compiled.GetOrAdd(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))),
            _ => new Lazy<byte[]>(() => CompileNow(texts, paths, assemblyName, outputKind, optimization))).Value;
        return [.. compiled];
    }

    private static byte[] CompileNow(IReadOnlyList<string> sources, IReadOnlyList<string> references, string assemblyName,
        OutputKind? outputKind, OptimizationLevel optimization)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = sources.Select((text, i) => CSharpSyntaxTree.ParseText(text, parseOptions, path: $"Source{i}.cs")).ToList();
        var options = new CSharpCompilationOptions(
            outputKind ?? (trees.Any(HasMain) ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary),
            optimizationLevel: optimization,
            allowUnsafe: true,
            deterministic: true);
        var compilation = CSharpCompilation.Create(assemblyName, trees,
            [.. References.Value, .. references.Select(path => MetadataReference.CreateFromFile(path))], options);

        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        if (!result.Success)
        {
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
            throw new InvalidOperationException($"The C# does not compile:\n{string.Join("\n", errors)}");
        }

        return image.ToArray();
    }

    /// <summary>
    /// Compiles one source file and writes the assembly to <paramref name="path"/> (creating its folder);
    /// the assembly is named after the file. Returns the path.
    /// </summary>
    public static string CompileToFile(string source, string path, OutputKind? outputKind = null) =>
        CompileToFile([source], path, outputKind);

    /// <summary>Compiles several source files into one assembly written to <paramref name="path"/>. Returns the path.</summary>
    public static string CompileToFile(IEnumerable<string> sources, string path, OutputKind? outputKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var bytes = Compile(sources, Path.GetFileNameWithoutExtension(path), outputKind);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static bool HasMain(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        return root.ChildNodes().OfType<GlobalStatementSyntax>().Any()
            || root.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "Main");
    }

    private static MetadataReference[] LoadReferences()
    {
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES is not set.");

        // only the shared framework (where System.Object lives), not the test's own dependencies
        var framework = Path.GetDirectoryName(typeof(object).Assembly.Location);
        return
        [
            .. trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(file => string.Equals(Path.GetDirectoryName(file), framework, StringComparison.OrdinalIgnoreCase))
                .Select(file => MetadataReference.CreateFromFile(file)),
        ];
    }
}
