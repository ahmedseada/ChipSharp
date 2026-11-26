using System.Text;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Programs that print text they build ($"...", +, ToString), compiled by ChipSharp for the PC, in Release and in
/// Debug (whose IL differs), and run: each must print byte for byte what the same program prints on real .NET, holes
/// worked out first, side effects and all, alignments and formats included. The same programs run on the simulated
/// Pico too (Pico/PrintedTextSimulatorTests).
/// </summary>
public sealed class PrintedTextProgramTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [HostFact]
    public Task Interpolations_of_every_type_print_what_dotnet_prints() =>
        PrintsWhatDotnetPrints(PrintedTextPrograms.Interpolations);

    [HostFact]
    public Task Alignments_and_formats_pad_and_write_like_dotnet() =>
        PrintsWhatDotnetPrints(PrintedTextPrograms.AlignmentsAndFormats);

    [HostFact]
    public Task Concatenations_and_ToString_print_what_dotnet_prints() =>
        PrintsWhatDotnetPrints(PrintedTextPrograms.Concatenations);

    [HostFact]
    public Task Holes_with_side_effects_run_first_in_order_as_in_dotnet() =>
        PrintsWhatDotnetPrints(PrintedTextPrograms.SideEffects);

    [HostFact]
    public Task Printed_text_in_loops_and_branches_and_of_fields_and_elements() =>
        PrintsWhatDotnetPrints(PrintedTextPrograms.LoopsAndPlaces);

    [HostFact]
    public async Task Alignment_counts_text_as_dotnet_does_in_UTF16_code_units()
    {
        var module = Lowered.Module(PrintedTextPrograms.NotAscii, assemblyName: "Program");

        var run = await HostRun.BuildAndRunAsync(module, _scratch);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(PrintedTextPrograms.NotAsciiOutput), run.Output);
    }

    /// <summary>
    /// Compiles <paramref name="source"/> in Release and in Debug, runs it on .NET and as ChipSharp programs, and
    /// compares; the whole-program pass must accept both.
    /// </summary>
    private async Task PrintsWhatDotnetPrints(string source)
    {
        var release = CSharpCompiler.Compile(source, "Program");
        var debug = CSharpCompiler.Compile(source, "Program", OutputKind.ConsoleApplication, OptimizationLevel.Debug);
        var dotnet = await HostRun.RunOnDotnetAsync(release, "Program", Path.Combine(_scratch, "dotnet"));
        Assert.NotEmpty(dotnet.Output);

        foreach (var (build, assembly) in new[] { ("release", release), ("debug", debug) })
        {
            var program = WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll"));
            var chipSharp = await HostRun.BuildAndRunAsync(Lowered.Lower(program), Path.Combine(_scratch, build));

            Assert.True(dotnet.Output.SequenceEqual(chipSharp.Output), $"{build}:\n{dotnet}\n{chipSharp}");
            Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
        }
    }
}

/// <summary>C# programs that print the text they build, for the PC and the Pico tests.</summary>
internal static class PrintedTextPrograms
{
    /// <summary>$"..." with holes of every type Console.Write prints, at their extremes, of text and of null.</summary>
    public const string Interpolations = """
        using System;

        class Program
        {
            static string Name(int road) => road == 1 ? "north" : null;

            static void Main()
            {
                int lane = 2;
                uint u = 4000000000u;
                long l = long.MinValue;
                ulong ul = ulong.MaxValue;
                bool on = true;
                float f = 1.5f;
                double d = -0.1;
                byte b = 200;
                sbyte sb = -4;
                short s = -300;
                ushort us = 65000;
                string name = "east";
                Console.WriteLine($"road : {lane}");
                Console.Write($"    {name} {-lane}");
                Console.WriteLine();
                Console.WriteLine($"{u} {l} {ul} {on} {!on} {f} {d} {b} {sb} {s} {us} {int.MaxValue} {int.MinValue}");
                Console.WriteLine($"{float.NaN} {double.NegativeInfinity} {-0f} {1e-7f} {double.Epsilon} {f * 3} {d / 3}");
                Console.WriteLine($"[{Name(1)}] [{Name(2)}] [{"lit"}] [{(string)null}] [{name}{name}{name}{name}{name}]");
                Console.WriteLine($"{lane}");
                Console.WriteLine($"{name}");
                Console.WriteLine($"{Name(2)}");
                Console.WriteLine($"");
                Console.Write($"no end {lane} ");
                Console.WriteLine($"{lane}" + "!" + $"{name}?");
                Console.WriteLine($"{{braces}} {lane} {lane + 1}{lane + 2}{lane + 3}{lane + 4}{lane + 5}");
                Console.Write($"{on}");
            }
        }
        """;

