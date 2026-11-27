using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// The notes of the whole-program pass: each place where a program calls a library method whose result in .NET depends
/// on the PC's language settings (comparing, searching and casing text, reading numbers, writing floats) gets a note
/// that says what ChipSharp does and what to write for the same result everywhere; the ordinal and invariant ways get
/// none. A note is no problem: the program compiles, and <c>chipsharp build</c> and <c>check</c> print it (on standard
/// error, "note: ..."), unless told <c>--quiet-notes</c>.
/// </summary>
public sealed class CultureNoteTests : IDisposable
{
    private const string Compares = "compares by the PC's language settings in .NET; ChipSharp compares character codes " +
        "(like .NET's invariant mode)";

    private const string FloatText = "a float or double written as text (Console.Write(x), $\"{x}\", \"a\" + x, " +
        "x.ToString()) depends on the PC's language settings in .NET (1,5 with German settings); ChipSharp writes it as " +
        ".NET's invariant culture does (1.5)";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>The method each case calls, as the note names it, and what the note says.</summary>
    public static TheoryData<string, string, string> CultureSensitive => new()
    {
        { "a.CompareTo(b)", "string.CompareTo", $"string.CompareTo {Compares}. Use string.CompareOrdinal to get the same result everywhere." },
        { "string.Compare(a, b)", "string.Compare", $"string.Compare {Compares}. Use string.CompareOrdinal or StringComparison.Ordinal to get the same result everywhere." },
        { "a.StartsWith(b)", "string.StartsWith", $"string.StartsWith {Compares}. Pass StringComparison.Ordinal to get the same result everywhere." },
        { "a.EndsWith(b)", "string.EndsWith", $"string.EndsWith {Compares}. Pass StringComparison.Ordinal to get the same result everywhere." },
        { "a.IndexOf(b)", "string.IndexOf", $"string.IndexOf {Compares}. Pass StringComparison.Ordinal to get the same result everywhere." },
        { "a.IndexOf(b, 1)", "string.IndexOf", $"string.IndexOf {Compares}. Pass StringComparison.Ordinal to get the same result everywhere." },
        { "a.LastIndexOf(b)", "string.LastIndexOf", $"string.LastIndexOf {Compares}. Pass StringComparison.Ordinal to get the same result everywhere." },
        {
            "a.StartsWith(b, StringComparison.CurrentCulture)", "string.StartsWith",
            "string.StartsWith with StringComparison.CurrentCulture compares by the PC's language settings in .NET; " +
            "ChipSharp compares character codes (like .NET's invariant mode). Pass StringComparison.Ordinal or " +
            "StringComparison.OrdinalIgnoreCase to get the same result everywhere."
        },
        {
            "string.Compare(a, b, StringComparison.InvariantCultureIgnoreCase)", "string.Compare",
            "string.Compare with StringComparison.InvariantCultureIgnoreCase compares by the invariant culture's rules " +
            "in .NET; ChipSharp compares character codes, ignoring case, (like .NET's invariant mode). Pass " +
            "StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase to get the same result everywhere."
        },
        {
            "a.Equals(b, (StringComparison)a.Length)", "string.Equals",
            "string.Equals with a StringComparison that is not Ordinal or OrdinalIgnoreCase compares by a culture's " +
            "rules in .NET; ChipSharp compares character codes (like .NET's invariant mode). Pass " +
            "StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase to get the same result everywhere."
        },
        {
            "a.ToUpper()", "string.ToUpper",
            "string.ToUpper changes case by the PC's language settings in .NET (with Turkish settings \"i\" becomes " +
            "\"İ\"); ChipSharp changes it as .NET's invariant culture does. Use ToUpperInvariant() to get the same " +
            "result everywhere."
        },
        {
            "a.ToLower()", "string.ToLower",
            "string.ToLower changes case by the PC's language settings in .NET (with Turkish settings \"i\" becomes " +
            "\"İ\"); ChipSharp changes it as .NET's invariant culture does. Use ToLowerInvariant() to get the same " +
            "result everywhere."
        },
        {
            "char.ToUpper(a[0])", "char.ToUpper",
            "char.ToUpper changes case by the PC's language settings in .NET (with Turkish settings \"i\" becomes " +
            "\"İ\"); ChipSharp changes it as .NET's invariant culture does. Use char.ToUpperInvariant to get the same " +
            "result everywhere."
        },
        {
            "char.ToLower(a[0])", "char.ToLower",
            "char.ToLower changes case by the PC's language settings in .NET (with Turkish settings \"i\" becomes " +
            "\"İ\"); ChipSharp changes it as .NET's invariant culture does. Use char.ToLowerInvariant to get the same " +
            "result everywhere."
        },
        {
            "int.Parse(a)", "int.Parse",
            "int.Parse reads the number by the PC's language settings in .NET (its minus and plus signs); ChipSharp " +
            "reads it as .NET's invariant culture does (- and +)."
        },
        {
            "ulong.TryParse(a, out var n)", "ulong.TryParse",
            "ulong.TryParse reads the number by the PC's language settings in .NET (its minus and plus signs); " +
            "ChipSharp reads it as .NET's invariant culture does (- and +)."
        },
        { "a.Length * 1.5", "a float or double as text", $"{FloatText}." },
        { "a.Length * 0.5f", "a float or double as text", $"{FloatText}." },
        { "$\"{a.Length / 3.0} s\"", "a float or double as text", $"{FloatText}." },
        { "\"took \" + a.Length * 1.5f", "a float or double as text", $"{FloatText}." },
        { "(a.Length * 2.5).ToString()", "a float or double as text", $"{FloatText}." },
        { "string.Format(\"{0} m\", a.Length * 0.25)", "a float or double as text", $"{FloatText}." },
        { "new System.Text.StringBuilder().Append(a.Length * 0.5)", "a float or double as text", $"{FloatText}." },
        { "new System.Text.StringBuilder().Append($\"{a.Length * 0.5f} V\")", "a float or double as text", $"{FloatText}." },
    };

