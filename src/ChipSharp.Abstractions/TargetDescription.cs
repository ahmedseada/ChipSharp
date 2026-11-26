using System.Text.Json.Nodes;

namespace ChipSharp.Abstractions;

public enum Endianness
{
    Little,
    Big,
}

/// <summary>A memory area of the chip, e.g. flash at 0x10000000, 2 MB long.</summary>
public sealed record MemoryRegion(ulong Origin, ulong Size)
{
    public ulong End => Origin + Size;
}

/// <summary>
/// Everything ChipSharp knows about one target after its inheritance chain is merged
/// (board -> chip -> architecture). The common settings are typed; anything family-specific
/// (the boot2 file, the UF2 family id, ...) is read from <see cref="Settings"/>.
/// </summary>
public sealed class TargetDescription
{
    public required string Name { get; init; }

    /// <summary>This target first, then each parent it inherits from, e.g. pico, rp2040, armv6m.</summary>
    public required IReadOnlyList<string> InheritanceChain { get; init; }

    /// <summary>The code generator to use, e.g. "llvm" or "c".</summary>
    public required string Backend { get; init; }

    public string? Toolchain { get; init; }
    public string? Runtime { get; init; }

    /// <summary>The LLVM target triple, e.g. "thumbv6m-none-eabi". Targets built through C have none.</summary>
    public string? Triple { get; init; }

    public string? Cpu { get; init; }

    /// <summary>Size of a pointer in bytes. C#'s <c>nint</c> follows it; <c>int</c> is always 32 bits.</summary>
    public required int PointerSize { get; init; }

    public required Endianness Endian { get; init; }
    public string? Image { get; init; }
    public string? Flasher { get; init; }
    public IReadOnlyDictionary<string, MemoryRegion> Memory { get; init; } = new Dictionary<string, MemoryRegion>();
    public IReadOnlyDictionary<string, string> Pins { get; init; } = new Dictionary<string, string>();

    /// <summary>What the hardware can do, e.g. hwDivide = false, atomics = none.</summary>
    public IReadOnlyDictionary<string, string> Capabilities { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The most memory the program's heap may take, from the capability "heap" (e.g. "224K"): where objects, text built
    /// while the program runs and collections live (M3). Null when the target has no heap ("heap": false, or not given):
    /// a program for it uses structs, static fields and arrays on the stack.
    /// </summary>
    public ulong? HeapSize { get; init; }

    /// <summary>True when the target has a heap (see <see cref="HeapSize"/>).</summary>
    public bool HasHeap => HeapSize is not null;

    /// <summary>True when Console.Write and Console.WriteLine have somewhere to go: the capability "console" is true.</summary>
    public bool HasConsole => HasCapability("console");

    /// <summary>The whole merged target file.</summary>
    public required JsonObject Settings { get; init; }

    /// <summary>
    /// Values a target that inherits this one must still provide, e.g. "memory.flash.size" on a chip
    /// whose flash is on the board. Only a complete target can be built.
    /// </summary>
    public IReadOnlyList<string> MissingValues { get; init; } = [];

    public bool IsComplete => MissingValues.Count == 0;

    /// <summary>True when the capability is set to true, e.g. <c>HasCapability("fpu")</c>.</summary>
    public bool HasCapability(string name) =>
        Capabilities.TryGetValue(name, out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>A single setting by dotted path, e.g. <c>Setting("boot.stage2")</c>, or null when absent.</summary>
    public string? Setting(string dottedPath)
    {
        JsonNode? node = Settings;
        foreach (var part in dottedPath.Split('.'))
        {
            node = node is JsonObject obj ? obj[part] : null;
        }

        return node is JsonValue value ? value.ToString() : null;
    }
}
