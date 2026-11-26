using System.Globalization;

namespace ChipSharp.Targets;

/// <summary>
/// Reads sizes and addresses as target files write them: "2M", "264K", "1G", "0x10000000" or "4096".
/// K, M and G are binary (1K = 1024 bytes), as chip datasheets use them.
/// </summary>
public static class SizeParser
{
    /// <exception cref="FormatException">The text is not a size or address.</exception>
    /// <exception cref="OverflowException">The value does not fit in 64 bits.</exception>
    public static ulong Parse(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
        {
            throw new FormatException("The size is empty.");
        }

        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.Parse(t.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        ulong multiplier = char.ToUpperInvariant(t[^1]) switch
        {
            'K' => 1UL << 10,
            'M' => 1UL << 20,
            'G' => 1UL << 30,
            _ => 1,
        };
        var digits = multiplier == 1 ? t.AsSpan() : t.AsSpan(0, t.Length - 1);
        return checked(ulong.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) * multiplier);
    }
}