    /// <summary>The ways that give the same result everywhere.</summary>
    public static TheoryData<string> Invariant => new()
    {
        "string.CompareOrdinal(a, b)",
        "string.Compare(a, b, StringComparison.Ordinal)",
        "string.Compare(a, b, StringComparison.OrdinalIgnoreCase)",
        "a.StartsWith(b, StringComparison.Ordinal)",
        "a.EndsWith(b, StringComparison.OrdinalIgnoreCase)",
        "a.IndexOf(b, StringComparison.Ordinal)",
        "a.IndexOf(b, 1, StringComparison.OrdinalIgnoreCase)",
        "a.LastIndexOf(b, StringComparison.Ordinal)",
        "a.Contains(b, StringComparison.OrdinalIgnoreCase)",
        "a.Equals(b, StringComparison.Ordinal)",
        "string.Equals(a, b, StringComparison.OrdinalIgnoreCase)",
        "a.ToUpperInvariant()",
        "a.ToLowerInvariant()",
        "char.ToUpperInvariant(a[0])",
        "char.ToLowerInvariant(a[0])",
        "a.IndexOf('x')",
        "a.LastIndexOf('x')",
        "a.StartsWith('x')",
        "a.EndsWith('x')",
        "a.Contains(b)",
        "a.Replace(b, \"y\")",
        "a == b",
        "a.Equals(b)",
        "$\"{a.Length * 2} items\"",
        "a.Length * 2",
        "new System.Text.StringBuilder().Append(a.Length * 2).Append(a)",
    };

    [Theory]
    [MemberData(nameof(CultureSensitive))]
    public void A_method_that_depends_on_the_culture_in_dotnet_gets_a_note_where_it_is_called(string expression,
        string api, string message)
    {
        var analysis = WholeProgram.Check(Program(expression));

        Assert.True(analysis.IsSupported, analysis.Summary);
        var note = Assert.Single(analysis.Notes);
        Assert.Equal(api, note.Api);
        Assert.Equal("Program.Main()", CSharpNames.Method(note.Method));
        Assert.Matches(@"^Program\.Main\(\) at IL_[0-9a-f]{4} \([a-z.0-9]+\): ", note.Line);
        Assert.EndsWith($"): {message}", note.Line);
        Assert.Equal(message, note.Difference.Message);

        // the summary's line: the difference, where, then the hint
        var group = Assert.Single(analysis.NoteGroups);
        var (heading, hint) = (note.Difference.Heading, note.Difference.Hint);
        Assert.Equal($"{heading}: 1 place in Program.Main() ({note.Place}).{(hint.Length == 0 ? "" : " " + hint)}", group.Line);
    }

    [Theory]
    [MemberData(nameof(Invariant))]
    public void The_ordinal_and_invariant_ways_get_no_note(string expression)
    {
        var analysis = WholeProgram.Check(Program(expression));

        Assert.True(analysis.IsSupported, analysis.Summary);
        Assert.Empty(analysis.Notes);
        Assert.Empty(analysis.NoteGroups);
    }

    /// <summary>The summary's line of a note: the difference, every place, then the hint once.</summary>
    [Fact]
    public void The_notes_of_one_method_are_one_line_with_every_place_and_the_hint_once()
    {
        var analysis = WholeProgram.Check("""
            using System;

            class Program
            {
                static void Main()
                {
                    string a = Text(), b = Text();
                    Console.WriteLine(a.CompareTo(b));
                    Console.WriteLine(Order(a, b));
                    Console.WriteLine(b.CompareTo(a) + string.CompareOrdinal(a, b));
                }

                static int Order(string x, string y) => y.CompareTo(x);

                static string Text() => "a";
            }
            """);

        Assert.Equal(3, analysis.Notes.Count);
        var line = Assert.Single(analysis.NoteGroups).Line;
        Assert.Matches(@"^string\.CompareTo compares by the PC's language settings in \.NET; ChipSharp compares character " +
            @"codes \(like \.NET's invariant mode\): 3 places in Program\.Main\(\) \(IL_[0-9a-f]{4} and IL_[0-9a-f]{4}\) " +
            @"and Program\.Order\(string, string\) \(IL_0002\)\. Use string\.CompareOrdinal to get the same result " +
            @"everywhere\.$", line);
    }

