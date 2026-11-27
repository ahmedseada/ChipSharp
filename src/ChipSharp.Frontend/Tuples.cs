using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// Tuples: C#'s <c>(1, "a")</c> is a <c>System.ValueTuple&lt;...&gt;</c>, whose substitute is ChipSharp.Library's
/// (src/ChipSharp.Library/ValueTuple.cs), a generic struct like any other: its elements are public fields, which C# reads
/// and writes itself (named elements, deconstruction, <c>==</c> element by element, a <c>switch</c> on a tuple), so a
/// tuple needs no heap. Like the collections' structs, a tuple may be boxed (<c>Console.WriteLine(tuple)</c>), and its
/// <c>ToString</c>, <c>Equals</c>, <c>GetHashCode</c> and <c>CompareTo</c> are run on it as on a struct of the program.
/// </summary>
internal static class Tuples
{
    /// <summary>The namespace of ChipSharp.Library's tuples, with the dot after it.</summary>
    private const string Namespace = "ChipSharp.Library.Tuples.";

    /// <summary>True for ChipSharp.Library's ValueTuple, or an instance of it.</summary>
    public static bool IsTuple(IlTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return (type.GenericDefinition ?? type).FullName.StartsWith(Namespace, StringComparison.Ordinal);
    }
}
