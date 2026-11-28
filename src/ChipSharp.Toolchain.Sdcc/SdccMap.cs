using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;

namespace ChipSharp.Toolchain.Sdcc;

/// <summary>One area of a linked program, as the map of SDCC's linker lists it, e.g. CODE at 0x8038, 55 bytes.</summary>
/// <param name="Absolute">
/// True for an area whose bytes are at fixed addresses (ABS: a Padauk chip's reset and interrupt vectors, its
/// pseudo-register p): its address in the map is 0 whatever its .org says, so it tells nothing of how far the program
/// reaches.
/// </param>
public sealed record SdccArea(string Name, ulong Address, ulong Size, bool Absolute)
{
    public ulong End => Address + Size;
}

/// <summary>
/// The map SDCC's linker (sdld) writes next to the program (&lt;name&gt;.map, from sdcc's -m): every area with its
/// address and size, in bytes, as in the .ihx (a Padauk chip's word n is at byte 2n). The areas of the RAM are known by
/// their names (<see cref="IsData"/>); the others are in the program memory.
/// </summary>
public sealed partial class SdccMap
{
    /// <summary>
    /// The areas SDCC puts in RAM: the variables (DATA; INITIALIZED, those with starting values, on the STM8), the
    /// overlaid locals and parameters of functions that call none (OSEG), the stack's marker (SSEG), variables at fixed
    /// addresses (DABS) and a Padauk chip's pseudo-registers (PREG, PREG2, RSEG, RSEG0). The rest (HOME, GSINIT,
    /// GSFINAL, CODE, CONST, INITIALIZER, CABS, HEADER...) are code and constants.
    /// </summary>
    private static readonly HashSet<string> DataAreas = new(StringComparer.Ordinal)
    {
        "DATA", "INITIALIZED", "OSEG", "SSEG", "DABS", "PREG", "PREG2", "RSEG", "RSEG0",
    };

    private SdccMap(IReadOnlyList<SdccArea> areas, IReadOnlyList<SdccFunction> functions)
    {
        Areas = areas;
        Functions = functions;
    }

    public IReadOnlyList<SdccArea> Areas { get; }

    /// <summary>
    /// The functions, the biggest first: a C symbol (leading underscore taken off) of the area CODE (or HOME), from
    /// its address to the next symbol's or the end of its area, in bytes. SDCC's own symbols (s_CODE, l_CODE) and
    /// the assembly's local labels are left out.
    /// </summary>
    public IReadOnlyList<SdccFunction> Functions { get; }

    /// <summary>
    /// The stack's area (SSEG): one byte, where the stack starts on a Padauk chip (it grows up from there) and nothing
    /// on an STM8 (whose stack starts at the end of RAM); what the variables take ends before it.
    /// </summary>
    public const string StackArea = "SSEG";

    /// <summary>True for an area SDCC puts in RAM (see <see cref="DataAreas"/>).</summary>
    public static bool IsData(string area) => DataAreas.Contains(area);

