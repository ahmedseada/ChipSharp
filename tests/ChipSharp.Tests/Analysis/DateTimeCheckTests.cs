using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// DateTime on a target: dates, their text printed straight away and TryFormat need no heap, so a Pico without one
/// takes them; text kept in a string needs one; reading or setting the clock (DateTime.Now, Stopwatch,
/// Environment.TickCount, Clock.Start) needs the capability "clock", which the Pico and the PC have and "tiny" has not.
/// </summary>
public sealed class DateTimeCheckTests : IDisposable
{
    private const string Hint = "Count time with ChipSharp.Hardware.Timing or a timer interrupt, or build for a target " +
        "with a timer that counts microseconds (\"clock\": true in its file), e.g. -t pico.";

    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private TargetDescription Tiny => new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name);

    private TargetDescription NoHeap => new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
        ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");

    [Fact]
    public void The_Pico_and_the_PC_have_a_clock_and_a_new_chip_has_none_until_its_file_says()
    {
        Assert.True(Shipped.Load("pico").HasCapability("clock"));
        Assert.True(Shipped.Load("host").HasCapability("clock"));
        Assert.False(Tiny.HasCapability("clock"));
    }

    [Fact]
    public void Reading_or_setting_the_clock_on_a_target_without_one_names_each_place_and_dates_alone_are_fine()
    {
        var analysis = WholeProgramPass.Check(Load("""
            using System;
            using System.Diagnostics;
            using ChipSharp.Hardware;

            class Program
            {
                static long last;

                static void Main()
                {
                    var now = DateTime.Now;
                    long t = Stopwatch.GetTimestamp();
                    int ms = Environment.TickCount;
                    Clock.Start(new DateTime(2026, 1, 1));
                    var d = new DateTime(2026, 11, 27).AddMonths(1);
                    Span<char> b = stackalloc char[20];
                    d.TryFormat(b, out _, "HH:mm");
                    last = now.Ticks + t + ms + Stopwatch.Frequency + Stopwatch.GetElapsedTime(t, t + 5).Ticks;
                }
            }
            """), Tiny);

        Assert.Equal(
        [
            $"Program.Main() at IL_0000 (call): System.DateTime.get_Now() needs a clock, and the target 'tiny' has none. {Hint}",
            $"Program.Main() at IL_0006 (call): System.Diagnostics.Stopwatch.GetTimestamp() needs a clock, and the target " +
                $"'tiny' has none. {Hint}",
            $"Program.Main() at IL_000c (call): System.Environment.get_TickCount() needs a clock, and the target 'tiny' has " +
                $"none. {Hint}",
            $"Program.Main() at IL_001e (call): ChipSharp.Hardware.Clock.Start(System.DateTime) needs a clock, and the target " +
                $"'tiny' has none. {Hint}",
            $"Program.Main() at IL_006d (ldsfld): System.Diagnostics.Stopwatch.Frequency needs a clock, and the target " +
                $"'tiny' has none. {Hint}",
            $"Program.Main() at IL_0078 (call): System.Diagnostics.Stopwatch.GetElapsedTime(long, long) needs a clock, and " +
                $"the target 'tiny' has none. {Hint}",
        ], analysis.Problems.Select(problem => problem.Line));
    }

    [Fact]
    public void Text_of_dates_printed_straight_away_or_into_a_span_needs_no_heap_and_kept_text_needs_one()
    {
        const string Program = """
            using System;

            class Program
            {
                static string kept;

                static void Main()
                {
                    var d = new DateTime(2026, 11, 27, 18, 5, 9);
                    Console.WriteLine($"{d:HH:mm:ss} {d,12:d} {DateOnly.FromDateTime(d):D} {TimeOnly.FromDateTime(d)}");
                    Console.WriteLine(d);
                    Console.WriteLine(d.ToString("yyyy-MM-dd"));
                    Console.WriteLine("{0:o} {1:T}", d, d);
                    Console.WriteLine("at " + d.ToShortTimeString());
                    Span<char> buffer = stackalloc char[30];
                    if (d.TryFormat(buffer, out int written, "dddd"))
                    {
                        Console.WriteLine(written);
                    }

                    kept = $"{d:t}";
                    kept = d.ToString("R");
                }
            }
            """;

        var analysis = WholeProgramPass.Check(Load(Program), NoHeap);

        const string Kept = "keeping built text in a variable needs a heap, and the target 'pico-no-heap' (264 KB RAM) has " +
            "no heap. Print it straight away: Console.WriteLine($\"...\").";
        // (and DateTime's Text, which makes the string, says why it needs one: a char[] and a new string)
        Assert.Equal([$"Program.Main() at IL_0113 (call): {Kept}", $"Program.Main() at IL_0124 (call): {Kept}"],
            analysis.Problems.Take(2).Select(problem => problem.Line));
        Assert.All(analysis.Problems.Skip(2), problem => Assert.StartsWith("ChipSharp.Library.DateTime.Text(", problem.Line));
        Assert.True(WholeProgramPass.Check(Load(Program), PicoBuild.Pico).IsSupported);
        Assert.True(WholeProgramPass.Check(Load(Program.Replace("kept = ", "Console.WriteLine(", StringComparison.Ordinal)
            .Replace("{d:t}\";", "{d:t}\");", StringComparison.Ordinal).Replace("ToString(\"R\");", "ToString(\"R\"));",
                StringComparison.Ordinal)), NoHeap).IsSupported);
    }

    [Fact]
    public void A_format_known_only_when_the_program_runs_makes_a_string_with_ToString_on_a_heap()
    {
        var analysis = WholeProgramPass.Check(Load("""
            using System;

            class Program
            {
                static void Main()
                {
                    string format = Console.ReadLine();
                    Console.WriteLine(new DateTime(2026, 11, 27).ToString(format));
                }
            }
            """), PicoBuild.Pico);

        Assert.True(analysis.IsSupported, string.Join("\n", analysis.Problems.Select(problem => problem.Line)));
        Assert.Contains(analysis.Methods, method => method.DisplayName == "ChipSharp.Library.DateTime.ToString");
    }

    [Fact]
    public void Text_of_a_date_is_noted_where_dotnet_would_write_it_by_the_PCs_culture()
    {
        var analysis = WholeProgramPass.Check(Load("""
            using System;

            var d = new DateTime(2026, 11, 27);
            Console.WriteLine($"{d:d}");
            Console.WriteLine(d.ToString("MMMM"));
            Console.WriteLine(d.Year);
            Span<char> buffer = stackalloc char[10];
            Console.WriteLine(d.TryFormat(buffer, out _, "yyyy"));
            """), PicoBuild.Pico);

        Assert.Equal(["a date or time as text", "a date or time as text", "a date or time as text"],
            analysis.Notes.Select(note => note.Api));
        Assert.Contains("ChipSharp writes it as .NET's invariant culture does (11/27/2026 18:05:09)", analysis.Notes[0].Line);
    }

    private static IlAssembly Load(string source) =>
        IlAssembly.Load(CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly], "Test"), "Test.dll");
}
