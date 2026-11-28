using System.Diagnostics;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Hardware;
using ChipSharp.Image.Uf2;
using ChipSharp.Runtime.Esp32c5;
using ChipSharp.Targets;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>A program built for the ESP32-C5: the linked .elf, and what the ELF reader makes of it.</summary>
internal sealed record Esp32c5Program(string Elf, ElfFile File);

/// <summary>
/// Builds C# for the ESP32-C5-DevKitC-1 the whole way to its .elf, as <c>chipsharp build -t esp32c5-devkitc</c> will:
/// Roslyn (with ChipSharp.Hardware referenced), the whole-program pass, IL to TIR, the llvm code generator for riscv32,
/// the runtime "esp32c5" on the runtime "riscv32", clang and ld.lld. The chip's runtime has no console yet (M4's wave 1
/// brings it): a program that prints is built with the shared Console.Write and Console.WriteLine
/// (chipsharp_console.c, chipsharp_float_text.c) and <see cref="ConsoleSink"/>, which keeps the text in RAM, for a
/// target with "console" (<see cref="WithConsole"/>).
/// </summary>
internal static class Esp32c5Build
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    /// <summary>The board, as its target files say.</summary>
    public static TargetDescription Board => Targets.Load("esp32c5-devkitc");

    /// <summary>Where the ROM's stack and data start: the end of what the program may load, and the top of its stack.</summary>
    public const uint SramStart = 0x40800000, LoadEnd = 0x4085c5a0;

    /// <summary>The tests' console: what the program writes goes into a buffer in RAM, which a debugger could read.</summary>
    public static GeneratedFile ConsoleSink { get; } = new("esp32c5_test_console.c", """
        // The tests' console, until the ESP32-C5's runtime has its own (UART0): the program's text, kept in RAM.
        char chipsharp_test_console[4096];
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
    public static Esp32c5Program Build(string source, string name, string folder, TargetDescription? target = null) =>
        Build(CSharpCompiler.Compile([source], [typeof(Pin).Assembly.Location], name), name, folder, target ?? Board);

    /// <summary>Builds the compiled C# <paramref name="assembly"/> into <paramref name="folder"/> for <paramref name="target"/>.</summary>
    public static Esp32c5Program Build(byte[] assembly, string name, string folder, TargetDescription target)
    {
        var program = WholeProgramPass.Analyze(IlAssembly.Load(assembly, name + ".dll"), target);
        var module = IlToTir.Lower(program);

        // as chipsharp build does: a program that never uses the heap is built for the target without one
        target = module.UsesHeap ? target : target.WithoutHeap();
        IArchRuntime runtime = new Esp32c5Runtime();
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
        return new Esp32c5Program(binary.Path, ElfFile.Read(binary.Path));
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

    /// <summary>Runs an LLVM tool next to clang (llvm-objdump, llvm-readelf) on <paramref name="arguments"/>: its output.</summary>
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
