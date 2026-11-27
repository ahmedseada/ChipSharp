using System.Reflection;
using ChipSharp.Ir;
using ChipSharp.Tests.Host;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// The programs in samples/, compiled the whole way (C# to IL with Roslyn, IL to TIR, TIR to LLVM IR, clang with the
/// windows-x64 runtime) and run: each must print exactly what real .NET printed, its &lt;Name&gt;.expected.txt.
/// </summary>
public sealed class SampleProgramTests : IDisposable
{
    private static readonly string Samples = FindSamplesFolder();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> SampleNames => [.. Names()];

    [HostFact]
    public Task Arithmetic() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task ArrayMethods() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Arrays() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task BitOps() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Boxing() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Classes() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Collections() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Comparisons() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Delegates() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task FactorialLong() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Fibonacci() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task FloatMath() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task ForLoops() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Gcd() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task GenericClasses() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Generics() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task HeapArrays() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task IfElse() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Logit() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task LookupTables() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Primes() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task PrintedText() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Projectile() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Shapes() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task ShortCircuit() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Spans() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Square() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task StaticFields() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Structs() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Text() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task TimeSpans() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task TrafficLights() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task Unsigned() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task VendingMachine() => PrintsWhatDotnetPrints();

    [HostFact]
    public Task WhileSum() => PrintsWhatDotnetPrints();

    [Fact]
    public void Every_sample_has_a_test_above()
    {
        var tests = typeof(SampleProgramTests).GetMethods()
            .Where(m => m.GetCustomAttribute<HostFactAttribute>() is not null && !m.Name.StartsWith("Every_"))
            .Select(m => m.Name)
            .Order(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(Names(), tests);
    }

    /// <summary>
    /// The samples compiled in Debug too: the IL differs a lot (nops, every value through a local, a bool local
    /// before each branch), and must still give the same output.
    /// </summary>
    [HostFact]
    public async Task Every_sample_compiled_in_Debug_prints_the_same()
    {
        var wrong = new List<string>();
        foreach (var name in Names())
        {
            var source = File.ReadAllText(Path.Combine(Samples, name + ".cs"));
            var module = Lowered.Lower(Lowered.Program(Lowered.CompileDebug(source, name)));

            var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, name + "-debug"));

            if (run.ExitCode != 0 || !run.Output.SequenceEqual(File.ReadAllBytes(Path.Combine(Samples, name + ".expected.txt"))))
            {
                wrong.Add($"{name}: {run}");
            }
        }

        Assert.Empty(wrong);
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Every_sample_becomes_well_formed_TIR_in_Release_and_Debug(string name)
    {
        var source = File.ReadAllText(Path.Combine(Samples, name + ".cs"));

        var release = Lowered.Module(source, assemblyName: name);
        var debug = Lowered.Lower(Lowered.Program(Lowered.CompileDebug(source, name)));

        Assert.Equal(name, release.Name);
        // a program with type initializers to run starts with <Start>, which runs them and then Main
        Assert.Contains(release.EntryPoint!.Name, (string[])["Program.Main", "<Start>"]);
        Assert.Empty(TirVerifier.FindErrors(release));
        Assert.Empty(TirVerifier.FindErrors(debug));
    }

    /// <summary>Builds and runs the sample the calling test is named after.</summary>
    private async Task PrintsWhatDotnetPrints([System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var module = Lowered.Module(File.ReadAllText(Path.Combine(Samples, name + ".cs")), assemblyName: name);

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, name));

        var expected = Path.Combine(Samples, name + ".expected.txt");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(File.ReadAllText(expected), run.Text);
        Assert.Equal(File.ReadAllBytes(expected), run.Output);
    }

    private static IEnumerable<string> Names() => Directory.GetFiles(Samples, "*.cs")
        .Select(path => Path.GetFileNameWithoutExtension(path))
        .Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>samples/ at the top of the repository: the first folder above the tests that has ChipSharp.slnx.</summary>
    private static string FindSamplesFolder()
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
