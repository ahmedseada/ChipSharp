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
/// riscv32 with the ilp32e ABI, the runtime "ch32v003" on the runtime "riscv32e" (with its console on USART1), clang and
/// ld.lld.
/// </summary>
internal static class Ch32v003Build
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    /// <summary>The board, as its target files say.</summary>
    public static TargetDescription Board => Targets.Load("ch32v003-evt");

    /// <summary>The flash, where the program is linked (the core starts at its alias, 0x00000000), and its end: 16 KB.</summary>
    public const uint FlashStart = 0x08000000, FlashEnd = 0x08004000;

    /// <summary>The SRAM and its end, the top of the stack: 2 KB.</summary>
    public const uint SramStart = 0x20000000, SramEnd = 0x20000800;

    /// <summary>The board with the "heap" given, which the shipped board has none of.</summary>
    public static TargetDescription WithHeap(string heap)
    {
        var settings = Board.Settings.DeepClone().AsObject();
        settings["capabilities"]!["heap"] = heap;

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
                ["heap"] = heap,
            },
            HeapSize = SizeParser.Parse(heap),
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
        ]);
        var binary = new ClangToolchain(new ClangToolchainOptions { OutputFolder = folder, ProgramName = name })
            .Build(code, target);
        return new Ch32v003Program(binary.Path, ElfFile.Read(binary.Path));
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