    /// <summary>Generic methods of the program are its own too; ChipSharp's own C# for library types is not.</summary>
    [Fact]
    public void Generic_methods_have_notes_and_the_library_substitutes_have_none()
    {
        var analysis = WholeProgram.Check("""
            using System;
            using System.Collections.Generic;

            class Program
            {
                static void Main()
                {
                    var list = new List<string> { "b", "a" };
                    list.Add("c");
                    Console.WriteLine(First(list, "a"));
                    Console.WriteLine(TimeSpan.FromSeconds(2).TotalMilliseconds > 1);
                }

                static T First<T>(List<T> items, string name)
                {
                    Console.WriteLine(name.ToUpper());
                    return items[0];
                }
            }
            """);

        Assert.True(analysis.IsSupported, analysis.Summary);
        var note = Assert.Single(analysis.Notes);
        Assert.Equal("string.ToUpper", note.Api);
        Assert.StartsWith("Program.First", note.Line);
    }

    /// <summary>A note does not stop a build: build writes the program, and the note on standard error.</summary>
    [HostFact]
    public async Task Build_prints_the_notes_on_standard_error_and_writes_the_program()
    {
        var dll = CSharpCompiler.CompileToFile(Program("a.ToUpper() + b.ToLower() + a.CompareTo(b)"),
            Path.Combine(_scratch, "Notes.dll"));
        var output = Path.Combine(_scratch, "out");

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-o", output);

        Assert.Equal(0, exitCode);
        var exe = Path.Combine(output, "Notes.exe");
        Assert.Equal(exe, Build.BuildCliTests.Lines(printed)[0]);
        var notes = error.ReplaceLineEndings("\n").TrimEnd().Split('\n');
        Assert.Equal(3, notes.Length);
        Assert.All(notes, line => Assert.StartsWith("note: ", line));
        Assert.StartsWith("note: string.ToUpper changes case by the PC's language settings in .NET", notes[0]);
        Assert.StartsWith("note: string.ToLower changes case", notes[1]);
        Assert.StartsWith($"note: string.CompareTo {Compares}: 1 place in Program.Main() (IL_", notes[2]);
        var run = await HostRun.RunAsync(exe, [], _scratch);
        Assert.Equal((0, "Aa0\r\n"), (run.ExitCode, run.Text));
    }

    [HostFact]
    public async Task Build_with_quiet_notes_prints_no_notes()
    {
        var dll = CSharpCompiler.CompileToFile(Program("a.ToUpper()"), Path.Combine(_scratch, "Quiet.dll"));

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-o", Path.Combine(_scratch, "out"),
            "--quiet-notes");

        Assert.Equal(0, exitCode);
        Assert.Equal("", error);
        Assert.Equal(2, Build.BuildCliTests.Lines(printed).Length);
    }

    [Fact]
    public async Task Check_prints_the_notes_after_the_methods_and_with_details_every_place()
    {
        var dll = CSharpCompiler.CompileToFile(Program("a.StartsWith(b) || a.StartsWith(\"x\")"),
            Path.Combine(_scratch, "Check.dll"));

        var summary = await ChipSharpCli.RunAsync("check", dll);
        var details = await ChipSharpCli.RunAsync("check", dll, "--details");
        var quiet = await ChipSharpCli.RunAsync("check", dll, "--quiet-notes");

        Assert.Equal(0, summary.ExitCode);
        Assert.StartsWith($"{dll}: ChipSharp compiles it.", summary.Output);
        Assert.Matches(@"^note: string\.StartsWith compares by the PC's language settings in \.NET; ChipSharp compares " +
            @"character codes \(like \.NET's invariant mode\): 2 places in Program\.Main\(\) \(IL_[0-9a-f]{4} and " +
            @"IL_[0-9a-f]{4}\)\. Pass StringComparison\.Ordinal to get the same result everywhere\.$", summary.Error.TrimEnd());
        Assert.Equal(summary.Output, details.Output);
        var lines = details.Error.ReplaceLineEndings("\n").TrimEnd().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"^note: Program\.Main\(\) at IL_[0-9a-f]{4} \(callvirt\): string\.StartsWith " +
            @"compares by the PC's language settings in \.NET; ChipSharp compares character codes \(like \.NET's invariant " +
            @"mode\)\. Pass StringComparison\.Ordinal to get the same result everywhere\.$", line));
        Assert.Equal((0, summary.Output, ""), quiet);
    }

    /// <summary>A program that prints <paramref name="expression"/>, of two strings a and b.</summary>
    private static string Program(string expression) => $$"""
        using System;

        class Program
        {
            static void Main()
            {
                string a = Text(), b = Text();
                Console.WriteLine({{expression}});
            }

            static string Text() => "a";
        }
        """;
}
