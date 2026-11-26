using System.Globalization;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Every place the program uses one feature that <see cref="SupportedSubset"/> does not have yet, e.g. arrays, or one
/// library method. <see cref="Line"/> is the group as the summary of <c>chipsharp check</c> lists it: the feature, how
/// many places, the methods with a few of their places each, and the hint, once.
/// </summary>
public sealed class ProblemGroup
{
    /// <summary>How many methods, and places of a method, a summary line names before "and N more".</summary>
    internal const int Shown = 3;

    internal ProblemGroup(IReadOnlyList<Problem> problems)
    {
        Problems = problems;
        Methods = [.. problems.Select(problem => problem.Method).Distinct()];
        var unsupported = problems[0].Unsupported;
        var where = $"{Count(problems.Count, "place")} in {Where()}";
        Line = unsupported.Hint.Length == 0
            ? $"{unsupported.Heading}: {where}."
            : $"{unsupported.Heading}: {where}. {unsupported.Hint}";
    }

    /// <summary>What is not supported, as C# programmers know it: "arrays", "the library method System.Math.Abs(int)".</summary>
    public string Feature => Problems[0].Feature;

    /// <summary>True for the calls to one library method M0 does not have.</summary>
    public bool IsLibraryCall => Problems[0].IsLibraryCall;

    /// <summary>The places, in the order of the detailed list: method by method in the program's order.</summary>
    public IReadOnlyList<Problem> Problems { get; }

    /// <summary>The methods the places are in, each once, in the program's order.</summary>
    public IReadOnlyList<IlMethod> Methods { get; }

    /// <summary>
    /// E.g. "arrays arrive after M0: 6 places in the top-level statements (IL_0001, IL_000d, IL_0015 and 3 more).
    /// For now, use separate variables."
    /// </summary>
    public string Line { get; }

    public override string ToString() => Line;

    /// <summary>"1 place", "3 places", "2 C# features".</summary>
    internal static string Count(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");

    /// <summary>"a", "a and b", "a, b and c"; past <see cref="Shown"/> + 1 items "a, b, c and 5 more" (and the noun).</summary>
    internal static string List(IReadOnlyList<string> items, string more = "")
    {
        if (items.Count > Shown + 1)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{string.Join(", ", items.Take(Shown))} and {items.Count - Shown} more{more}");
        }

        return items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";
    }

    /// <summary>
    /// The methods by their C# names, each with its places: the signature, then the code (IL offsets), then the locals.
    /// "the top-level statements (IL_0001, IL_000d, IL_0015 and 5 more) and local function Logit (return type)".
    /// </summary>
    private string Where()
    {
        var methods = Problems.GroupBy(problem => problem.Method).Select(method =>
        {
            var places = method.OrderBy(problem => problem.Kind).Select(problem => problem.Place)
                .Where(place => place.Length > 0).ToList();
            var name = CSharpNames.Friendly(method.Key);
            return places.Count == 0 ? name : $"{name} ({List(places)})";
        });
        return List([.. methods], " methods");
    }
}
