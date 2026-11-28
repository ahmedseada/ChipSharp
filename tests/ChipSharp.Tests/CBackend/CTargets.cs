using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Targets;

namespace ChipSharp.Tests.CBackend;

/// <summary>Targets for the tests of the c code generator: the shipped host-c, and chips of SDCC's made here.</summary>
internal static class CTargets
{
    /// <summary>The shipped host-c target: the PC, its programs through the c code generator and clang.</summary>
    public static TargetDescription HostC =>
        new TargetLoader(Path.Combine(AppContext.BaseDirectory, "targets")).Load("host-c");

    /// <summary>A Padauk PDK14 chip as SDCC compiles for it: 2-byte (generic) pointers, no heap, double of 32 bits.</summary>
    public static TargetDescription Pdk14 { get; } = Make("pdk14-test", pointerSize: 2, Endianness.Little, toolchain: "sdcc");

    /// <summary>An STM8 as SDCC compiles for it: 2-byte pointers, big endian, no heap, double of 32 bits.</summary>
    public static TargetDescription Stm8 { get; } = Make("stm8-test", pointerSize: 2, Endianness.Big, toolchain: "sdcc");

    /// <summary>A target of the c code generator with these settings, its "c" settings <paramref name="c"/>.</summary>
    public static TargetDescription Make(string name, int pointerSize, Endianness endian = Endianness.Little,
        string? toolchain = null, ulong? heap = null, JsonObject? c = null, string? triple = null,
        IReadOnlyDictionary<string, string>? capabilities = null) => new()
    {
        Name = name,
        InheritanceChain = [name],
        Backend = "c",
        Toolchain = toolchain,
        Triple = triple,
        PointerSize = pointerSize,
        Endian = endian,
        HeapSize = heap,
        Capabilities = capabilities ?? new Dictionary<string, string>(),
        Settings = c is null ? new JsonObject() : new JsonObject { ["c"] = c },
    };
}
