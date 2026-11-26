namespace ChipSharp.Frontend.Analysis;

/// <summary>What printed text (<see cref="PrintedText"/>) cannot do yet, in C# words.</summary>
internal sealed partial record Unsupported
{
    /// <summary>Text built and then kept: in a variable or a field, returned, passed on, measured, or chosen where ways join.</summary>
    public static readonly Unsupported KeepingBuiltText = Heap("keeping built text in a variable", false,
        "For now, print it straight away: Console.WriteLine($\"...\").");

    /// <summary>An enum in printed text, which .NET prints by its name: <c>$"{color}"</c>, <c>color.ToString()</c>.</summary>
    public static readonly Unsupported EnumNames = Heap("enum names", true,
        "For now, print the number: Console.WriteLine($\"{(int)color}\").");

    /// <summary>A float or a double with an alignment (<c>$"{x,8}"</c>): its width is known only once it is written.</summary>
    public static readonly Unsupported AlignedFloats = new("aligning a float or a double ($\"{x,8}\")", false,
        "For now, leave out the alignment of floats and doubles, or print them on their own.");

    /// <summary>
    /// A format ChipSharp does not write exactly as .NET does (<c>$"{x:F2}"</c>, <c>x.ToString("N0")</c>), so two formats
    /// are two features: "the format "F2" in printed text".
    /// </summary>
    public static Unsupported Format(string format) => new($"the format \"{format}\" in printed text", false,
        "For now, write whole numbers with D, D<n>, X or X<n> (x for lowercase hex digits), or leave the format out.");

    /// <summary>A nint or nuint in printed text, which Console.Write does not print either.</summary>
    public static readonly Unsupported NativeIntText = new("the text of a nint or nuint ($\"{size}\")", false,
        "For now, convert it: $\"{(long)size}\".");

    /// <summary>
    /// The text of a value that is not a number, a bool or text (a struct, a TimeSpan, ...), which .NET makes with its
    /// ToString: "the text of a Point ($"{value}", value.ToString())".
    /// </summary>
    public static Unsupported TextOf(string type) => Heap($"the text of a {type} ($\"{{value}}\", value.ToString())",
        false, "For now, print the numbers, bools and text it holds one after another.");
}
