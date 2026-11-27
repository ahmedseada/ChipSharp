namespace ChipSharp.Frontend.Analysis;

/// <summary>Strings (see <see cref="Strings"/> and <see cref="StringMethods"/>): what needs the heap, and what is not supported yet.</summary>
internal sealed partial record Unsupported
{
    /// <summary>A method of string (or of char, a span of chars) that makes a new string or array: a heap use.</summary>
    public static readonly Unsupported MakingText = Heap(
        "making new text (Substring, Replace, Trim, ToUpper, Split, AsSpan and the other methods of string that give a new string)",
        false, "For now, print the parts one after another with Console.Write.");

    /// <summary>An enum name with an alignment, which may need a string to measure: a heap use.</summary>
    public static readonly Unsupported AlignedEnumNames = Heap("aligning an enum name ($\"{color,8}\")", false,
        "For now, leave out the alignment.");
}
