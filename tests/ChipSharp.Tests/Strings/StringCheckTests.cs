using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Strings;

/// <summary>
/// Strings in the whole-program pass, for a target: what reads text (literals, comparisons, searching, chars, printed
/// text, enum names, a switch on strings) compiles on every target, a chip without a heap too; what makes text (kept
/// text, the methods of string that give a new string, a span of a string's chars) needs a heap, and says so there.
/// </summary>
public sealed class StringCheckTests : IDisposable
{
    private const string NeedsHeap = "needs a heap, and the target 'pico-no-heap' (264 KB RAM) has no heap";

    /// <summary>What reads text and makes none: fine without a heap.</summary>
    private const string Reading = """
        using System;

        enum Mode { Off, Heat }

        class Program
        {
            static readonly string Greeting = "hello";

            static int Code(string word)
            {
                switch (word)
                {
                    case "alpha": return 1;
                    case "bravo": return 2;
                    case "charl": return 3;
                    case "delta": return 4;
                    case "echoo": return 5;
                    case "foxtr": return 6;
                    case "golfx": return 7;
                    case "hotel": return 8;
                    default: return 0;
                }
            }

            static void Main()
            {
                string word = Greeting;
                char first = word[0];
                Console.WriteLine(word.Length + word.IndexOf('l') + word.IndexOf("lo") + Code("delta"));
                Console.WriteLine(word == "hello" && word.StartsWith("he") && word.Contains('o') && char.IsLetter(first));
                Console.WriteLine(string.CompareOrdinal(word, "help") + " " + char.ToUpper(first) + " " + int.Parse(" 42"));
                Console.WriteLine($"{Mode.Heat} {first,3} {word,-7}|");
                Console.WriteLine("{0} is {1}", word, Mode.Off);
                foreach (char c in word)
                {
                    Console.Write(c);
                }

                Console.WriteLine();
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

    /// <summary>The Pico without its heap: a console, no heap.</summary>
    private TargetDescription NoHeap => new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
        ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");

    [Theory]
    [InlineData(OptimizationLevel.Release)]
    [InlineData(OptimizationLevel.Debug)]
    public void What_reads_text_compiles_without_a_heap(OptimizationLevel optimization)
    {
        var analysis = WholeProgramPass.Check(StringPrograms.Compile(Reading, "Reading", optimization == OptimizationLevel.Debug),
            NoHeap);

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void What_reads_text_becomes_a_module_that_makes_no_object()
    {
        var module = IlToTir.Lower(WholeProgramPass.Analyze(StringPrograms.Compile(Reading, "Reading", debug: false), NoHeap));

        Assert.DoesNotContain(module.Functions, function => function.MakesObjects);
        Assert.Contains(module.TypeDescriptors, type => type.ExternalSymbol == "chipsharp_string_type");
    }

    [Fact]
    public void What_makes_text_needs_a_heap_where_there_is_none_and_compiles_where_there_is_one()
    {
        var assembly = StringPrograms.Compile("""
            using System;

            class Program
            {
                static string kept;

                static string Report(float result) => $"result: {result}";

                static void Main()
                {
                    kept = "lane " + 3;
                    Console.WriteLine(kept.ToUpper());
                    Console.WriteLine(Report(1.5f).Substring(1));
                    Console.WriteLine("a,b".Split(',').Length);
                    ReadOnlySpan<char> span = "chars".AsSpan();
                    Console.WriteLine(span.Length);
                }
            }
            """, "Making", debug: false);

        var analysis = WholeProgramPass.Check(assembly, NoHeap);

        const string Making = "making new text (Substring, Replace, Trim, ToUpper, Split, AsSpan and the other methods of " +
            "string that give a new string)";
        Assert.Equal(
        [
            $"keeping built text in a variable {NeedsHeap}",
            $"{Making} {NeedsHeap}",
        ], analysis.Groups.Select(group => group.Feature));
        Assert.Equal(
        [
            $"Program.Main() at IL_000b (call): keeping built text in a variable {NeedsHeap}. Print it straight away: " +
                "Console.WriteLine($\"...\").",
            $"Program.Main() at IL_0015 (callvirt): {Making} {NeedsHeap}. Print the parts one after another with Console.Write.",
            $"Program.Main() at IL_002a (callvirt): {Making} {NeedsHeap}. Print the parts one after another with Console.Write.",
            $"Program.Main() at IL_003a (call): {Making} {NeedsHeap}. Print the parts one after another with Console.Write.",
            $"Program.Main() at IL_004b (call): {Making} {NeedsHeap}. Print the parts one after another with Console.Write.",
            $"Program.Report(float) at IL_002a (call): keeping built text in a variable {NeedsHeap}. Print it straight " +
                "away: Console.WriteLine($\"...\").",
        ], analysis.Problems.Select(problem => problem.Line));
        Assert.True(WholeProgramPass.Check(assembly).IsSupported);
        Assert.True(WholeProgramPass.Check(assembly, PicoBuild.Pico).IsSupported);
    }

    [Fact]
    public void A_module_that_makes_text_cannot_be_generated_for_a_target_without_a_heap()
    {
        var module = Lowered.Module("""
            class Program
            {
                static void Main() => System.Console.WriteLine("a".PadLeft(3).Length);
            }
            """);

        var error = Assert.Throws<ChipSharpException>(() => new ChipSharp.Backend.Llvm.LlvmCodeGenerator().Generate(module, NoHeap));

        Assert.Contains("creates objects ('%", error.Message);
        Assert.Contains("ChipSharp.Runtime.StringPadLeft", error.Message);
    }
}
