namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The library methods a program can call, as a message lists them after a call to one that is not there: a few short
/// lines by type (<see cref="Summary"/>), or every method with its parameters (<see cref="Details"/>). Both come from
/// <see cref="KnownExternals"/> and the library substitutes (<see cref="LibrarySubstitutes.Members"/>), so a method
/// added there shows up here.
/// </summary>
internal static class LibraryCalls
{
    /// <summary>Every library method a program can call: the .NET ones first, then ChipSharp's own.</summary>
    private static readonly string[] Known =
    [
        .. KnownExternals.All.Select(function => function.LibraryMethod).Concat(LibrarySubstitutes.Members)
            .OrderBy(method => method.StartsWith("ChipSharp.", StringComparison.Ordinal)),
    ];

    /// <summary>
    /// The library methods by type, the .NET ones with their names and ChipSharp's own by type only:
    /// <code>
    /// ChipSharp can call these library methods (--details lists each one with its parameters):
    ///   Console.WriteLine of (nothing), int, uint, long, ulong, bool, float, double
    ///   Math and MathF: Abs, Min, Max, Sqrt
    ///   TimeSpan: FromTicks, FromDays, ..., Equals, its constructors, properties and operators
    ///   ChipSharp.Hardware: Pin, Timing, Registers (see the README)
    /// </code>
    /// </summary>
    public static string Summary { get; } = Summarize(Known);

    /// <summary>
    /// Every library method with its parameters, one line per type:
    /// <code>
    /// ChipSharp can call these library methods:
    ///   System.Console: WriteLine(), WriteLine(int), ...
    /// </code>
    /// </summary>
    public static string Details { get; } = List(Known);

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
            var sameNames = space.GroupBy(method => method.Type).GroupBy(type => Names(type));
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

    /// <summary>
    /// The lines of <see cref="Details"/> for <paramref name="methods"/>; a property (written without parentheses) by
    /// its name alone.
    /// </summary>
    internal static string List(IEnumerable<string> methods) => string.Join("\n",
        [
            "ChipSharp can call these library methods:",
            .. methods.Select(Parse).GroupBy(method => method.FullType).Select(type =>
                $"  {type.Key}: {string.Join(", ", type.Select(method =>
                    method.IsProperty ? method.Name : $"{method.Name}({method.Parameters})"))}"),
        ]);

    /// <summary>"int", "(nothing)" for no parameters, "(int, bool)" for several.</summary>
    private static string Parameters(Method method) => method.Parameters.Length == 0 ? "(nothing)"
        : method.Parameters.Contains(',', StringComparison.Ordinal) ? $"({method.Parameters})"
        : method.Parameters;

    /// <summary>
    /// The names of a type's methods, each once, and its constructors, properties (and fields) and operators in a few
    /// words: "FromSeconds, Add, its constructors, properties and operators".
    /// </summary>
    private static string Names(IEnumerable<Method> methods)
    {
        var list = methods.ToList();
        bool IsConstructor(Method method) => method.Name.StartsWith("new ", StringComparison.Ordinal);
        bool IsOperator(Method method) => method.Name.StartsWith("operator ", StringComparison.Ordinal);

        var kinds = new List<string>();
        if (list.Any(IsConstructor))
        {
            kinds.Add("constructors");
        }

        if (list.Any(method => method.IsProperty))
        {
            kinds.Add("properties");
        }

        if (list.Any(IsOperator))
        {
            kinds.Add("operators");
        }

        var names = list.Where(method => !IsConstructor(method) && !method.IsProperty && !IsOperator(method))
            .Select(method => method.Name).Distinct();
        return string.Join(", ", kinds.Count == 0 ? names : names.Append($"its {And(kinds)}"));
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string And(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";

    /// <summary>
    /// "System.Console.WriteLine(int)" as its namespace, type, name and parameters; also a property without parentheses
    /// ("System.TimeSpan.Ticks") and an operator ("System.TimeSpan.operator &lt;(System.TimeSpan, System.TimeSpan)").
    /// </summary>
    private static Method Parse(string method)
    {
        var open = method.IndexOf('(', StringComparison.Ordinal);
        var qualified = open < 0 ? method : method[..open];
        var parameters = open < 0 ? "" : method[(open + 1)..].TrimEnd(')');
        var dot = qualified.IndexOf(".operator ", StringComparison.Ordinal) is >= 0 and var op ? op : LastDot(qualified);
        var fullType = dot < 0 ? "" : qualified[..dot];
        var name = qualified[(dot + 1)..];
        var typeDot = LastDot(fullType);
        return new Method(typeDot < 0 ? "" : fullType[..typeDot], fullType[(typeDot + 1)..], fullType, name, parameters,
            IsProperty: open < 0);
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

    private sealed record Method(string Namespace, string Type, string FullType, string Name, string Parameters,
        bool IsProperty);
}
