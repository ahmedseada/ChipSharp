using System.Globalization;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;

namespace ChipSharp.Backend.C;

/// <summary>
/// What a target says about the C compiler its programs go through: in its capabilities, and its <c>"c"</c> settings
/// (pdk14.json, stm8.json). Each has a default, so a target needs none of them:
/// <code>
/// "capabilities": {
///   "double64": false,         // C's double is not 64 bits (SDCC's is its float): a program that uses double is refused
///   "recursion": "reentrant"   // a function keeps its locals in static memory unless it is marked __reentrant
/// },
/// "c": {
///   "int": 2, "long": 4, "longLong": 8, "float": 4, "double": 4, "pointer": 2, "functionPointer": 2,
///   "charSigned": false,       // the sizes of the compiler's types in bytes, and whether a char is signed: checked
///                              // when the file is compiled; "double" says the double's size when "double64" does not
///   "maxAlignment": 1,         // the most alignment the compiler gives any type (1: none, as on an 8-bit chip)
///   "constQualifier": "__code", // what else constant data is declared with, besides const (none by default)
///   "largeFrame": 255,         // how many bytes of a frame the compiler reaches (none: all of it)
///   "stack": "up"              // which way the stack grows, for "stackCheck" (down by default)
/// }
/// </code>
/// <list type="bullet">
/// <item>The double: 64 bits, unless "double64" is false or "c.double" is 4, or the toolchain is "sdcc" (whose double
/// is a float on every chip).</item>
/// <item>"recursion": "reentrant" (SDCC's pdk14 port, which has no stack for locals unless asked): the functions that
/// may run again while they run (see <see cref="CCallGraph"/>) are <c>__reentrant</c>; any other value, or none, marks
/// nothing.</item>
/// <item>The alignment: 1 when the target's pointers have 2 bytes (an 8-bit chip, where neither SDCC nor avr-gcc
/// align anything), else none: every type aligned to its size, as C lays it out on the PC and on 32-bit chips. The
/// file checks, when it is compiled, that the compiler lays every struct out so (see <see cref="CTypes"/>).</item>
/// <item>The qualifier: none. Constant data (string literals, tables, type descriptors) is <c>const</c>, which is
/// enough for SDCC to keep it in code memory on a Padauk (and in flash on an STM8), and its addresses are the compiler's
/// ordinary pointers, which read code memory too (SDCC's generic pointers: bit 15 set on a Padauk). A compiler that
/// needs a named address space for that gets it here, written right before the name of each constant object.</item>
/// <item>The frame: all of it. SDCC's STM8 port reaches a function's locals and parameters through 8-bit offsets from
/// the stack pointer, and cannot return 3 or 4 bytes from a function whose frame they do not reach (an internal error
/// of its code generator): with "largeFrame", a function whose locals may take more returns such a value through a
/// pointer (see <see cref="CFunctionWriter.ResultName"/>).</item>
/// </list>
/// </summary>
internal sealed record CSettings(int DoubleBits, string? Reentrant, int? MaxAlignment, string? ConstQualifier,
    IReadOnlyList<(string Expression, int Bytes)> Sizes, bool? CharSigned, int? LargeFrame = null,
    bool StackGrowsUp = false)
{
    /// <summary>The keyword that gives a function a frame on the stack, for "recursion": "reentrant".</summary>
    public const string ReentrantKeyword = "__reentrant";

    /// <summary>
    /// The C type each size of the "c" settings is of. Not the double's: SDCC warns of any mention of double, and a
    /// program that uses one checks its size anyway.
    /// </summary>
    private static readonly (string Key, string Expression)[] SizeKeys =
    [
        ("int", "int"), ("long", "long"), ("longLong", "long long"), ("float", "float"), ("pointer", "void *"),
        ("functionPointer", "void (*)(void)"),
    ];

    /// <summary>The settings of <paramref name="target"/>, with the defaults for what it does not say.</summary>
    public static CSettings Read(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ChipSharpException Error(string message) => new($"Target '{target.Name}': {message}");

        var c = target.Settings["c"] switch
        {
            null => new JsonObject(),
            JsonObject settings => settings,
            _ => throw Error("'c' must be an object, e.g. { \"int\": 2 }."),
        };

        int? Number(string key, int[] allowed) => c[key] switch
        {
            null => null,
            JsonValue value when value.TryGetValue(out int n) && allowed.Contains(n) => n,
            _ => throw Error($"'c.{key}' must be {string.Join(" or ", allowed)}."),
        };

        var sizes = new List<(string, int)>();
        foreach (var (key, expression) in SizeKeys)
        {
            if (Number(key, [1, 2, 3, 4, 8]) is { } bytes)
            {
                sizes.Add((expression, bytes));
            }
        }

        var charSigned = c["charSigned"] switch
        {
            null => (bool?)null,
            JsonValue value when value.TryGetValue(out bool signed) => signed,
            _ => throw Error("'c.charSigned' must be true or false."),
        };

        var isSdcc = string.Equals(target.Toolchain, "sdcc", StringComparison.OrdinalIgnoreCase);
        var doubleBits = target.Capabilities.TryGetValue("double64", out var double64)
            ? string.Equals(double64, "true", StringComparison.OrdinalIgnoreCase) ? 64 : 32
            : Number("double", [4, 8]) is { } doubleBytes ? doubleBytes * 8
            : isSdcc ? 32 : 64;
        var reentrant = target.Capabilities.TryGetValue("recursion", out var recursion)
            && string.Equals(recursion, "reentrant", StringComparison.OrdinalIgnoreCase)
                ? ReentrantKeyword
                : null;
        var maxAlignment = Number("maxAlignment", [1, 2, 4, 8]) ?? (target.PointerSize == 2 ? 1 : null);
        var qualifier = c["constQualifier"] switch
        {
            null => null,
            JsonValue value when value.TryGetValue(out string? text) && IsWord(text) => text,
            _ => throw Error("'c.constQualifier' must be one word of C, e.g. \"__code\"."),
        };

        var largeFrame = c["largeFrame"] switch
        {
            null => (int?)null,
            JsonValue value when value.TryGetValue(out int bytes) && bytes > 0 => bytes,
            _ => throw Error("'c.largeFrame' must be a number of bytes above 0, e.g. 255."),
        };

        var stackGrowsUp = c["stack"] switch
        {
            null => false,
            JsonValue value when value.TryGetValue(out string? way) && way is "up" or "down" => way == "up",
            _ => throw Error("'c.stack' must be \"up\" or \"down\": which way the stack grows."),
        };

        return new CSettings(doubleBits, reentrant, maxAlignment, qualifier, sizes, charSigned, largeFrame, stackGrowsUp);
    }

    /// <summary>
    /// The conditions the file checks of the compiler's types, from the "c" settings: <c>sizeof(int) == 2</c>, ...,
    /// and <c>(char)-1 &lt; 0</c> (or its opposite).
    /// </summary>
    public IEnumerable<string> TypeConditions()
    {
        foreach (var (expression, bytes) in Sizes)
        {
            yield return $"sizeof({expression}) == {bytes.ToString(CultureInfo.InvariantCulture)}";
        }

        if (CharSigned is { } signed)
        {
            yield return signed ? "(char)-1 < 0" : "(char)-1 > 0";
        }
    }

    private static bool IsWord(string text) =>
        text.Length > 0 && !char.IsAsciiDigit(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
