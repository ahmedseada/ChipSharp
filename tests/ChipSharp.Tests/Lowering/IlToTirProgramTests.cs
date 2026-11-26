using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// C# programs compiled by ChipSharp for the PC and run: each must print what the same assembly prints on real
/// .NET, byte for byte, and end with the same exit code.
/// </summary>
public sealed class IlToTirProgramTests : IDisposable
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
    public Task Small_integers_and_conversions_wrap_and_extend_like_dotnet() => PrintsWhatDotnetPrints("""
        using System;

        class Program
        {
            static sbyte ToSByte(int x) => (sbyte)x;
            static byte ToByte(int x) => (byte)x;
            static short ToShort(long x) => (short)x;
            static ushort ToUShort(int x) => (ushort)x;
            static int Mix(sbyte a, byte b, short c, ushort d) => a + b + c + d;

            static void Main()
            {
                Console.WriteLine(ToSByte(200));
                Console.WriteLine(ToByte(-1));
                Console.WriteLine(ToShort(100000L));
                Console.WriteLine(ToUShort(-2));
                Console.WriteLine(Mix(ToSByte(-5), ToByte(250), ToShort(-300), ToUShort(65000)));

                int big = 1000;
                long wide = big * 3_000_000L;
                Console.WriteLine(wide);
                Console.WriteLine((int)wide);
                Console.WriteLine((uint)wide);
                long minusOne = -1;
                Console.WriteLine((ulong)minusOne);
                uint u = 4_000_000_000;
                Console.WriteLine((long)u);
                Console.WriteLine((int)u);

                byte counter = 250;
                for (int i = 0; i < 10; i++)
                {
                    counter++;
                }

                Console.WriteLine(counter);
                sbyte small = 127;
                small++;
                Console.WriteLine(small);
                short s = -1;
                Console.WriteLine((ushort)s);
                Console.WriteLine(s >> 1);
                Console.WriteLine((byte)s >> 1);
            }
        }
        """);

    [HostFact]
    public Task Native_ints_compute_like_dotnet_on_a_64_bit_PC() => PrintsWhatDotnetPrints("""
        using System;

        class Program
        {
            static nint Scale(nint n, int factor) => n * factor;

            static void Main()
            {
                nint n = -7;
                Console.WriteLine((long)Scale(n, 3));
                nuint m = 7;
                Console.WriteLine((ulong)(m * 1_000_000_000_000));
                int negative = -1;
                Console.WriteLine((ulong)(nuint)negative);
                nint big = (nint)5_000_000_000L;
                Console.WriteLine((long)(big + 1));
                Console.WriteLine(n < 0);
                Console.WriteLine((ulong)(nuint)n > 5);
            }
        }
        """);

    [HostFact]
    public Task Switches_dense_sparse_and_on_long_pick_the_right_case() => PrintsWhatDotnetPrints("""
        using System;

        class Program
        {
            static int Days(int month)
            {
                switch (month)
                {
                    case 1: case 3: case 5: case 7: case 8: case 10: case 12:
                        return 31;
                    case 2:
                        return 28;
                    case 4: case 6: case 9: case 11:
                        return 30;
                    default:
                        return -1;
                }
            }

            static int Sparse(int x) => x switch { 1 => 10, 100 => 20, 1000 => 30, -5 => 40, _ => 0 };

            static long OnLong(long x) => x switch { 0 => 1, 1 => 2, 2 => 3, 3 => 5, _ => 8 };

            static void Main()
            {
                for (int month = -1; month <= 13; month++)
                {
                    Console.WriteLine(Days(month));
                }

                Console.WriteLine(Sparse(1));
                Console.WriteLine(Sparse(100));
                Console.WriteLine(Sparse(1000));
                Console.WriteLine(Sparse(-5));
                Console.WriteLine(Sparse(7));
                for (long x = -1; x <= 4; x++)
                {
                    Console.WriteLine(OnLong(x));
                }
            }
        }
        """);

    [HostFact]
    public Task Loops_that_assign_their_parameters_count_like_dotnet() => PrintsWhatDotnetPrints("""
        using System;

        class Program
        {
            static int DigitSum(int n)
            {
                int sum = 0;
                while (n > 0)
                {
                    sum += n % 10;
                    n /= 10;
                }

                return sum;
            }

            static int CollatzSteps(long n)
            {
                int steps = 0;
                while (n != 1)
                {
                    n = n % 2 == 0 ? n / 2 : 3 * n + 1;
                    steps++;
                }

                return steps;
            }

            static uint Reverse(uint x)
            {
                uint result = 0;
                for (int i = 0; i < 32; i++)
                {
                    result = (result << 1) | (x & 1);
                    x >>= 1;
                }

                return result;
            }

            static int CountDown(int n)
            {
                do
                {
                    n -= 3;
                }
                while (n > 0);

                return n;
            }

            static void Main()
            {
                Console.WriteLine(DigitSum(987654321));
                Console.WriteLine(CollatzSteps(27));
                Console.WriteLine(CollatzSteps(837799));
                Console.WriteLine(Reverse(1));
                Console.WriteLine(Reverse(0xF0F0F0F0));
                Console.WriteLine(CountDown(10));
                Console.WriteLine(CountDown(-4));
            }
        }
        """);

    [HostFact]
    public Task Unsigned_comparisons_and_branches_read_the_bits_as_unsigned() => PrintsWhatDotnetPrints("""
        using System;

        class Program
        {
            static bool Less(uint a, uint b) => a < b;
            static bool AtLeast(ulong a, ulong b) => a >= b;

            static int Classify(ulong x)
            {
                if (x > 1000)
                {
                    return 1;
                }

                if (x >= 10)
                {
                    return 2;
                }

                return x <= 3 ? 3 : 4;
            }

            static void Main()
            {
                Console.WriteLine(Less(1, 4_000_000_000));
                Console.WriteLine(Less(4_000_000_000, 1));
                Console.WriteLine(AtLeast(ulong.MaxValue, 1));
                Console.WriteLine(AtLeast(5, 5));
                Console.WriteLine(Classify(ulong.MaxValue));
                Console.WriteLine(Classify(10));
                Console.WriteLine(Classify(3));
                Console.WriteLine(Classify(7));
                uint big = 3_000_000_000;
                Console.WriteLine(big / 7 + big % 7);
                Console.WriteLine(big > 2_000_000_000 ? big - 1 : big + 1);
            }
        }
        """);

    [HostFact]
    public Task Nested_conditionals_keep_the_values_under_them() => PrintsWhatDotnetPrints("""
        using System;

        class Program
        {
            static int Pick(int a, int b, int c) => a + (b > 0 ? (c > 0 ? 1 : 2) : c < -5 ? 3 : 4) * 10;

            static bool Check(int id, bool answer)
            {
                Console.WriteLine(id);
                return answer;
            }

            static void Main()
            {
                Console.WriteLine(Pick(100, 1, 1));
                Console.WriteLine(Pick(100, 1, -1));
                Console.WriteLine(Pick(100, -1, -9));
                Console.WriteLine(Pick(100, -1, 0));
                Console.WriteLine(Pick(1, 2, 3) + Pick(4, -5, -6) * (Check(1, true) ? 2 : 3));
                bool both = Check(2, true) && (Check(3, false) || Check(4, true));
                Console.WriteLine(both);
                Console.WriteLine(!both || Check(5, false));
                long total = 0;
                for (int i = 0; i < 10; i++)
                {
                    total += i % 3 == 0 ? i : -i;
                }

                Console.WriteLine(total);
            }
        }
        """);

    [HostFact]
    public Task Top_level_statements_with_local_functions_run_without_their_args() => PrintsWhatDotnetPrints("""
        using System;

        int Twice(int x) => x * 2;
        bool IsEven(int x) => x % 2 == 0;

        for (int i = 0; i < 4; i++)
        {
            Console.WriteLine(Twice(i));
            Console.WriteLine(IsEven(i));
        }
        """);

    [HostFact]
    public Task The_int_Main_returns_is_the_exit_code() => PrintsWhatDotnetPrints("""
        using System;

        class Program
        {
            static int Main()
            {
                Console.WriteLine(5);
                return 42;
            }
        }
        """);

    /// <summary>Compiles <paramref name="source"/>, runs it on .NET and as a ChipSharp program, and compares.</summary>
    private async Task PrintsWhatDotnetPrints(string source)
    {
        var assembly = CSharpCompiler.Compile(source, "Program");
        var dotnet = await HostRun.RunOnDotnetAsync(assembly, "Program", Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(Lowered.Program(IlAssembly.Load(assembly, "Program.dll")));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.Output, chipSharp.Output);
        Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
    }
}
