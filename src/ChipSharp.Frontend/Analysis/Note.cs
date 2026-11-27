using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// How ChipSharp's text differs from what .NET does by default: what the program calls (<see cref="Api"/>), what .NET
/// does and what ChipSharp does (<see cref="Heading"/>), and what to write for the same result everywhere
/// (<see cref="Hint"/>, empty when there is nothing to write instead). The notes of a check that have equal
/// differences are one group in its summary.
/// </summary>
/// <param name="Api">What the program calls, as C# names it, e.g. "string.CompareTo".</param>
/// <param name="Heading">
/// The difference, as a clause: "string.CompareTo compares by the PC's language settings in .NET; ChipSharp compares
/// character codes (like .NET's invariant mode)".
/// </param>
/// <param name="Hint">A sentence, e.g. "Use string.CompareOrdinal to get the same result everywhere."; may be empty.</param>
internal sealed record Difference(string Api, string Heading, string Hint)
{
    /// <summary>The sentence a note line ends with: the difference, then the hint.</summary>
    public string Message => Hint.Length == 0 ? $"{Heading}." : $"{Heading}. {Hint}";
}

/// <summary>
/// One place where the program uses something whose result in .NET depends on the PC's language settings (the
/// culture), where ChipSharp's is the same everywhere (see <see cref="CultureNotes"/>). A note is no problem: the
/// program compiles. <see cref="Line"/> is the note as <c>chipsharp check --details</c> lists it.
/// </summary>
public sealed class Note
{
    internal Note(IlMethod method, IlInstruction instruction, Difference difference)
    {
        Method = method;
        Offset = instruction.Offset;
        Place = IlPrinter.Label(instruction.Offset);
        Difference = difference;
        Line = $"{CSharpNames.Method(method)} at {Place} ({instruction.Name}): {difference.Message}";
    }

    /// <summary>The method the place is in.</summary>
    public IlMethod Method { get; }

    /// <summary>The IL offset of the instruction.</summary>
    public int Offset { get; }

    /// <summary>The place as a summary lists it, e.g. "IL_0007".</summary>
    public string Place { get; }

    /// <summary>What the program calls there, as C# names it, e.g. "string.CompareTo".</summary>
    public string Api => Difference.Api;

    /// <summary>
    /// The method, the place and the message, e.g. "Program.Main() at IL_0007 (callvirt): string.CompareTo compares by
    /// the PC's language settings in .NET; ChipSharp compares character codes (like .NET's invariant mode). Use
    /// string.CompareOrdinal to get the same result everywhere."
    /// </summary>
    public string Line { get; }

    internal Difference Difference { get; }

    public override string ToString() => Line;
}

/// <summary>
/// Every note of one difference (see <see cref="Note"/>). <see cref="Line"/> is the group as the summary lists it: the
/// difference, how many places and where (the methods with a few of their places each), and the hint, once.
/// </summary>
public sealed class NoteGroup
{
    internal NoteGroup(IReadOnlyList<Note> notes)
    {
        Notes = notes;
        var difference = notes[0].Difference;
        var methods = notes.GroupBy(note => note.Method).Select(method =>
            $"{CSharpNames.Friendly(method.Key)} ({ProblemGroup.List([.. method.Select(note => note.Place)])})");
        var where = $"{ProblemGroup.Count(notes.Count, "place")} in {ProblemGroup.List([.. methods], " methods")}";
        Line = difference.Hint.Length == 0
            ? $"{difference.Heading}: {where}."
            : $"{difference.Heading}: {where}. {difference.Hint}";
    }

    /// <summary>What the program calls, as C# names it, e.g. "string.CompareTo".</summary>
    public string Api => Notes[0].Api;

    /// <summary>The places, method by method in the program's order.</summary>
    public IReadOnlyList<Note> Notes { get; }

    /// <summary>
    /// E.g. "string.CompareTo compares by the PC's language settings in .NET; ChipSharp compares character codes (like
    /// .NET's invariant mode): 2 places in Program.Main() (IL_0007 and IL_0015). Use string.CompareOrdinal to get the
    /// same result everywhere."
    /// </summary>
    public string Line { get; }

    public override string ToString() => Line;
}
