using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;

namespace ChipSharp.Toolchain.Clang;

/// <summary>
/// A chip program that does not fit in the target's memory, in ChipSharp's words rather than ld.lld's ("section
/// '.text' will not fit in region 'flash': overflowed by 136 bytes"): how much of each region the program needs, how
/// much the target has, and the target.
/// </summary>
/// <remarks>
/// ld.lld names each section that ends past its region, with how far past; the last one says by how much the region
/// as a whole overflowed. The data region of the linker script is the target's less the stack (see
/// <see cref="LinkerScript"/>), so the program needs its variables and the stack: the region's size and the overflow.
/// </remarks>
internal static partial class MemoryOverflow
{
    /// <summary>
    /// The message for a link that failed because the program does not fit, from what the linker wrote
    /// (<paramref name="output"/>), e.g. "Blink does not fit in target 'tiny': its code, constants and the starting
    /// values of its variables need 1.1 KB of flash, 152 bytes more than the target's 1 KB. ..."; null when the link
    /// failed for another reason.
    /// </summary>
    public static string? Message(string output, string program, TargetDescription target, ChipSettings settings)
    {
        var overflows = Overflow().Matches(output)
            .GroupBy(match => match.Groups["region"].Value, StringComparer.Ordinal)
            .ToDictionary(
                region => region.Key,
                region => region.Max(match => ulong.Parse(match.Groups["bytes"].Value, CultureInfo.InvariantCulture)),
                StringComparer.Ordinal);

        var needs = new List<string>();
        var (code, data) = (settings.Code, settings.Data);
        if (overflows.TryGetValue(code.Name, out var codeOver) && code.Name != data.Name)
        {
            needs.Add("its code, constants and the starting values of its variables need " +
                Needs(code, code.Region.Size + codeOver, codeOver));
        }

        if (overflows.TryGetValue(data.Name, out var dataOver))
        {
            var what = code.Name == data.Name ? "its code, constants and variables" : "its variables";
            var variables = data.Region.Size - settings.StackSize + dataOver;
            needs.Add(settings.StackSize == 0
                ? $"{what} need {Needs(data, variables, dataOver)}"
                : $"{what} ({MemorySize.Text(variables)}) and the stack ({MemorySize.Text(settings.StackSize)}, " +
                  $"link.stack) need {Needs(data, data.Region.Size + dataOver, dataOver)}");
        }

        return needs.Count == 0
            ? null
            : $"{program} does not fit in target '{target.Name}': {string.Join("; ", needs)}. Make the program " +
              "smaller, or build it for a chip with more memory.";
    }

    /// <summary>"1.1 KB of flash, 152 bytes more than the target's 1 KB".</summary>
    private static string Needs(NamedRegion region, ulong needed, ulong over) =>
        $"{MemorySize.Text(needed)} of {MemorySize.RegionName(region.Name)}, {MemorySize.Text(over)} more than the " +
        $"target's {MemorySize.Text(region.Region.Size)}";

    [GeneratedRegex(@"section '[^']*' will not fit in region '(?<region>[^']+)': overflowed by (?<bytes>\d+) bytes")]
    private static partial Regex Overflow();
}
