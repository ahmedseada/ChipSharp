using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Targets;

namespace ChipSharp.Tests.Llvm;

/// <summary>Targets for the LLVM tests: made here, or the real pico target file.</summary>
internal static class LlvmTargets
{
    /// <summary>A PC running Windows, as the host target file describes it (with its heap of 256 MB, and unaligned access).</summary>
    public static TargetDescription Host { get; } = Make("host", "x86_64-pc-windows-msvc", pointerSize: 8, heap: 256UL << 20,
        capabilities: new Dictionary<string, string> { ["unalignedAccess"] = "true" });

    public static TargetDescription Riscv32 { get; } = Make("riscv32", "riscv32-unknown-elf", pointerSize: 4);

    /// <summary>The shipped pico target: thumbv6m-none-eabi, Cortex-M0+, 4-byte pointers.</summary>
    public static TargetDescription Pico => new TargetLoader(Path.Combine(AppContext.BaseDirectory, "targets")).Load("pico");

    /// <summary>
    /// A target of this name, triple and pointer size, with a heap of <paramref name="heap"/> bytes (null: none) and the
    /// <paramref name="capabilities"/> given (none: then, as a chip's file that does not say, no unaligned access).
    /// </summary>
    public static TargetDescription Make(string name, string? triple, int pointerSize, ulong? heap = null,
        IReadOnlyDictionary<string, string>? capabilities = null) => new()
    {
        HeapSize = heap,
        Name = name,
        InheritanceChain = [name],
        Backend = "llvm",
        Triple = triple,
        PointerSize = pointerSize,
        Endian = Endianness.Little,
        Capabilities = capabilities ?? new Dictionary<string, string>(),
        Settings = new JsonObject(),
    };
}