    /// <exception cref="ChipSharpException">The map cannot be read.</exception>
    public static SdccMap Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ChipSharpException($"Cannot read SDCC's map of the program '{path}': {e.Message}", e);
        }
    }

    /// <summary>The areas of a map's text, in the order it lists them.</summary>
    public static SdccMap Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var areas = AreaLine().Matches(text).Select(match => new SdccArea(
            match.Groups["name"].Value,
            ulong.Parse(match.Groups["address"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ulong.Parse(match.Groups["size"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            match.Groups["attributes"].Value.Split(',').Contains("ABS", StringComparer.Ordinal))).ToList();
        return new SdccMap(areas, FindFunctions(text, areas));
    }

    /// <summary>The symbols the map lists after each code area's line, as functions (see <see cref="Functions"/>).</summary>
    private static List<SdccFunction> FindFunctions(string text, List<SdccArea> areas)
    {
        var symbols = new List<(SdccArea Area, ulong Address, string Name)>();
        SdccArea? current = null;
        var next = 0;
        foreach (var line in text.Split('\n'))
        {
            if (AreaLine().Match(line) is { Success: true } area && next < areas.Count)
            {
                current = areas[next++];
            }
            else if (current is { Absolute: false, Name: "CODE" or "HOME" } && SymbolLine().Match(line) is { Success: true } symbol
                && symbol.Groups["name"].Value is ['_', ..] name)
            {
                symbols.Add((current, ulong.Parse(symbol.Groups["address"].Value, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture), name[1..]));
            }
        }

        return [.. symbols.GroupBy(symbol => symbol.Area).SelectMany(group =>
        {
            var sorted = group.OrderBy(symbol => symbol.Address).ToList();
            return sorted.Select((symbol, i) => new SdccFunction(symbol.Name, symbol.Area.Name,
                (i + 1 < sorted.Count ? sorted[i + 1].Address : symbol.Area.End) - symbol.Address));
        }).Where(function => function.Size > 0).OrderByDescending(function => function.Size)];
    }

    /// <summary>
    /// How much of the target's memory the program takes: the program memory up to the end of its furthest area of
    /// code (from the region's start, so with a Padauk chip's vectors before the code), and the RAM up to the end of its
    /// variables (the stack's marker left out). Areas at fixed addresses and empty ones are left out.
    /// </summary>
    public SdccMemoryUse Use(SdccSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ulong Reach(SdccRegion region, Func<SdccArea, bool> include) => Areas
            .Where(area => !area.Absolute && area.Size > 0 && include(area) && area.Address >= region.Region.Origin)
            .Select(area => area.End - region.Region.Origin)
            .DefaultIfEmpty(0UL)
            .Max();

        return new SdccMemoryUse(
            settings.Code, Reach(settings.Code, area => !IsData(area.Name)), settings.CodeWordSize,
            settings.Data, Reach(settings.Data, area => IsData(area.Name) && area.Name != StackArea), settings.StackSize);
    }

    // "     00008038  _main                              main"
    [GeneratedRegex(@"^\s+(?<address>[0-9A-Fa-f]{4,8})\s+(?<name>[A-Za-z_.$][\w.$]*)\s")]
    private static partial Regex SymbolLine();

    // "CODE                                00008038    00000037 =          55. bytes (REL,CON)"
    [GeneratedRegex(@"^(?<name>[A-Za-z_][\w$.]*)\s+(?<address>[0-9A-Fa-f]{4,8})\s+(?<size>[0-9A-Fa-f]{4,8})\s+=\s+\d+\.\s+bytes\s+\((?<attributes>[A-Z,]*)\)",
        RegexOptions.Multiline)]
    private static partial Regex AreaLine();
}

/// <summary>A function of a linked program: its C name and the code area it is in, and its size in bytes (up to the next symbol).</summary>
public sealed record SdccFunction(string Name, string Area, ulong Size);

/// <summary>
/// How much of the target's memory an SDCC program takes (<see cref="SdccMap.Use"/>): its code, constants and the
/// starting values of its variables in the code region, its variables in the data region, and the RAM it leaves for the
/// stack; in bytes, the code region's also in words when a word takes more than one byte (<see cref="CodeWordSize"/>).
/// </summary>
public sealed record SdccMemoryUse(SdccRegion Code, ulong CodeUsed, ulong CodeWordSize, SdccRegion Data, ulong DataUsed,
    ulong Stack)
{
    /// <summary>The map next to an SDCC program (&lt;name&gt;.ihx), or null when <paramref name="binary"/> is not one.</summary>
    public static string? MapOf(LinkedBinary binary)
    {
        ArgumentNullException.ThrowIfNull(binary);
        var map = Path.ChangeExtension(binary.Path, ".map");
        return string.Equals(Path.GetExtension(binary.Path), ".ihx", StringComparison.OrdinalIgnoreCase) && File.Exists(map)
            ? map
            : null;
    }

    /// <summary>The use of the program SDCC linked into <paramref name="binary"/> for <paramref name="target"/>, from its map.</summary>
    /// <exception cref="ChipSharpException">It has no map, or the target has no SDCC settings.</exception>
    public static SdccMemoryUse Read(LinkedBinary binary, TargetDescription target)
    {
        var map = MapOf(binary)
            ?? throw new ChipSharpException($"'{binary.Path}' is not a program SDCC linked: there is no .map next to it.");
        return SdccMap.Read(map).Use(SdccSettings.Read(target));
    }
}
