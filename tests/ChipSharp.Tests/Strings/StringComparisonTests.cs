using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Strings;

/// <summary>
/// The methods of string with a StringComparison (Compare, Equals, StartsWith, EndsWith, IndexOf, LastIndexOf,
/// Contains), for every value of it, on the PC and on the simulated Pico, in Release and in Debug: they print what .NET
/// prints in its invariant mode, where every comparison is by character codes and the IgnoreCase ones compare upper
/// case (Ordinal and OrdinalIgnoreCase give that with any culture); where .NET throws, the program stops with its
/// message.
/// </summary>
public sealed class StringComparisonTests : IDisposable
{
    /// <summary>Every method, for every comparison, of every pair of a few words (Latin-1, and wide text).</summary>
    private const string Everything = """
        using System;

        class Program
        {
            static void Main()
            {
                string[] words = ["apple", "Apple", "APPLE", "app", "ap", "", "pPl", "ÿes", "ŸES", "Café", "CAFÉ", "Ωx"];
                for (int c = 0; c <= 5; c++)
                {
                    foreach (var a in words)
                    {
                        foreach (var b in words)
                        {
                            Console.WriteLine($"{c} {a}|{b}: {string.Compare(a, b, (StringComparison)c)} " +
                                $"{a.Equals(b, (StringComparison)c)} {string.Equals(a, b, (StringComparison)c)} " +
                                $"{a.StartsWith(b, (StringComparison)c)} {a.EndsWith(b, (StringComparison)c)} " +
                                $"{a.IndexOf(b, (StringComparison)c)} {a.LastIndexOf(b, (StringComparison)c)} " +
                                $"{a.Contains(b, (StringComparison)c)}");
                        }

                        Console.WriteLine($"{c} {a}: {a.IndexOf("p", a.Length < 2 ? a.Length : 2, (StringComparison)c)} {string.Compare(a, null, (StringComparison)c)} " +
                            $"{string.Compare(null, a, (StringComparison)c)} {a.Equals(null, (StringComparison)c)}");
                    }
                }

                Console.WriteLine(string.Compare(null, null, StringComparison.Ordinal));
                Console.WriteLine(string.Equals(null, null, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine(string.Equals(null, "a", StringComparison.Ordinal));
                Console.WriteLine("abc".StartsWith("ABC", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine("command: RESET".EndsWith("reset", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine("Hello".IndexOf("", 5, StringComparison.Ordinal));
            }
        }
        """;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>What .NET throws for: a null value, a start past the end, a value that is no StringComparison.</summary>
    public static TheoryData<string> Failures => new()
    {
        "\"abc\".StartsWith(null, StringComparison.Ordinal)",
        "\"abc\".EndsWith(null, StringComparison.CurrentCulture)",
        "\"abc\".IndexOf(null, StringComparison.OrdinalIgnoreCase)",
        "\"abc\".IndexOf(null, (StringComparison)9)",
        "\"abc\".IndexOf(\"b\", 4, StringComparison.Ordinal)",
        "\"abc\".IndexOf(\"b\", (StringComparison)6)",
        "\"abc\".LastIndexOf(null, StringComparison.Ordinal)",
        "\"abc\".Contains(\"b\", (StringComparison)(-1))",
        "string.Compare(\"a\", \"b\", (StringComparison)6)",
        "string.Compare(null, null, (StringComparison)6)",
        "\"abc\".Equals(\"abc\", (StringComparison)7)",
        "\"abc\".StartsWith(\"a\", (StringComparison)8)",
    };

    [HostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task On_the_PC_every_comparison_gives_what_dotnet_gives(bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(StringPrograms.ForDotnet(Everything, "Comparisons"), "Comparisons",
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(StringPrograms.Compile(Everything, "Comparisons", debug)));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(dotnet.Text, run.Text);
        Assert.Equal(dotnet.Output, run.Output);
    }

    [SimulatorTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task On_the_Pico_every_comparison_prints_over_UART0_what_dotnet_prints(bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(StringPrograms.ForDotnet(Everything, "Comparisons"), "Comparisons",
            Path.Combine(_scratch, "dotnet"));
        var assembly = StringPrograms.Compile(Everything, "Comparisons", debug, [PicoBuild.HardwareAssembly]);
        var pico = PicoBuild.Build(IlToTir.Lower(WholeProgramPass.Analyze(assembly, PicoBuild.Pico)),
            Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 60_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Output, run.Uart0);
    }

    [HostTheory]
    [MemberData(nameof(Failures))]
    public async Task Where_dotnet_throws_the_program_stops_with_dotnets_message(string expression)
    {
        var source = $$"""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(1);
                    Console.WriteLine({{expression}});
                    Console.WriteLine(2);
                }
            }
            """;
        var dotnet = await HostRun.RunOnDotnetAsync(StringPrograms.ForDotnet(source, "Throws"), "Throws",
            Path.Combine(_scratch, "dotnet"), failing: true);
        var expected = string.Concat(dotnet.Errors.ReplaceLineEndings("\n").Split('\n')
            .TakeWhile(line => !line.StartsWith("   at ", StringComparison.Ordinal)).Select(line => line + "\r\n"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(StringPrograms.Compile(source, "Throws", debug: false)));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "throws"));

        Assert.StartsWith("Unhandled exception. System.Argument", expected);
        Assert.Equal((1, "1\r\n", expected), (run.ExitCode, run.Text, run.Errors));
    }
}
