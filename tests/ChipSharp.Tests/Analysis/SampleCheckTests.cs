using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>The whole-program pass accepts every sample program in samples/.</summary>
public sealed class SampleCheckTests
{
    public static TheoryData<string> Samples() => new(SampleNames());

    [Fact]
    public void There_are_samples_to_check()
    {
        Assert.Contains("Square", SampleNames());
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void A_sample_passes_the_check(string name)
    {
        var source = File.ReadAllText(Path.Combine(SamplesFolder(), name + ".cs"));
        var assembly = IlAssembly.Load(CSharpCompiler.Compile(source, name), name + ".dll");

        var program = WholeProgramPass.Analyze(assembly);

        Assert.Equal("Program.Main", program.EntryPoint.DisplayName);
        Assert.Equal(program.Methods.Count, program.Methods.Distinct().Count());
    }

    private static string[] SampleNames() =>
    [
        .. Directory.GetFiles(SamplesFolder(), "*.cs")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>samples/ at the top of the repository: the first folder above the tests that has ChipSharp.slnx.</summary>
    private static string SamplesFolder()
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
