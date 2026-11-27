using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Runtime.Windows;
using ChipSharp.Targets;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Build;

/// <summary>
/// The build cache of the clang toolchain: each source file compiled once for a target, its flags and its text; a later
/// build takes the objects from the cache and links the same program; another flag or another text is another object;
/// without the cache, nothing is kept.
/// </summary>
public sealed class BuildCacheTests : IDisposable
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [PicoFact]
    public void A_program_linked_from_cached_objects_is_the_program_compiled_from_its_sources()
    {
        var cache = Path.Combine(_scratch, "cache");
        var code = PicoCode(PicoBuild.BlinkSource);

        var compiled = Build(code, "compiled", useCache: false, cache);
        Assert.False(Directory.Exists(cache));
        var first = Build(code, "first", useCache: true, cache);
        var kept = Directory.GetFiles(cache);
        var second = Build(code, "second", useCache: true, cache);

        // one object for each .c, .S and .ll file of the build, made by the first build and taken by the second
        var sources = code.Files.Count(file => Path.GetExtension(file.FileName) is ".c" or ".S" or ".ll");
        Assert.Equal(sources, kept.Count(path => path.EndsWith(".o", StringComparison.Ordinal)));
        Assert.Equal(kept, Directory.GetFiles(cache));
        Assert.Equal(File.ReadAllBytes(compiled), File.ReadAllBytes(first));
        Assert.Equal(File.ReadAllBytes(compiled), File.ReadAllBytes(second));
    }

    [PicoFact]
    public void Another_flag_or_another_text_is_compiled_anew_and_the_rest_is_taken_from_the_cache()
    {
        var cache = Path.Combine(_scratch, "cache");
        var code = PicoCode(PicoBuild.BlinkSource);
        Build(code, "first", useCache: true, cache);
        var kept = Directory.GetFiles(cache).ToHashSet();

        Build(code, "verified", useCache: true, cache, [ClangToolchainOptions.VerifyHeapFlag]);
        var withFlag = Directory.GetFiles(cache).Except(kept).ToList();
        var other = PicoCode(PicoBuild.BlinkSource.Replace("500", "250", StringComparison.Ordinal));
        Build(other, "other", useCache: true, cache);
        var withText = Directory.GetFiles(cache).Except(kept).Except(withFlag).ToList();

        // a flag changes every object; another program only its own .ll
        Assert.Equal(kept.Count, withFlag.Count);
        Assert.Single(withText);
    }

    [HostFact]
    public async Task A_PC_program_takes_its_objects_and_import_library_from_the_cache_and_runs()
    {
        var cache = Path.Combine(_scratch, "cache");
        var shipped = Targets.Load("host");
        var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(
            CSharpCompiler.Compile(File.ReadAllText(Path.Combine(Samples.Folder, "Primes.cs")), "Primes"), "Primes.dll")));
        var host = module.UsesHeap ? shipped : shipped.WithoutHeap();
        IArchRuntime runtime = new WindowsX64Runtime();
        var code = new GeneratedCode([.. new LlvmCodeGenerator().Generate(module, host).Files, .. runtime.Startup(host).Files]);

        var compiled = Build(code, "compiled", useCache: false, cache, target: host);
        Build(code, "first", useCache: true, cache, target: host);
        var kept = Directory.GetFiles(cache);
        var program = Build(code, "second", useCache: true, cache, target: host);

        // the same program, byte for byte (its header's timestamp is a hash of it, not the time)
        Assert.Equal(File.ReadAllBytes(compiled), File.ReadAllBytes(program));
        Assert.Equal(kept, Directory.GetFiles(cache));
        Assert.Single(kept, path => path.EndsWith(".lib", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_scratch, "second", "kernel32.lib")));
        var run = await HostRun.RunAsync(program, [], _scratch);
        Assert.Equal(Samples.ExpectedOutput("Primes"), run.Output);
    }

    /// <summary>The C#'s generated code and the rp2040 runtime, as a build for the pico hands them to the toolchain.</summary>
    private static GeneratedCode PicoCode(string source)
    {
        var pico = PicoBuild.Pico.WithoutHeap();
        var bytes = CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly], "Blink");
        var module = IlToTir.Lower(WholeProgramPass.Analyze(IlAssembly.Load(bytes, "Blink.dll"), pico));
        IArchRuntime runtime = new Rp2040Runtime();
        return new GeneratedCode(
        [
            .. new LlvmCodeGenerator().Generate(module, pico).Files,
            .. runtime.Startup(pico).Files,
            .. runtime.InterruptEntry(pico).Files,
        ]);
    }

    /// <summary>Builds <paramref name="code"/> into the folder <paramref name="name"/>; returns the program.</summary>
    private string Build(GeneratedCode code, string name, bool useCache, string cache, IReadOnlyList<string>? flags = null,
        TargetDescription? target = null)
    {
        var options = new ClangToolchainOptions
        {
            OutputFolder = Path.Combine(_scratch, name),
            UseCache = useCache,
            CacheFolder = cache,
            ExtraFlags = flags ?? [],
        };
        return new ClangToolchain(options).Build(code, target ?? PicoBuild.Pico.WithoutHeap()).Path;
    }
}
