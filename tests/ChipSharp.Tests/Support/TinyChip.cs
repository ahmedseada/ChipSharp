using ChipSharp.Tests.Build;

namespace ChipSharp.Tests.Support;

/// <summary>
/// Made-up targets for the tests of what a target allows, written next to the shipped ones: "tiny", an ARMv6-M chip with
/// 16 KB of flash and 2 KB of RAM, which inherits armv6m's capabilities (no heap, no console, no interrupts); and
/// "tiny-flash", a Pico with 512 bytes of flash, which no program fits in.
/// </summary>
internal static class TinyChip
{
    public const string Name = "tiny";

    public const string Json = """
        {
          // a small ARMv6-M chip: no heap and no console (armv6m's capabilities), no interrupts
          "inherits": "armv6m",
          "cpu": "cortex-m0plus",
          "memory": {
            "flash": { "origin": "0x08000000", "size": "16K" },
            "ram":   { "origin": "0x20000000", "size": "2K" }
          },
          "link": { "stack": "512" }
        }
        """;

    public const string TinyFlash = "tiny-flash";

    public const string TinyFlashJson = """{ "inherits": "pico", "memory": { "flash": { "size": "512" } } }""";

    /// <summary>The shipped targets and the made-up ones, in <paramref name="scratch"/>\targets; returns that folder.</summary>
    public static string Targets(string scratch) =>
        TargetFiles.WithShipped(Path.Combine(scratch, "targets"), (Name, Json), (TinyFlash, TinyFlashJson));
}
