using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Build;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Image.Hex;
using ChipSharp.Runtime.Avr;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// Builds C# for the Arduino Uno the whole way, as <c>chipsharp build -t uno</c> builds it (the whole-program pass for
/// the Uno, IL to TIR, the llvm code generator, the runtime "avr", the toolchain "zig", the .hex), with a console for the
/// tests: <see cref="ConsoleSource"/>, USART0 at 115200 baud, written here for the tests and for nothing else, with the
/// shared runtime's Console.Write (chipsharp_console.c) and float text (chipsharp_float_text.c) over it.
/// </summary>
internal static class UnoBuild
{
    /// <summary>The test target: the Uno with a console ("uno-console", which inherits "uno").</summary>
    public const string TargetName = "uno-console";

    private static readonly Lazy<TargetDescription> UnoWithConsole = new(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "uno-targets-" + Environment.ProcessId);
        return new TargetLoader(TargetFiles.WithShipped(folder,
            (TargetName, """{ "inherits": "uno", "capabilities": { "console": true } }"""))).Load(TargetName);
    });

    /// <summary>The Uno, with a console.</summary>
    public static TargetDescription Target => UnoWithConsole.Value;

    /// <summary>
    /// USART0 for the tests' console: 115200 baud at 16 MHz (U2X0, UBRR0 = 16), 8N1, polled; the failures' messages go
    /// there too. chipsharp_chip_init (weak in the runtime "avr") sets it up before the program runs.
    /// </summary>
    public const string ConsoleSource = """
        #define REG(address) (*(volatile unsigned char *)(address))
        #define UCSR0A REG(0xC0)
        #define UCSR0B REG(0xC1)
        #define UCSR0C REG(0xC2)
        #define UBRR0L REG(0xC4)
        #define UBRR0H REG(0xC5)
        #define UDR0 REG(0xC6)

        void chipsharp_chip_init(void)
        {
            UBRR0H = 0;
            UBRR0L = 16;
            UCSR0A = 1 << 1;
            UCSR0C = 3 << 1;
            UCSR0B = 1 << 3;
        }

        void chipsharp_console_write(const char *bytes, unsigned int count)
        {
            for (unsigned int i = 0; i < count; i++)
            {
                while (!(UCSR0A & (1 << 5)))
                {
                }

                UDR0 = bytes[i];
            }
        }

        void chipsharp_failure_write(const char *text, unsigned int count)
        {
            chipsharp_console_write(text, count);
        }
        """;

    /// <summary>What the whole-program pass says of <paramref name="source"/> for the Uno.</summary>
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
        IArchRuntime runtime = new AvrRuntime();
        var code = new GeneratedCode(
        [
            .. new LlvmCodeGenerator().Generate(module, target).Files,
            .. runtime.Startup(target).Files,
            .. runtime.InterruptEntry(target).Files,
            .. runtime.Atomics(target).Files,
            Shared("chipsharp_console.c"),
            Shared("chipsharp_float_text.c"),
            new GeneratedFile("uno_test_console.c", ConsoleSource),
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

    /// <summary>A file of src/ChipSharp.Runtime.Shared, as the repository has it.</summary>
    private static GeneratedFile Shared(string fileName) => new(fileName,
        File.ReadAllText(Path.Combine(Repository.Root, "src", "ChipSharp.Runtime.Shared", fileName)));
}
