using ChipSharp.Abstractions;

namespace ChipSharp.Toolchain.Sdcc;

/// <summary>
/// A program SDCC linked that does not fit in the target's memory, in ChipSharp's words: how much of each region it
/// needs, how much the target has, and the target. SDCC's linker checks only its port's largest chip (sdldpdk: "ROM
/// value 3057 too large (2048W max)"), sdldstm8 nothing at all; this checks the target's own regions, from the map.
/// </summary>
internal static class SdccMemoryOverflow
{
    /// <summary>
    /// "Blink does not fit in target 'pfs154-bare': its code, constants and the starting values of its variables need
    /// 3057 words of program memory, 1009 words more than the target's 2048 words. Make the program smaller, or build
    /// it for a chip with more memory."; null when it fits. When the program's code is too big and the map's
    /// <paramref name="functions"/> are known, the message names the biggest ones ("The biggest functions: Main (812
    /// words), ...").
    /// </summary>
    public static string? Message(SdccMemoryUse use, string program, TargetDescription target,
        IReadOnlyList<SdccFunction>? functions = null)
    {
        var needs = new List<string>();
        var (code, data) = (use.Code, use.Data);
        if (use.CodeUsed > code.Region.Size)
        {
            needs.Add("its code, constants and the starting values of its variables need " +
                Needs(code, use.CodeWordSize, use.CodeUsed, use.CodeUsed - code.Region.Size));
        }

        var needed = use.DataUsed + use.Stack;
        if (needed > data.Region.Size)
        {
            var over = needed - data.Region.Size;
            needs.Add(use.Stack == 0
                ? $"its variables need {Needs(data, 1, needed, over)}"
                : $"its variables ({MemorySize.Text(use.DataUsed)}) and the stack ({MemorySize.Text(use.Stack)}, " +
                  $"link.stack) need {Needs(data, 1, needed, over)}");
        }

        return needs.Count == 0
            ? null
            : $"{program} does not fit in target '{target.Name}': {string.Join("; ", needs)}. " +
              (use.CodeUsed > code.Region.Size ? Biggest(functions, use.CodeWordSize, use.CodeUsed) : "") +
              "Make the program smaller, or build it for a chip with more memory.";
    }

    /// <summary>
    /// "The biggest functions: Program_Main (812 words), Foo (300 words). "; empty without functions, and when they are
    /// less than half of the code (the constants are what makes the program big, not the functions).
    /// </summary>
    private static string Biggest(IReadOnlyList<SdccFunction>? functions, ulong wordSize, ulong codeUsed) =>
        functions is not { Count: > 0 } || (ulong)functions.Sum(function => (long)function.Size) * 2 < codeUsed ? ""
        : "The biggest functions: " + string.Join(", ",
            functions.Take(3).Select(function => $"{function.Name} ({MemorySize.Text(function.Size, wordSize)})")) + ". ";

    /// <summary>"RAM", "flash", "program memory".</summary>
    public static string RegionName(string name) =>
        string.Equals(name, "program", StringComparison.OrdinalIgnoreCase) ? "program memory" : MemorySize.RegionName(name);

    /// <summary>"3057 words of program memory, 1009 words more than the target's 2048 words".</summary>
    private static string Needs(SdccRegion region, ulong wordSize, ulong needed, ulong over) =>
        $"{MemorySize.Text(needed, wordSize)} of {RegionName(region.Name)}, {MemorySize.Text(over, wordSize)} more " +
        $"than the target's {MemorySize.Text(region.Region.Size, wordSize)}";
}
