using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Programs with arrays in static fields (constant tables and static fixed arrays), compiled by ChipSharp for the PC
/// (Release and Debug IL) and run: each prints what .NET prints. The same programs run on the simulated Pico too
/// (Pico/StaticArraySimulatorTests).
/// </summary>
public sealed class StaticArrayProgramTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [HostFact]
    public Task Tables_are_read_in_loops_and_passed_to_methods_that_only_read_them() =>
        PrintsWhatDotnetPrints(StaticArrayPrograms.Tables);

    [HostFact]
    public Task A_table_a_callee_writes_is_a_fixed_array_that_keeps_what_it_wrote() =>
        PrintsWhatDotnetPrints(StaticArrayPrograms.WrittenByCallees);

    [HostFact]
    public Task A_static_buffer_is_filled_and_summed_across_calls() => PrintsWhatDotnetPrints(StaticArrayPrograms.Buffer);

    [HostFact]
    public Task Every_element_type_and_structs_keep_their_values() => PrintsWhatDotnetPrints(StaticArrayPrograms.ElementTypes);

    [HostFact]
    public Task Static_constructors_see_the_tables_of_other_types_from_the_start() =>
        PrintsWhatDotnetPrints(StaticArrayPrograms.Initializers);

    [HostFact]
    public Task Tables_of_record_structs_and_computed_elements_are_set_when_the_program_starts() =>
        PrintsWhatDotnetPrints(StaticArrayPrograms.ComputedElements);

    [HostFact]
    public async Task An_index_outside_a_table_stops_the_program_with_IndexOutOfRangeException_and_exit_code_1()
    {
        var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(
            CSharpCompiler.Compile(StaticArrayPrograms.OutOfRange, "Program"), "Program.dll")));

        var run = await HostRun.BuildAndRunAsync(module, _scratch);

        Assert.Equal(StaticArrayPrograms.OutOfRangePrinted, run.Text);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(ArrayPrograms.IndexOutOfRangeMessage, run.Errors);
    }

    /// <summary>
    /// Compiles <paramref name="source"/> (Release and Debug), runs it on .NET and as ChipSharp programs, and compares.
    /// The whole-program pass checks it first.
    /// </summary>
    private async Task PrintsWhatDotnetPrints(string source)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Program"), "Program",
            Path.Combine(_scratch, "dotnet"));
        Assert.NotEmpty(dotnet.Output);

        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var assembly = CSharpCompiler.Compile(source, "Program", OutputKind.ConsoleApplication, optimization);
            var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll")));

            var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, optimization.ToString()));

            Assert.Equal(dotnet.Text, chipSharp.Text);
            Assert.Equal(dotnet.Output, chipSharp.Output);
            Assert.Equal(0, chipSharp.ExitCode);
        }
    }
}

/// <summary>The programs of <see cref="StaticArrayProgramTests"/>, which the Pico tests run too.</summary>
internal static class StaticArrayPrograms
{
    public const string Tables = """
        using System;

        class Program
        {
            // a 7-segment display's segments (gfedcba) for each digit
            static readonly byte[] Segments = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };
            static readonly int[] Primes = { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37 };
            static readonly short[] Steps = { -300, 120, 45 };

            static int Lit(byte pattern)
            {
                int count = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((pattern & (1 << bit)) != 0)
                    {
                        count++;
                    }
                }

                return count;
            }

            static int Segment(int digit) => Segments[digit % Segments.Length];

            static long Sum(int[] values)
            {
                long total = 0;
                foreach (int value in values)
                {
                    total += value;
                }

                return total;
            }

            static int Largest(short[] values, int start)
            {
                int largest = values[start];
                for (int i = start + 1; i < values.Length; i++)
                {
                    largest = values[i] > largest ? values[i] : largest;
                }

                return largest;
            }

            static void Main()
            {
                for (int digit = 0; digit < 10; digit++)
                {
                    Console.Write(digit);
                    Console.Write(": ");
                    Console.Write(Segment(digit));
                    Console.Write(" lights ");
                    Console.WriteLine(Lit(Segments[digit]));
                }

                Console.WriteLine(Segment(1234));
                Console.WriteLine(Primes.Length);
                Console.WriteLine(Sum(Primes));
                Console.WriteLine(Largest(Steps, 0) + Largest(Steps, 2));
                int[] primes = Primes;
                Console.WriteLine(primes[primes.Length - 1] * Steps[0]);
            }
        }
        """;

