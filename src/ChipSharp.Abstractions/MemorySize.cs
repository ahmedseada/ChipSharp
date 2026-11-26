using System.Globalization;

namespace ChipSharp.Abstractions;

/// <summary>Amounts of memory as ChipSharp's messages and reports write them.</summary>
public static class MemorySize
{
    /// <summary>
    /// "1 byte", "8 bytes", then KB (1024 bytes): whole when it is ("2 KB", "2048 KB"), else with one decimal ("1.1 KB").
    /// </summary>
    public static string Text(ulong bytes) => bytes switch
    {
        1 => "1 byte",
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
        _ when bytes % 1024 == 0 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB"),
    };

    /// <summary>A memory region's name as messages write it: "RAM" for ram, else the target's own name for it ("flash").</summary>
    public static string RegionName(string name) =>
        string.Equals(name, "ram", StringComparison.OrdinalIgnoreCase) ? "RAM" : name;
}
