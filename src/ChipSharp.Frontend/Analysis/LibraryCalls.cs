namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The library methods a program can call, as a message lists them after a call to one that is not there: a few short
/// lines by type (<see cref="Summary"/>), or every method with its parameters (<see cref="Details"/>). Both come from
/// <see cref="KnownExternals"/>, so a method added there shows up here.
/// </summary>
internal static class LibraryCalls
{
    /// <summary>
    /// The library methods by type, the .NET ones with their names and ChipSharp's own by type only:
    /// <code>
    /// ChipSharp can call these library methods (--details lists each one with its parameters):
    ///   Console.WriteLine of (nothing), int, uint, long, ulong, bool, float, double
    ///   Math and MathF: Abs, Min, Max, Sqrt
    ///   ChipSharp.Hardware: Pin, Timing, Registers (see the README)
    /// </code>
    /// </summary>
    public static string Summary { get; } = Summarize(KnownExternals.All.Select(function => function.LibraryMethod));

    /// <summary>
    /// Every library method with its parameters, one line per type:
    /// <code>
    /// ChipSharp can call these library methods:
    ///   System.Console: WriteLine(), WriteLine(int), ...
    /// </code>
    /// </summary>
    public static string Details { get; } = List(KnownExternals.All.Select(function => function.LibraryMethod));

    /// <summary>
    /// The lines of <see cref="Summary"/> for <paramref name="methods"/>, e.g. "System.Console.WriteLine(int)".
    /// </summary>
    internal static string Summarize(IEnumerable<string> methods)
    {
        var lines = new List<string>
        {
            "ChipSharp can call these library methods (--details lists each one with its parameters):",
        };
        foreach (var space in methods.Select(Parse).GroupBy(method => method.Namespace))
        {
            if (space.Key.StartsWith("ChipSharp", StringComparison.Ordinal))
            {
                // ChipSharp's own libraries: the README shows how to use them
                var types = string.Join(", ", space.Select(method => method.Type).Distinct());
                lines.Add($"  {space.Key}: {types} (see the README)");
                continue;
            }

            // types with the same methods share a line: "Math and MathF: Abs, Min, Max, Sqrt"
            var sameNames = space.GroupBy(method => method.Type)
                .GroupBy(type => string.Join(", ", type.Select(method => method.Name).Distinct()));
            foreach (var same in sameNames)
            {
                var names = And([.. same.Select(type => type.Key)]);
                var overloads = same.First().ToList();
                lines.Add(same.Count() == 1 && overloads.All(method => method.Name == overloads[0].Name)
                    ? $"  {names}.{overloads[0].Name} of {string.Join(", ", overloads.Select(Parameters))}"
                    : $"  {names}: {same.Key}");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>The lines of <see cref="Details"/> for <paramref name="methods"/>.</summary>
    internal static string List(IEnumerable<string> methods) => string.Join("\n",
        [
            "ChipSharp can call these library methods:",
            .. methods.Select(Parse).GroupBy(method => method.FullType).Select(type =>
                $"  {type.Key}: {string.Join(", ", type.Select(method => $"{method.Name}({method.Parameters})"))}"),
        ]);

    /// <summary>"int", "(nothing)" for no parameters, "(int, bool)" for several.</summary>
    private static string Parameters(Method method) => method.Parameters.Length == 0 ? "(nothing)"
        : method.Parameters.Contains(',', StringComparison.Ordinal) ? $"({method.Parameters})"
        : method.Parameters;

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string And(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";

    /// <summary>"System.Console.WriteLine(int)" as its namespace, type, name and parameters.</summary>
    private static Method Parse(string method)
    {
        var open = method.IndexOf('(', StringComparison.Ordinal);
        var qualified = open < 0 ? method : method[..open];
        var parameters = open < 0 ? "" : method[(open + 1)..].TrimEnd(')');
        var dot = LastDot(qualified);
        var fullType = dot < 0 ? "" : qualified[..dot];
        var name = qualified[(dot + 1)..];
        var typeDot = LastDot(fullType);
        return new Method(typeDot < 0 ? "" : fullType[..typeDot], fullType[(typeDot + 1)..], fullType, name, parameters);
    }

    /// <summary>The last '.' of <paramref name="name"/> outside a generic's type arguments, or -1.</summary>
    private static int LastDot(string name)
    {
        var depth = 0;
        for (var i = name.Length - 1; i >= 0; i--)
        {
            switch (name[i])
            {
                case '>':
                    depth++;
                    break;
                case '<':
                    depth--;
                    break;
                case '.' when depth == 0:
                    return i;
            }
        }

        return -1;
    }

    private sealed record Method(string Namespace, string Type, string FullType, string Name, string Parameters);
}