    public const string WrittenByCallees = """
        using System;

        class Program
        {
            static readonly int[] Scores = { 10, 20, 30 };
            static readonly int[] Limits = { 5, 6, 7 };
            static int[] counts = new int[3];

            static void Double(int[] values)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] *= 2;
                }
            }

            static void Twice(int[] values)
            {
                Double(values);
                Double(values);
            }

            static void Increment(ref int value) => value++;

            static int Peek(in int value) => value;

            static int Sum(int[] values) => values[0] + values[1] + values[2];

            static void Main()
            {
                Twice(Scores);
                Console.WriteLine(Sum(Scores));
                Twice(Scores);
                Console.WriteLine(Scores[2]);
                for (int i = 0; i < 5; i++)
                {
                    Increment(ref counts[i % counts.Length]);
                }

                ref int last = ref counts[2];
                last += 40;
                Console.WriteLine(Sum(counts));
                Console.WriteLine(counts[0] * 100 + counts[1] * 10 + counts[2]);
                Console.WriteLine(Peek(in Limits[1]) + Sum(Limits));
            }
        }
        """;

    public const string Buffer = """
        using System;

        class Program
        {
            static int[] samples = new int[8];
            static int next;
            static long[] history = { 1, 2, 3, 4 };

            static void Record(int value)
            {
                samples[next] = value;
                next = (next + 1) % samples.Length;
            }

            static int Total()
            {
                int total = 0;
                foreach (int sample in samples)
                {
                    total += sample;
                }

                return total;
            }

            static void Shift(long value)
            {
                for (int i = history.Length - 1; i > 0; i--)
                {
                    history[i] = history[i - 1];
                }

                history[0] = value;
            }

            static void Main()
            {
                for (int round = 1; round <= 3; round++)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        Record(round * 10 + i);
                    }

                    Console.Write("round ");
                    Console.Write(round);
                    Console.Write(": ");
                    Console.WriteLine(Total());
                    Shift(Total() * 1000L);
                }

                foreach (long value in history)
                {
                    Console.WriteLine(value);
                }
            }
        }
        """;

    public const string ElementTypes = """
        using System;

        enum Phase : byte { Off, Rising, On, Falling }

        struct Point
        {
            public int X;
            public int Y;

            public readonly int Length => (X < 0 ? -X : X) + (Y < 0 ? -Y : Y);

            public void Move(int dx) => X += dx;
        }

        class Program
        {
            static readonly bool[] Pattern = { true, false, true, true };
            static readonly sbyte[] Small = { -128, 127, -1 };
            static readonly ushort[] Wide = { 65535, 1, 2 };
            static readonly uint[] Big = { 4000000000, 1 };
            static readonly long[] Longs = { -5000000000, 7 };
            static readonly ulong[] Huge = { 18446744073709551615, 3 };
            static readonly nint[] Natives = { -2, 3 };
            static readonly float[] Gains = { 1.5f, -0.25f, 3.0e-5f };
            static readonly double[] Curve = { 0.1, 0.2, 0.30000000000000004, -0.0 };
            static readonly Phase[] Cycle = { Phase.Rising, Phase.On, Phase.Falling, Phase.Off };
            static Point[] points = new Point[3];
            static readonly Point[] Origins = new Point[2];

            static void Main()
            {
                foreach (bool on in Pattern)
                {
                    Console.Write(on);
                    Console.Write(" ");
                }

                Console.WriteLine();
                Console.WriteLine(Small[0] + Small[1] + Small[2]);
                Console.WriteLine(Wide[0] + Wide[1]);
                Console.WriteLine(Big[0] + Big[1]);
                Console.WriteLine(Longs[0] * Longs[1]);
                Console.WriteLine(Huge[0] - Huge[1]);
                Console.WriteLine(Natives[0] * Natives[1]);
                float gain = 0;
                foreach (float g in Gains)
                {
                    gain += g;
                }

                Console.WriteLine(gain);
                Console.WriteLine(Curve[0] + Curve[1] == Curve[2]);
                Console.WriteLine(1 / Curve[3] < 0);
                int phase = 0;
                for (int i = 0; i < 6; i++)
                {
                    phase = (int)Cycle[phase];
                }

                Console.WriteLine(phase);
                points[1].X = 3;
                points[1].Y = -4;
                points[2].Move(5);
                points[2].Move(2);
                Console.WriteLine(points[1].Length + points[2].X + Origins[1].Length + Origins.Length);
            }
        }
        """;

