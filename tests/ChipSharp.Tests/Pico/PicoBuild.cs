using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Hardware;
using ChipSharp.Image.Uf2;
using ChipSharp.Ir;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Pico;

/// <summary>A program built for the Pico: the linked .elf and the .uf2 made from it.</summary>
internal sealed record PicoProgram(string Elf, string Uf2)
{
    public string Folder => Path.GetDirectoryName(Elf)!;
}

/// <summary>
/// Builds C# for the Pico the whole way, as the build pipeline will: Roslyn (with ChipSharp.Hardware referenced),
/// the whole-program pass, IL to TIR, the llvm code generator, the rp2040 runtime, the clang toolchain and the uf2
/// image format, all for the real "pico" target file.
/// </summary>
internal static class PicoBuild
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    public static TargetDescription Pico => Targets.Load("pico");

    /// <summary>ChipSharp.Hardware.dll, as user programs reference it.</summary>
    public static string HardwareAssembly => typeof(Pin).Assembly.Location;

    /// <summary>ChipSharp.Components.dll, the drivers of parts connected to a chip, as user programs reference it.</summary>
    public static string ComponentsAssembly => typeof(ChipSharp.Components.Lcd).Assembly.Location;

    /// <summary>The blink example's C#, examples/pico-blink/Program.cs.</summary>
    public static string BlinkSource => File.ReadAllText(Path.Combine(Repository.Examples, "pico-blink", "Program.cs"));

    /// <summary>Compiles <paramref name="source"/> and builds it into <paramref name="folder"/> as &lt;name&gt;.elf and &lt;name&gt;.uf2.</summary>
    public static PicoProgram Build(string source, string name, string folder) =>
        Build(source, name, folder, Pico);

    public static PicoProgram Build(string source, string name, string folder, TargetDescription target) =>
        Build(source, name, folder, target, []);

    /// <summary>
    /// Compiles <paramref name="source"/> with ChipSharp.Hardware and the class libraries at <paramref name="libraries"/>
    /// (e.g. <see cref="ComponentsAssembly"/>) referenced, and builds it, those libraries' C# with it, into
    /// <paramref name="folder"/>.
    /// </summary>
    public static PicoProgram Build(string source, string name, string folder, TargetDescription target,
        IReadOnlyList<string> libraries)
    {
        var bytes = CSharpCompiler.Compile([source], [HardwareAssembly, .. libraries], name);
        var program = WholeProgramPass.Analyze(
            IlAssembly.Load(bytes, name + ".dll", [.. libraries.Select(IlAssembly.Load)]), target);
        return Build(IlToTir.Lower(program), folder, target);
    }

    /// <summary>
    /// Builds TIR the rest of the way into <paramref name="folder"/>, as &lt;module name&gt;.elf and .uf2: for programs
    /// C# cannot be compiled into yet.
    /// </summary>
    public static PicoProgram Build(TirModule module, string folder) => Build(module, folder, Pico);

    private static PicoProgram Build(TirModule module, string folder, TargetDescription target)
    {
        // as chipsharp build does: a program that never uses the heap is built for the target without one
        target = module.UsesHeap ? target : target.WithoutHeap();
        IArchRuntime runtime = new Rp2040Runtime();
        var code = new GeneratedCode(
        [
            .. new LlvmCodeGenerator().Generate(module, target).Files,
            .. runtime.Startup(target).Files,
            .. runtime.InterruptEntry(target).Files,
            .. runtime.Atomics(target).Files,
        ]);
        var binary = new ClangToolchain(new ClangToolchainOptions { OutputFolder = folder }).Build(code, target);

        var uf2 = Path.ChangeExtension(binary.Path, ".uf2");
        using (var output = File.Create(uf2))
        {
            new Uf2ImageFormat().Package(binary, target, output);
        }

        return new PicoProgram(binary.Path, uf2);
    }

    /// <summary>The bytes the .elf puts in flash, from its start, as one array (gaps are zero).</summary>
    public static byte[] FlashImage(ElfFile elf, uint flashStart = 0x10000000)
    {
        var loaded = elf.Segments.Where(s => s.Data.Length > 0).ToList();
        var end = loaded.Max(s => s.PhysicalAddress + (uint)s.Data.Length);
        var image = new byte[end - flashStart];
        foreach (var segment in loaded)
        {
            segment.Data.CopyTo(image, (int)(segment.PhysicalAddress - flashStart));
        }

        return image;
    }
}
