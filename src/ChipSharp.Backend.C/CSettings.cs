using System.Text.Json.Nodes;
using ChipSharp.Abstractions;

namespace ChipSharp.Backend.C;

/// <summary>
/// What a target says about the C compiler its programs go through, in its <c>"c"</c> settings; each has a default, so a
/// target needs none of them:
/// <code>
/// "c": {
///   "doubleBits": 32,          // C's double has 32 bits (SDCC's): a program that uses C#'s double is refused
///   "maxAlignment": 1,         // the most alignment the compiler gives any type (1: none, as on an 8-bit chip)
///   "constQualifier": "__code" // what else constant data is declared with, besides const (none by default)
/// }
/// </code>
/// <list type="bullet">
/// <item><c>doubleBits</c>: 64, or 32 when the target's toolchain is "sdcc" (whose double is a float on every chip).</item>
/// <item><c>maxAlignment</c>: 1 when the target's pointers have 2 bytes (an 8-bit chip, where neither SDCC nor avr-gcc
/// align anything), else none: every type aligned to its size, as C lays it out on the PC and on 32-bit chips. The
/// file checks, when it is compiled, that the compiler lays every struct out so (see <see cref="CTypes"/>).</item>
/// <item><c>constQualifier</c>: none. Constant data (string literals, tables, type descriptors) is <c>const</c>, which is
/// enough for SDCC to keep it in code memory on a Padauk (and in flash on an STM8), and its addresses are the compiler's
/// ordinary pointers, which read code memory too (SDCC's generic pointers: bit 15 set on a Padauk). A compiler that needs
/// a named address space for that gets it here, written right before the name of each constant object.</item>
/// </list>
/// </summary>
internal sealed record CSettings(int DoubleBits, int? MaxAlignment, string? ConstQualifier)
{
    /// <summary>The settings of <paramref name="target"/>, with the defaults for what it does not say.</summary>
    public static CSettings Read(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ChipSharpException Error(string message) => new($"Target '{target.Name}': {message}");

        var c = target.Settings["c"] switch
        {
            null => new JsonObject(),
            JsonObject settings => settings,
            _ => throw Error("'c' must be an object, e.g. { \"doubleBits\": 32 }."),
        };

        int? Number(string key, int[] allowed)
        {
            return c[key] switch
            {
                null => null,
                JsonValue value when value.TryGetValue(out int n) && allowed.Contains(n) => n,
                _ => throw Error($"'c.{key}' must be {string.Join(" or ", allowed)}."),
            };
        }

        var isSdcc = string.Equals(target.Toolchain, "sdcc", StringComparison.OrdinalIgnoreCase);
        var doubleBits = Number("doubleBits", [32, 64]) ?? (isSdcc ? 32 : 64);
        var maxAlignment = Number("maxAlignment", [1, 2, 4, 8]) ?? (target.PointerSize == 2 ? 1 : null);
        var qualifier = c["constQualifier"] switch
        {
            null => null,
            JsonValue value when value.TryGetValue(out string? text) && IsQualifier(text) => text,
            _ => throw Error("'c.constQualifier' must be one word of C, e.g. \"__code\"."),
        };

        return new CSettings(doubleBits, maxAlignment, qualifier);
    }

    private static bool IsQualifier(string text) =>
        text.Length > 0 && !char.IsAsciiDigit(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
