using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Targets;

namespace ChipSharp.Tests.Llvm;

/// <summary>Targets for the LLVM tests: made here, or the real pico target file.</summary>
internal static class LlvmTargets
{
    /// <summary>A PC running Windows, as the host target file describes it (with its heap of 256 MB).</summary>
    public static TargetDescription Host { get; } = Make("host", "x86_64-pc-windows-msvc", pointerSize: 8, heap: 256UL << 20);

    public static TargetDescription Riscv32 { get; } = Make("riscv32", "riscv32-unknown-elf", pointerSize: 4);

    /// <summary>The shipped pico target: thumbv6m-none-eabi, Cortex-M0+, 4-byte pointers.</summary>
    public static TargetDescription Pico => new TargetLoader(Path.Combine(AppContext.BaseDirectory, "targets")).Load("pico");

    /// <summary>A target of this name, triple and pointer size, with a heap of <paramref name="heap"/> bytes (null: none).</summary>
    public static TargetDescription Make(string name, string? triple, int pointerSize, ulong? heap = null) => new()
    {
        HeapSize = heap,
        Name = name,
        InheritanceChain = [name],
        Backend = "llvm",
        Triple = triple,
        PointerSize = pointerSize,
        Endian = Endianness.Little,
        Settings = new JsonObject(),
    };
}