    /// <summary>Alignments on both sides, and the formats D, X, x and G of every whole number, R and G of floats.</summary>
    public const string AlignmentsAndFormats = """
        using System;

        class Program
        {
            static string Name(int road) => road == 1 ? "north" : null;

            static void Main()
            {
                int x = -42;
                long l = long.MinValue;
                ulong ul = ulong.MaxValue;
                bool on = true;
                string name = "east";
                Console.WriteLine($"[{x,6}] [{x,-6}] [{x,2}] [{x,0}] [{ul,25}] [{l,-22}|] [{7u,3}] [{(byte)9,-3}]");
                Console.WriteLine($"[{x:D5}] [{x:d}] [{x:D1}] [{x:D03}] [{x:G}] [{x:g}] [{x,8:D4}] [{x,-8:D4}] [{7u:D3}]");
                Console.WriteLine($"[{x:X}] [{x:x8}] [{x:X10}] [{(sbyte)-1:X}] [{(short)-2:x}] [{(byte)10:X2}]");
                Console.WriteLine($"[{(ushort)65535:X}] [{-1L:X}] [{ul:x}] [{0:X}] [{0:x4}] [{l:X}] [{long.MaxValue:x}]");
                Console.WriteLine($"[{x,10:X}] [{x,-10:x}] [{255,6:X4}] [{255,-6:x1}] [{l:D22}] [{int.MinValue,14:D11}]");
                Console.WriteLine($"[{name,8}] [{name,-8}] [{Name(2),3}|{Name(2),-3}] [{"",2}] [{"lit",5}] [{Name(1),-6}]");
                Console.WriteLine($"[{on,7}] [{!on,-7}] [{on,2}] [{on:X}] [{name:Q}] [{!on,6:D2}]");
                Console.WriteLine($"{1.5:G} {2.5f:R} {0.1:R} {1e20:G} {-0.0:R}");
                Console.WriteLine(x.ToString("D6") + "|" + x.ToString("X") + "|" + ((byte)7).ToString("x2") + "|"
                    + l.ToString() + "|" + on.ToString() + "|" + 1.5f.ToString() + "|" + 0.25.ToString("R"));
            }
        }
        """;

    /// <summary>+ of two to four parts and of more (string.Concat of an array), ToString, ?? "", ternaries among the parts.</summary>
    public const string Concatenations = """
        using System;

        class Program
        {
            static void Main()
            {
                string a = "a";
                string none = null;
                int n = 7;
                bool cut = true;
                Console.WriteLine("road : " + n);
                Console.WriteLine(a + none + a);
                Console.WriteLine(a + n + a + cut);
                Console.WriteLine("v" + n + (n + 1) + (n + 2) + (n + 3) + (n + 4));
                Console.WriteLine(a + a + a + a + a + a + none + a);
                Console.WriteLine("" + n);
                Console.WriteLine(n + "");
                Console.WriteLine("" + none);
                Console.WriteLine("lane " + n + (cut ? " cut" : ""));
                Console.WriteLine("lane " + (cut ? n : -n) + (!cut ? "" : " no") + "!" + (n > 5 ? a : none));
                Console.WriteLine(n.ToString());
                Console.WriteLine(cut.ToString() + 1.5 + 2.5f + 3L + 4u + 5UL + (byte)6 + (sbyte)-7 + (short)8);
                Console.WriteLine(1 + 2 + "a" + 1 + 2);
                Console.Write("x=" + n + ", ");
                Console.WriteLine(string.Concat("p", "q") + string.Concat("p", "q", "r", "s"));
                Console.WriteLine(n.ToString("X2") + (n * 1000L).ToString("D9"));
            }
        }
        """;

