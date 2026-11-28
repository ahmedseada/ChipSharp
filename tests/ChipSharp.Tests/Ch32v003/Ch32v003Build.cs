using System.Diagnostics;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Hardware;
using ChipSharp.Image.Uf2;
using ChipSharp.Runtime.Ch32v003;
using ChipSharp.Targets;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Ch32v003;

/// <summary>A program built for the CH32V003: the linked .elf, and what the ELF reader makes of it.</summary>
internal sealed record Ch32v003Program(string Elf, ElfFile File);

/// <summary>
/// Builds C# for WCH's CH32V003 evaluation board the whole way to its .elf, as <c>chipsharp build -t ch32v003-evt</c>
/// does: Roslyn (with ChipSharp.Hardware referenced), the whole-program pass, IL to TIR, the llvm code generator for
/// riscv32 with the ilp32e ABI, the runtime "ch32v003" on the runtime "riscv32e", clang and ld.lld. The chip's runtime
/// has no console yet (M5's wave 1 brings it, on USART1): a program that prints is built with the shared Console.Write
/// and Console.WriteLine (chipsharp_console.c, chipsharp_float_text.c) and <see cref="ConsoleSink"/>, which keeps the
/// last bytes in RAM, for a target with "console" (<see cref="WithConsole"/>).
/// </summary>
internal static class Ch32v003Build
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    /// <summary>The board, as its target files say.</summary>
    public static TargetDescription Board => Targets.Load("ch32v003-evt");

    /// <summary>The flash, at the alias the core runs it from (0x00000000), and its end: 16 KB.</summary>
    public const uint FlashStart = 0x00000000, FlashEnd = 0x00004000;

    /// <summary>The SRAM and its end, the top of the stack: 2 KB.</summary>
    public const uint SramStart = 0x20000000, SramEnd = 0x20000800;

    /// <summary>The tests' console: the last 32 bytes the program writes, in RAM, where a debugger could read them.</summary>
    public static GeneratedFile ConsoleSink { get; } = new("ch32v003_test_console.c", """
        // The tests' console, until the CH32V003's runtime has its own (USART1): the program's last 32 bytes, in RAM.
        char chipsharp_test_console[32];
        volatile unsigned int chipsharp_test_console_count;

        void chipsharp_console_write(const char *bytes, unsigned int count)
        {
            for (unsigned int i = 0; i < count; i++)
            {
                chipsharp_test_console[chipsharp_test_console_count++ % sizeof chipsharp_test_console] = bytes[i];
            }
        }
        """);

    /// <summary>The board with "console": true (and the "heap" given), for <see cref="ConsoleSink"/>.</summary>
    public static TargetDescription WithConsole(string? heap = null)
    {
        var settings = Board.Settings.DeepClone().AsObject();
        settings["capabilities"]!["console"] = true;
        if (heap is not null)
        {
            settings["capabilities"]!["heap"] = heap;
        }

        var target = Board;
        return new TargetDescription
        {
            Name = target.Name,
            InheritanceChain = target.InheritanceChain,
            Backend = target.Backend,
            Toolchain = target.Toolchain,
            Runtime = target.Runtime,
            Triple = target.Triple,
            Cpu = target.Cpu,
            PointerSize = target.PointerSize,
            Endian = target.Endian,
            Image = target.Image,
            Flasher = target.Flasher,
            Memory = target.Memory,
            Pins = target.Pins,
            Capabilities = new Dictionary<string, string>(target.Capabilities, StringComparer.OrdinalIgnoreCase)
            {
                ["console"] = "true",
                ["heap"] = heap ?? "false",
            },
            HeapSize = heap is null ? null : SizeParser.Parse(heap),
            Settings = settings,
            MissingValues = target.MissingValues,
        };
    }

    /// <summary>What the whole-program pass says of the compiled C# <paramref name="assembly"/> for <paramref name="target"/>.</summary>
    public static AnalysisResult Check(byte[] assembly, string name, TargetDescription target) =>
        WholeProgramPass.Check(IlAssembly.Load(assembly, name + ".dll"), target);

    /// <summary>Compiles <paramref name="source"/> (with ChipSharp.Hardware) and builds it into <paramref name="folder"/>.</summary>
    public static Ch32v003Program Build(string source, string name, string folder, TargetDescription? target = null) =>
        Build(CSharpCompiler.Compile([source], [typeof(Pin).Assembly.Location], name), name, folder, target ?? Board);

    /// <summary>Builds the compiled C# <paramref name="assembly"/> into <paramref name="folder"/> for <paramref name="target"/>.</summary>
    public static Ch32v003Program Build(byte[] assembly, string name, string folder, TargetDescription target)
    {
        var program = WholeProgramPass.Analyze(IlAssembly.Load(assembly, name + ".dll"), target);
        var module = IlToTir.Lower(program);

        // as chipsharp build does: a program that never uses the heap is built for the target without one
        target = module.UsesHeap ? target : target.WithoutHeap();
        IArchRuntime runtime = new Ch32v003Runtime();
        var code = new GeneratedCode(
        [
            .. new LlvmCodeGenerator().Generate(module, target).Files,
            .. runtime.Startup(target).Files,
            .. runtime.InterruptEntry(target).Files,
            .. runtime.Atomics(target).Files,
            .. target.HasConsole ? Console(target) : [],
        ]);
        var binary = new ClangToolchain(new ClangToolchainOptions { OutputFolder = folder, ProgramName = name })
            .Build(code, target);
        return new Ch32v003Program(binary.Path, ElfFile.Read(binary.Path));
    }

    /// <summary>The shared console's C (and, without a heap, the text of floats it prints with), and the sink.</summary>
    private static IEnumerable<GeneratedFile> Console(TargetDescription target)
    {
        var shared = Path.Combine(Repository.Root, "src", "ChipSharp.Runtime.Shared");
        yield return new GeneratedFile("chipsharp_console.c", File.ReadAllText(Path.Combine(shared, "chipsharp_console.c")));
        if (!target.HasHeap)
        {
            // with a heap, the riscv32 runtime has it already
            yield return new GeneratedFile("chipsharp_float_text.c",
                File.ReadAllText(Path.Combine(shared, "chipsharp_float_text.c")));
        }

        yield return ConsoleSink;
    }

    /// <summary>Runs an LLVM tool next to clang (llvm-objdump, llvm-readobj) on <paramref name="arguments"/>: its output.</summary>
    public static string Tool(string name, params string[] arguments)
    {
        var start = new ProcessStartInfo(PicoTools.LlvmTool(name))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{name}: {errors.Result}");
        return output;
    }
}
