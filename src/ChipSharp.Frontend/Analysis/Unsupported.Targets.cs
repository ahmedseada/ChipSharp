using ChipSharp.Abstractions;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// What a target does not have that the program needs, in C# words (see <see cref="TargetRules"/>): a heap, or a
/// console. Like a rule of interrupt handlers it never "arrives": its heading says what the program needs and that the
/// target does not have it, and its hint holds for good, not "for now".
/// </summary>
internal sealed partial record Unsupported
{
    private const string ForNow = "For now, ";

    /// <summary>
    /// True for a feature that needs a heap: one that comes with the heap because it makes objects, or text, while
    /// the program runs, or with a later piece of M3 built on objects (stored strings, generics over classes). Generic
    /// methods and types, char, references to text and enum names (printed from fixed text) came with M3 too, but need
    /// no heap.
    /// </summary>
    public bool NeedsHeap => (Arrival == WithTheHeap || ComesWithLaterClasses)
        && !new[] { GenericMethods, GenericTypes, Characters, TextReferences, EnumNames }.Any(
            feature => What.StartsWith(feature.What, StringComparison.Ordinal));

    /// <summary>
    /// <paramref name="feature"/>, one that <see cref="NeedsHeap"/>, on a target without a heap: "creating objects (new
    /// ...) needs a heap, and the target 'tiny' (2 KB RAM) has no heap. Use structs and static methods."
    /// </summary>
    /// <param name="feature">The feature, as a check without a target reports it.</param>
    /// <param name="target">The target's name.</param>
    /// <param name="ram">The size of the target's RAM, when its file gives one.</param>
    public static Unsupported WithoutHeap(Unsupported feature, string target, ulong? ram)
    {
        var size = ram is { } bytes ? $" ({MemorySize.Text(bytes)} RAM)" : "";
        return Rule($"{feature.What} {(feature.IsPlural ? "need" : "needs")} a heap, and the target '{target}'{size} " +
            "has no heap", ForGood(feature.Hint));
    }

    /// <summary>A call to Console.Write or Console.WriteLine on a target without a console.</summary>
    public static Unsupported NoConsole(string target) => Rule(
        $"Console.Write and Console.WriteLine need a console, and the target '{target}' has none",
        "Show what the program does another way, e.g. on a pin with ChipSharp.Hardware.Pin.");

    /// <summary>A hint for now as advice for good: "For now, use structs and static methods." is "Use structs and static methods."</summary>
    private static string ForGood(string hint) => hint.StartsWith(ForNow, StringComparison.Ordinal)
        ? char.ToUpperInvariant(hint[ForNow.Length]) + hint[(ForNow.Length + 1)..]
        : hint;
}
