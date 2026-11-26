namespace ChipSharp.Tests.Support;

/// <summary>
/// The sample programs in samples/ at the top of the repository: &lt;Name&gt;.cs, and &lt;Name&gt;.expected.txt with
/// what real .NET printed for it, byte for byte.
/// </summary>
public static class Samples
{
    /// <summary>samples/: next to the first ChipSharp.slnx above the tests.</summary>
    public static string Folder { get; } = FindFolder();

    /// <summary>Every sample's name, e.g. "Square", sorted.</summary>
    public static IReadOnlyList<string> Names =>
    [
        .. Directory.GetFiles(Folder, "*.cs")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>What real .NET printed for the sample.</summary>
    public static byte[] ExpectedOutput(string name) => File.ReadAllBytes(Path.Combine(Folder, name + ".expected.txt"));

    /// <summary>Compiles the sample (Release) to &lt;folder&gt;\&lt;name&gt;.dll and returns that path.</summary>
    public static string Compile(string name, string folder) =>
        CSharpCompiler.CompileToFile(File.ReadAllText(Path.Combine(Folder, name + ".cs")), Path.Combine(folder, name + ".dll"));

    private static string FindFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "ChipSharp.slnx")))
            {
                return Path.Combine(folder.FullName, "samples");
            }
        }

        throw new InvalidOperationException($"No ChipSharp.slnx above '{AppContext.BaseDirectory}'.");
    }
}