    /// <summary>Holes that print, count and change variables: worked out first, in order, then printed.</summary>
    public const string SideEffects = """
        using System;

        class Program
        {
            static int calls;

            static int Next()
            {
                calls++;
                Console.Write($"[next {calls}]");
                return calls;
            }

            static bool Flag()
            {
                Console.Write("[flag]");
                return calls > 1;
            }

            static string Word(int i)
            {
                Console.Write("[word " + i + "]");
                return i % 2 == 0 ? "even" : "odd";
            }

            static void Main()
            {
                Console.WriteLine($"{Next()} and {Next()}");
                Console.WriteLine("a" + Next() + "b" + Next() + Flag());
                int x = 4;
                Console.WriteLine($"{x++} {x} {x += 2} {x} {--x}");
                Console.WriteLine(x + " " + x++ + " " + x);
                Console.WriteLine($"{Word(Next())} {Flag(),-6}|{Next(),4}|{Next():D3}");
                Console.WriteLine("" + Next());
                Console.WriteLine("v" + Next() + Next() + Next() + Next() + Word(calls));
            }
        }
        """;

    /// <summary>Printed text in loops and branches, with holes of struct fields, static fields and array elements.</summary>
    public const string LoopsAndPlaces = """
        using System;

        struct Point
        {
            public int X;
            public bool On;
        }

        class Program
        {
            static int counter = 5;

            static void Main()
            {
                for (int i = 0; i < 4; i++)
                {
                    Console.WriteLine($"i={i,2}|{i * i:D3}|{(i % 2 == 0 ? "even" : "odd")}|{i > 1 && i < 3}");
                    if (i == 2)
                    {
                        Console.WriteLine("two: " + i);
                    }
                }

                int k = 3;
                while (k > 0)
                {
                    Console.Write($"{k--} ");
                }

                Console.WriteLine();
                Console.WriteLine($"state {k switch { 0 => 10, 1 => 11, _ => 12 }} end");
                Console.WriteLine("state " + k + (k switch { 0 => " off", _ => " on" }) + $"{k}" + (k == 0 ? "!" : "?"));
                string text = $"k = {k,3}";
                Console.WriteLine(text);
                foreach (int v in new[] { 5, -6 })
                {
                    Console.WriteLine("v=" + v);
                }

                Console.WriteLine($"{(k == 0 ? 1.5 : 2.5)} {(k == 0 ? "zero" : null)}");
                Point p = new Point { X = 9, On = true };
                int[] values = { 4, 5, 6 };
                Console.WriteLine($"x={p.X} on={p.On} c={counter} a={values[1]}");
                Console.WriteLine("x=" + p.X + " on=" + p.On + " c=" + counter + " a=" + values[2]);
                counter++;
                Console.WriteLine(p.X.ToString() + counter.ToString() + values[0].ToString() + p.On.ToString());
            }
        }
        """;

    /// <summary>Text that is not ASCII, aligned: .NET pads to a length in UTF-16 code units (a smiley is two).</summary>
    public const string NotAscii = """
        using System;

        class Program
        {
            static void Main()
            {
                string cafe = "caf\u00e9";
                Console.WriteLine($"[{cafe,6}] [{cafe,-6}] [{"\U0001F642",4}] [{"\u65e5\u672c",-3}]");
            }
        }
        """;

    /// <summary>What <see cref="NotAscii"/> prints, as UTF-8.</summary>
    public const string NotAsciiOutput = "[  caf\u00e9] [caf\u00e9  ] [  \U0001F642] [\u65e5\u672c ]\r\n";

    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Interpolations), Interpolations),
        (nameof(AlignmentsAndFormats), AlignmentsAndFormats),
        (nameof(Concatenations), Concatenations),
        (nameof(SideEffects), SideEffects),
        (nameof(LoopsAndPlaces), LoopsAndPlaces),
    ];
}
