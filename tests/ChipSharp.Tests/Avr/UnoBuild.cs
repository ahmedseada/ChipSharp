using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Build;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Image.Hex;
using ChipSharp.Runtime.Atmega328p;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// Builds C# for the Arduino Uno the whole way, as <c>chipsharp build -t uno</c> builds it: the whole-program pass for
/// the Uno, IL to TIR (narrowed, as for the Uno), the llvm code generator, the runtime "atmega328p" on the runtime
/// "avr" (the console on USART0 at 115200 baud), the toolchain "zig", the .hex.
/// </summary>
internal static class UnoBuild
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    /// <summary>The Uno, as its target files say.</summary>
    public static TargetDescription Target => Targets.Load("uno");

    /// <summary>What the whole-program pass says of the compiled C# <paramref name="assembly"/> for the Uno.</summary>
    public static AnalysisResult Check(byte[] assembly, string name) =>
        WholeProgramPass.Check(IlAssembly.Load(assembly, name + ".dll"), Target);

    /// <summary>Compiles <paramref name="source"/> and builds it into <paramref name="folder"/> as &lt;name&gt;.elf and .hex.</summary>
    public static AvrProgram Build(string source, string name, string folder) =>
        Build(CSharpCompiler.Compile(source, name), name, folder);

    /// <summary>Builds the compiled C# <paramref name="assembly"/> into <paramref name="folder"/>.</summary>
    public static AvrProgram Build(byte[] assembly, string name, string folder) => Build(assembly, name, folder, Target);

    /// <summary>Builds the compiled C# <paramref name="assembly"/> into <paramref name="folder"/> for <paramref name="target"/>.</summary>
    public static AvrProgram Build(byte[] assembly, string name, string folder, TargetDescription target)
    {
        var program = WholeProgramPass.Analyze(IlAssembly.Load(assembly, name + ".dll"), target);
        var module = IlToTir.Lower(program);
        target = module.UsesHeap ? target : target.WithoutHeap();
        IArchRuntime runtime = new Atmega328pRuntime();
        var code = new GeneratedCode(
        [
            .. new LlvmCodeGenerator().Generate(module, target).Files,
            .. runtime.Startup(target).Files,
            .. runtime.InterruptEntry(target).Files,
            .. runtime.Atomics(target).Files,
        ]);
        var binary = new ClangToolchain(new ClangToolchainOptions
        {
            Driver = ClangDriver.Zig, OutputFolder = folder, ProgramName = name,
        }).Build(code, target);

        var hex = Path.ChangeExtension(binary.Path, ".hex");
        using (var output = File.Create(hex))
        {
            new IntelHexImageFormat().Package(binary, target, output);
        }

        return new AvrProgram(binary.Path, hex, MemoryReport.Read(binary, target));
    }
}
