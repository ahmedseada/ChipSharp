using System.Globalization;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Programs with arrays on the stack, compiled by ChipSharp for the PC (Release and Debug IL) and run: each prints
/// what .NET prints. An index outside an array stops the program with .NET's message and exit code 1.
/// </summary>
public sealed class ArrayProgramTests : IDisposable
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
    public Task Every_element_type_holds_its_values_like_dotnet() => PrintsWhatDotnetPrints(ArrayPrograms.ElementTypes);

    [HostFact]
    public Task Initializers_small_and_large_fill_the_array() => PrintsWhatDotnetPrints(ArrayPrograms.Initializers);

    [HostFact]
    public Task Arrays_passed_down_are_read_written_and_measured_by_the_callees() =>
        PrintsWhatDotnetPrints(ArrayPrograms.Callees);

    [HostFact]
    public Task Loops_sort_fill_and_sum_and_a_new_array_starts_zeroed_each_time_round() =>
        PrintsWhatDotnetPrints(ArrayPrograms.Loops);

    [HostFact]
    public Task Long_ulong_uint_and_native_indexes_reach_the_same_elements() => PrintsWhatDotnetPrints(ArrayPrograms.Indexes);

    [HostFact]
    public Task Big_arrays_on_the_stack_link_and_run() => PrintsWhatDotnetPrints(ArrayPrograms.BigArrays);

    [HostFact]
    public async Task An_index_outside_the_array_stops_the_program_with_IndexOutOfRangeException_and_exit_code_1()
    {
        var index = 0;
        foreach (var (source, printed) in ArrayPrograms.OutOfRange)
        {
            var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(CSharpCompiler.Compile(source, "Program"),
                "Program.dll")));

            var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, $"case{index++}"));

            Assert.Equal(printed, run.Text);
            Assert.Equal(1, run.ExitCode);
            Assert.Equal(ArrayPrograms.IndexOutOfRangeMessage, run.Errors);
        }
    }

    /// <summary>
    /// Compiles <paramref name="source"/> (Release and Debug), runs it on .NET and as ChipSharp programs, and compares.
    /// The whole-program pass checks it first.
    /// </summary>
    private async Task PrintsWhatDotnetPrints(string source)
    {
        var release = CSharpCompiler.Compile(source, "Program");
        var dotnet = await HostRun.RunOnDotnetAsync(release, "Program", Path.Combine(_scratch, "dotnet"));
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

/// <summary>The programs of <see cref="ArrayProgramTests"/>, which the Pico tests run too.</summary>
internal static class ArrayPrograms
{
    /// <summary>What .NET writes first for an IndexOutOfRangeException nothing catches, and so does ChipSharp.</summary>
    public const string IndexOutOfRangeMessage =
        "Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array.\r\n";

    public const string ElementTypes = """
        using System;

        class Program
        {
            static void Main()
            {
                sbyte[] sbytes = new sbyte[3];
                sbytes[0] = -128;
                sbytes[1] = 127;
                sbytes[2] = (sbyte)(sbytes[1] + 1);
                foreach (sbyte value in sbytes) Console.WriteLine(value);

                byte[] bytes = new byte[2];
                bytes[0] = 255;
                bytes[1] = (byte)(bytes[0] + 2);
                bytes[0]++;
                Console.WriteLine(bytes[0] + bytes[1]);

                short[] shorts = { -32768, 32767, 0 };
                shorts[2] = (short)(shorts[1] + 1);
                Console.WriteLine(shorts[0] + shorts[2]);

                ushort[] ushorts = new ushort[2];
                ushorts[0] = 65535;
                ushorts[1] += 70000 % 65536;
                Console.WriteLine(ushorts[0] + ushorts[1]);

                int[] ints = { int.MinValue, int.MaxValue };
                ints[1] += 1;
                Console.WriteLine(ints[0] == ints[1]);

                uint[] uints = { 4000000000, 1 };
                uints[1] -= 2;
                Console.WriteLine(uints[0]);
                Console.WriteLine(uints[1]);

                long[] longs = { long.MinValue, 3000000000 };
                longs[1] *= 3;
                Console.WriteLine(longs[0]);
                Console.WriteLine(longs[1]);

                ulong[] ulongs = new ulong[1];
                ulongs[0]--;
                Console.WriteLine(ulongs[0]);

                nint[] nints = new nint[2];
                nints[0] = -7;
                nints[1] = nints[0] * 3;
                Console.WriteLine((long)nints[1]);

                nuint[] nuints = new nuint[2];
                nuints[1] = 5;
                nuints[0] = nuints[1] - 6;
                Console.WriteLine(nuints[0] > nuints[1]);

                bool[] flags = new bool[3];
                flags[1] = true;
                flags[2] = !flags[1];
                foreach (bool flag in flags) Console.WriteLine(flag);
            }
        }
        """;

    public static string Initializers { get; } = $$"""
        using System;

        class Program
        {
            static long Sum(int[] values)
            {
                long sum = 0;
                for (int i = 0; i < values.Length; i++) sum += values[i];
                return sum;
            }

            static void Main()
            {
                int[] pair = new[] { 5, -2 };
                Console.WriteLine(pair[0] * pair[1]);
                byte[] three = { 1, 2, 3 };
                Console.WriteLine(three[2]);
                long[] longs = { -1, 3000000000, 0, 7 };
                Console.WriteLine(longs[1] + longs[3]);
                bool[] flags = { true, false, true, true };
                Console.WriteLine(flags[3]);
                int[] collection = [10, 20, 30];
                Console.WriteLine(collection[1]);

                int[] table = { {{Numbers(200, i => (i * 7919 % 2001) - 1000)}} };
                Console.WriteLine(table.Length);
                Console.WriteLine(Sum(table));
                Console.WriteLine(table[0]);
                Console.WriteLine(table[199]);

                short[] shorts = { {{Numbers(120, i => (i * 541 % 60001) - 30000)}} };
                long shortSum = 0;
                foreach (short value in shorts) shortSum += value;
                Console.WriteLine(shortSum);

                sbyte[] sbytes = { {{Numbers(300, i => (i * 37 % 256) - 128)}} };
                int sbyteSum = 0;
                foreach (sbyte value in sbytes) sbyteSum += value;
                Console.WriteLine(sbyteSum);

                ulong[] ulongs = { {{Numbers(40, i => i * 1234567)}}, 18446744073709551615 };
                Console.WriteLine(ulongs[40]);
                Console.WriteLine(ulongs[39]);

                bool[] manyFlags = { {{string.Join(", ", Enumerable.Range(0, 64).Select(i => i % 3 == 0 ? "true" : "false"))}} };
                int set = 0;
                foreach (bool flag in manyFlags) if (flag) set++;
                Console.WriteLine(set);
            }
        }
        """;

    public const string Callees = """
        using System;

        class Program
        {
            static int Dot(int[] w, int[] f)
            {
                int sum = 0;
                for (int i = 0; i < w.Length; i++) sum += w[i] * f[i];
                return sum;
            }

            static void Fill(int[] values, int start)
            {
                for (int i = 0; i < values.Length; i++) values[i] = start + i;
            }

            // three levels deep: each passes the caller's array on
            static int Outer(int[] values) => Middle(values) + values.Length;

            static int Middle(int[] values)
            {
                Fill(values, 100);
                return Inner(values, values.Length - 1);
            }

            static int Inner(int[] values, int index) => values[index];

            static int SumFrom(int[] values, int index) => index == values.Length ? 0 : values[index] + SumFrom(values, index + 1);

            static int Swap(int[] values)
            {
                values = new int[] { 7, 8, 9 };
                return values.Length * values[2];
            }

            static int Count(params int[] values) => values.Length;

            static void Main()
            {
                Console.WriteLine(Dot(new[] { 5, -2 }, new[] { 3, 4 }));

                int[] small = new int[3];
                int[] large = new int[8];
                Console.WriteLine(Outer(small));
                Console.WriteLine(Outer(large));
                Console.WriteLine(small[2] + large[7]);

                Console.WriteLine(SumFrom(large, 0));
                Console.WriteLine(Swap(small));
                Console.WriteLine(small[0]);
                Console.WriteLine(Count());
                Console.WriteLine(Count(4, 5, 6));
            }
        }
        """;

    public const string Loops = """
        using System;

        class Program
        {
            static void Sort(int[] values)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    for (int j = 0; j + 1 < values.Length - i; j++)
                    {
                        if (values[j] > values[j + 1])
                        {
                            int swap = values[j];
                            values[j] = values[j + 1];
                            values[j + 1] = swap;
                        }
                    }
                }
            }

            static void Main()
            {
                int[] values = { 42, -7, 19, 0, 3, 3, 100, -50 };
                Sort(values);
                foreach (int value in values) Console.WriteLine(value);

                int[] squares = new int[20];
                for (int i = 0; i < squares.Length; i++) squares[i] = i * i;
                long total = 0;
                foreach (int square in squares) total += square;
                Console.WriteLine(total);

                // a new array each time round starts at zero, even though its memory is the one of the time before
                int sum = 0;
                for (int round = 0; round < 5; round++)
                {
                    int[] counts = new int[3];
                    for (int i = 0; i <= round; i++) counts[i % 3]++;
                    sum = sum * 10 + counts[0];
                }

                Console.WriteLine(sum);
            }
        }
        """;

    public const string Indexes = """
        using System;

        class Program
        {
            static int ByLong(int[] values, long index) => values[index];
            static int ByULong(int[] values, ulong index) => values[index];
            static int ByUInt(int[] values, uint index) => values[index];
            static int ByNative(int[] values, nint index) => values[index];

            static void Main()
            {
                int[] values = { 10, 20, 30, 40, 50 };
                long index = 4;
                Console.WriteLine(ByLong(values, index));
                Console.WriteLine(ByULong(values, 3));
                Console.WriteLine(ByUInt(values, 2));
                Console.WriteLine(ByNative(values, 1));
                long[] longs = new long[3];
                for (long i = 0; i < longs.Length; i++) longs[i] = i * 3000000000;
                Console.WriteLine(longs[2]);
            }
        }
        """;

    public const string BigArrays = """
        using System;

        class Program
        {
            // a frame of 40 KB: larger than a page, so the PC probes the stack (__chkstk), and zeroed with memset
            static long SquaresUpTo(int count)
            {
                int[] squares = new int[10000];
                for (int i = 0; i < count; i++) squares[i] = i * i;
                long sum = 0;
                foreach (int square in squares) sum += square;
                return sum;
            }

            static void Main()
            {
                Console.WriteLine(SquaresUpTo(10000));
                Console.WriteLine(SquaresUpTo(10));

                long[] longs = new long[3000];
                longs[2999] = 5;
                Console.WriteLine(longs[0] + longs[2999] + longs.Length);
            }
        }
        """;

    /// <summary>Programs whose index is outside the array, and what they print before it.</summary>
    public static IReadOnlyList<(string Source, string Printed)> OutOfRange { get; } =
    [
        ("""
        using System;

        class Program
        {
            static int Last(int[] values) => values[values.Length];

            static void Main()
            {
                Console.WriteLine(1);
                Console.WriteLine(Last(new int[3]));
                Console.WriteLine(2);
            }
        }
        """, "1\r\n"),
        ("""
        using System;

        class Program
        {
            static void Main()
            {
                int[] values = { 1, 2, 3 };
                for (int i = 2; i > -5; i--)
                {
                    values[i] = 7;
                    Console.WriteLine(i);
                }
            }
        }
        """, "2\r\n1\r\n0\r\n"),
        ("""
        using System;

        class Program
        {
            static long At(long[] values, long index) => values[index];

            static void Main()
            {
                long[] values = new long[2];
                Console.WriteLine(At(values, 1));
                Console.WriteLine(At(values, 4294967296));
            }
        }
        """, "0\r\n"),
    ];

    private static string Numbers(int count, Func<int, long> value) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => value(i).ToString(CultureInfo.InvariantCulture)));
}