    public const string Initializers = """
        using System;

        static class Tables
        {
            public static readonly int[] Squares = { 0, 1, 4, 9, 16, 25 };
            public static int[] Seen = new int[4];
        }

        class Program
        {
            static readonly int Fourth;
            static int count = 3;
            static readonly int[] Cubes = { 0, 1, 8, 27 };

            static Program()
            {
                Fourth = Tables.Squares[4] + Cubes[count];
                Tables.Seen[0] = Fourth;
                Console.WriteLine("static constructor");
            }

            static void Main()
            {
                Console.WriteLine(Fourth);
                Console.WriteLine(Tables.Seen[0] + Tables.Squares[5]);
            }
        }
        """;

    /// <summary>
    /// Elements the initializers compute: a table of record structs (a traffic light's steps), constructors, tuples,
    /// calls and static fields, set when the program starts; one of them written later too.
    /// </summary>
    public const string ComputedElements = """
        using System;

        enum Light { Red, Yellow, Green }

        readonly record struct Step(Light Light, int Seconds, bool CanBeCut = false);

        static class Plan
        {
            public static int Pause = 3;
            public static readonly Step[] Steps =
            [
                new(Light.Green, 4, CanBeCut: true),
                new(Light.Yellow, 1),
                new(Light.Red, Pause - 1),
            ];
        }

        struct Point
        {
            public int X;
            public int Y;

            public Point(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        class Program
        {
            static int scale = 10;
            static readonly int[] Scaled = { scale, scale * 2, Twice(scale) + 1 };
            static readonly (int Min, int Max)[] Ranges = { (1, 5), (scale, scale + 5) };
            static readonly Point[] Corners = { new Point(1, 2), new(-3, 4), default };
            static long[] totals = { Twice(3), 7 };

            static int Twice(int value) => value * 2;

            static int Seconds(Step[] steps)
            {
                int seconds = 0;
                foreach (var step in steps)
                {
                    seconds += step.Seconds;
                }

                return seconds;
            }

            static void Main()
            {
                foreach (var step in Plan.Steps)
                {
                    Console.WriteLine($"{step.Light} {step.Seconds} {step.CanBeCut}");
                }

                Console.WriteLine(Seconds(Plan.Steps));
                for (int i = 0; i < Scaled.Length; i++)
                {
                    Console.WriteLine(Scaled[i]);
                }

                foreach (var (min, max) in Ranges)
                {
                    Console.WriteLine($"{min}..{max}");
                }

                foreach (var corner in Corners)
                {
                    Console.WriteLine($"({corner.X}, {corner.Y})");
                }

                totals[1] += totals[0];
                Console.WriteLine(totals[1]);
            }
        }
        """;

    public const string OutOfRange = """
        using System;

        class Program
        {
            static readonly int[] Table = { 1, 2, 3 };

            static int At(int index) => Table[index];

            static void Main()
            {
                Console.WriteLine(At(2));
                Console.WriteLine(At(3));
            }
        }
        """;

    public const string OutOfRangePrinted = "3\r\n";

    /// <summary>The programs above that print what .NET prints, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        ("Tables", Tables),
        ("WrittenByCallees", WrittenByCallees),
        ("Buffer", Buffer),
        ("ElementTypes", ElementTypes),
        ("Initializers", Initializers),
        ("ComputedElements", ComputedElements),
    ];
}
