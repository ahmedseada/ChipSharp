using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// What the whole-program pass found in an assembly: the methods of the program, and every place they use C# that
/// <see cref="SupportedSubset"/> does not have yet, one by one (<see cref="Problems"/>) and by feature
/// (<see cref="Groups"/>). <see cref="Summary"/> and <see cref="Details"/> are the two ways <c>chipsharp check</c>
/// reports them.
/// </summary>
public sealed class AnalysisResult
{
    private static readonly string SupportedLibraryCalls =
        $"{SupportedSubset.Milestone} supports these library calls: " +
        $"{string.Join(", ", KnownExternals.All.Select(function => function.LibraryMethod))}.";

    internal AnalysisResult(IlAssembly assembly, IReadOnlyList<IlMethod> methods, IReadOnlyList<Problem> problems)
    {
        Assembly = assembly;
        Methods = methods;
        Problems = problems;
        Groups = [.. problems.GroupBy(problem => problem.Unsupported).Select(group => new ProblemGroup([.. group]))];
    }

    public IlAssembly Assembly { get; }

    /// <summary>
    /// Every method of the program the entry point can reach, itself included: the entry point first, then in the
    /// order they are first called. Methods nothing calls are left out, and not checked.
    /// </summary>
    public IReadOnlyList<IlMethod> Methods { get; }

    /// <summary>Every problem, one per place: method by method in the order of <see cref="Methods"/>.</summary>
    public IReadOnlyList<Problem> Problems { get; }

    /// <summary>The problems by feature, in the order each feature first appears in <see cref="Problems"/>.</summary>
    public IReadOnlyList<ProblemGroup> Groups { get; }

    /// <summary>True when the milestone compiles the program: there are no problems.</summary>
    public bool IsSupported => Problems.Count == 0;

    /// <summary>
    /// The problems by feature, one line each, then the library calls there are (when a library call is a problem)
    /// and how to see every place:
    /// <code>
    /// Demo.dll uses 2 C# features that M0 does not compile yet (4 places):
    ///   the library method System.Math.Abs(int) is not available in M0: 1 place in Program.Main() (IL_000e).
    ///   floating point (float/double) arrives after M0: 3 places in Program.Half(int) (return type, IL_0001 and IL_0002). For now, ...
    /// M0 supports these library calls: System.Console.WriteLine(), ...
    /// For every place, run: chipsharp check Demo.dll --details
    /// </code>
    /// Empty when the program has no problems.
    /// </summary>
    public string Summary => IsSupported ? "" : Message(
        Groups.Select(group => group.Line),
        $"For every place, run: chipsharp check {Quoted(Assembly.FilePath)} --details");

    /// <summary>
    /// The heading of <see cref="Summary"/>, then every problem on a line of its own (<see cref="Problem.Line"/>), then the
    /// library calls there are when a library call is a problem. Empty when the program has no problems.
    /// </summary>
    public string Details => IsSupported ? "" : Message(Problems.Select(problem => problem.Line), null);

    /// <summary>The program, for the IL-to-TIR translation.</summary>
    /// <exception cref="ChipSharpException">The program has problems; the message is the <see cref="Summary"/>.</exception>
    public IlProgram ToProgram() => IsSupported ? new IlProgram(Assembly, Methods) : throw new ChipSharpException(Summary);

    /// <summary>The heading, the lines indented, the library calls there are if they matter, and <paramref name="last"/>.</summary>
    private string Message(IEnumerable<string> lines, string? last)
    {
        var features = ProblemGroup.Count(Groups.Count, "C# feature");
        var places = ProblemGroup.Count(Problems.Count, "place");
        var message = new List<string>
        {
            $"{Assembly.FilePath} uses {features} that {SupportedSubset.Milestone} does not compile yet ({places}):",
        };
        message.AddRange(lines.Select(line => $"  {line}"));
        if (Groups.Any(group => group.IsLibraryCall))
        {
            message.Add(SupportedLibraryCalls);
        }

        if (last is not null)
        {
            message.Add(last);
        }

        return string.Join("\n", message);
    }

    /// <summary>The path as a command line takes it: in quotes when it has a space.</summary>
    private static string Quoted(string path) => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
