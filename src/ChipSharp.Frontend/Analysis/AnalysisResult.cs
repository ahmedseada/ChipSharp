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
    /// order they are first called. Methods nothing calls are left out, and not checked. The initializers (.cctor) of
    /// the types the program uses are in it too (see <see cref="IlProgram.TypeInitializers"/>), and so are the
    /// interrupt handlers, after the entry point, and what they reach.
    /// </summary>
    public IReadOnlyList<IlMethod> Methods { get; }

    /// <summary>The methods marked <c>[Interrupt(...)]</c> that handle an event (see <see cref="IlProgram.InterruptHandlers"/>).</summary>
    public IReadOnlyList<InterruptHandler> InterruptHandlers { get; internal init; } = [];

    /// <summary>
    /// Every problem, one per place: method by method in the order of <see cref="Methods"/>, then the calls of Console
    /// on a target without a console (see <see cref="TargetRules"/>), then the places that break a rule of interrupt
    /// handlers.
    /// </summary>
    public IReadOnlyList<Problem> Problems { get; }

    /// <summary>
    /// The target the details must be asked for, when its rules (<see cref="TargetRules"/>) found problems: the command
    /// the <see cref="Summary"/> ends with names it (-t), so that the details say what the summary says.
    /// </summary>
    internal string? DetailsTarget { get; init; }

    /// <summary>Whether the target reads values at any address (see <see cref="IlProgram.UnalignedAccess"/>).</summary>
    internal bool UnalignedAccess { get; init; } = true;

    /// <summary>The problems by feature, in the order each feature first appears in <see cref="Problems"/>.</summary>
    public IReadOnlyList<ProblemGroup> Groups { get; }

    /// <summary>
    /// Every place whose result in .NET depends on the PC's language settings, where ChipSharp's is the same everywhere
    /// (see <see cref="CultureNotes"/>), method by method in the order of <see cref="Methods"/>. Notes are no problems:
    /// a program with notes compiles.
    /// </summary>
    public IReadOnlyList<Note> Notes { get; internal init; } = [];

    /// <summary>The notes by difference (one for each library method), in the order each first appears in <see cref="Notes"/>.</summary>
    public IReadOnlyList<NoteGroup> NoteGroups =>
        [.. Notes.GroupBy(note => note.Difference).Select(group => new NoteGroup([.. group]))];

    /// <summary>True when ChipSharp compiles the program: there are no problems.</summary>
    public bool IsSupported => Problems.Count == 0;

    /// <summary>
    /// The problems by feature, one line each, then the library calls there are by type (when a library call is a
    /// problem) and how to see every place:
    /// <code>
    /// Demo.dll uses 2 C# features that ChipSharp does not compile yet (3 places):
    ///   the library method System.Math.Abs(int) is not available yet: 1 place in Program.Main() (IL_000e).
    ///   checked arithmetic arrives later: 2 places in Program.Main() (IL_0001 and IL_0014). For now, ...
    /// ChipSharp can call these library methods (--details lists each one with its parameters):
    ///   Console.Write and Console.WriteLine of (nothing), text, int, uint, long, ulong, bool, float, double
    ///   ...
    /// For every place, run: chipsharp check Demo.dll --details
    /// </code>
    /// Empty when the program has no problems.
    /// </summary>
    public string Summary => IsSupported ? "" : Message(
        Groups.Select(group => group.Line),
        LibraryCalls.Summary,
        $"For every place, run: chipsharp check {Quoted(Assembly.FilePath)}" +
        $"{(DetailsTarget is null ? "" : $" -t {Quoted(DetailsTarget)}")} --details");

    /// <summary>
    /// The heading of <see cref="Summary"/>, then every problem on a line of its own (<see cref="Problem.Line"/>), then
    /// every library method there is, with its parameters, when a library call is a problem. Empty when the program has
    /// no problems.
    /// </summary>
    public string Details =>
        IsSupported ? "" : Message(Problems.Select(problem => problem.Line), LibraryCalls.Details, null);

    /// <summary>The program, for the IL-to-TIR translation.</summary>
    /// <exception cref="ChipSharpException">The program has problems; the message is the <see cref="Summary"/>.</exception>
    public IlProgram ToProgram() => IsSupported
        ? new IlProgram(Assembly, Methods) { InterruptHandlers = InterruptHandlers, UnalignedAccess = UnalignedAccess }
        : throw new ChipSharpException(Summary);

    /// <summary>
    /// The heading, the lines indented, <paramref name="libraryCalls"/> if they matter, and <paramref name="last"/>.
    /// </summary>
    private string Message(IEnumerable<string> lines, string libraryCalls, string? last)
    {
        var features = ProblemGroup.Count(Groups.Count, "C# feature");
        var places = ProblemGroup.Count(Problems.Count, "place");
        var message = new List<string>
        {
            $"{Assembly.FilePath} uses {features} that ChipSharp does not compile yet ({places}):",
        };
        message.AddRange(lines.Select(line => $"  {line}"));
        if (Groups.Any(group => group.IsLibraryCall))
        {
            message.Add(libraryCalls);
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
