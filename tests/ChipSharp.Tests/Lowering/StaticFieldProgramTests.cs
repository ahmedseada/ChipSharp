using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Programs with static fields, compiled by ChipSharp for the PC and run: each must print what the same assembly
/// prints on real .NET. The same programs run on the simulated Pico too (Pico/StaticFieldSimulatorTests).
/// </summary>
public sealed class StaticFieldProgramTests : IDisposable
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
    public Task Counters_keep_their_values_from_call_to_call() => PrintsWhatDotnetPrints(StaticFieldPrograms.Counters);

    [HostFact]
    public Task Field_initializers_with_expressions_run_in_order() => PrintsWhatDotnetPrints(StaticFieldPrograms.Initializers);

    [HostFact]
    public Task Static_readonly_fields_computed_in_a_static_constructor() =>
        PrintsWhatDotnetPrints(StaticFieldPrograms.StaticReadonly);

    [HostFact]
    public Task The_initializers_of_several_classes_run_before_the_ones_that_use_them() =>
        PrintsWhatDotnetPrints(StaticFieldPrograms.InitializerOrder);

    [HostFact]
    public Task Static_constructors_that_use_each_other_see_what_dotnet_sees() =>
        PrintsWhatDotnetPrints(StaticFieldPrograms.Cycle);

    [HostFact]
    public Task Fields_of_every_supported_type_start_at_their_values_and_wrap_like_dotnet() =>
        PrintsWhatDotnetPrints(StaticFieldPrograms.EveryType);

    [HostFact]
    public async Task Every_program_compiled_in_Debug_prints_the_same()
    {
        foreach (var (name, source) in StaticFieldPrograms.All)
        {
            var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Program"), "Program",
                Path.Combine(_scratch, name, "dotnet"));
            var module = Lowered.Lower(Lowered.Program(Lowered.CompileDebug(source, "Program")));

            var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, name, "chipsharp"));

            Assert.True(dotnet.Output.SequenceEqual(chipSharp.Output), $"{name}:\n{dotnet}\n{chipSharp}");
        }
    }

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

/// <summary>C# programs that keep state in static fields, for the PC and the Pico tests.</summary>
internal static class StaticFieldPrograms
{
    public const string Counters = """
        using System;

        class Program
        {
            static int count;
            static long sum;
            static uint hits = 7;

            static int Count() => ++count;

            static void Record(int value)
            {
                sum += value;
                hits++;
            }

            static int Fib(int n)
            {
                count++;
                return n < 2 ? n : Fib(n - 1) + Fib(n - 2);
            }

            static void Main()
            {
                for (int i = 0; i < 4; i++)
                {
                    Console.WriteLine(Count());
                }

                Record(10);
                Record(-3);
                Record(int.MaxValue);
                Console.WriteLine(sum);
                Console.WriteLine(hits);
                count = 0;
                Console.WriteLine(Fib(15));
                Console.WriteLine(count);
            }
        }
        """;

    /// <summary>The initializers run in the order the fields are declared, each seeing the ones before it set.</summary>
    public const string Initializers = """
        using System;

        class Program
        {
            const int Base = 12;
            static int squared = Base * Base;
            static int computed = Square(Base) + 1;
            static long shifted = 1L << (Base + 20);
            static bool big = computed > 100;
            static int next = computed + squared;
            static byte low = (byte)(next * 3);
            static short negative = (short)-next;
            static nuint large = 3_000_000_000;

            static int Square(int x) => x * x;

            static void Main()
            {
                Console.WriteLine(squared);
                Console.WriteLine(computed);
                Console.WriteLine(shifted);
                Console.WriteLine(big);
                Console.WriteLine(next);
                Console.WriteLine(low);
                Console.WriteLine(negative);
                Console.WriteLine((ulong)large);
            }
        }
        """;

    public const string StaticReadonly = """
        using System;

        static class Primes
        {
            public static readonly int Count;
            public static readonly int Largest;
            public static readonly ulong Product;

            static Primes()
            {
                ulong product = 1;
                for (int n = 2; n < 50; n++)
                {
                    bool prime = true;
                    for (int d = 2; d * d <= n; d++)
                    {
                        if (n % d == 0)
                        {
                            prime = false;
                            break;
                        }
                    }

                    if (prime)
                    {
                        Count++;
                        Largest = n;
                        product *= (ulong)n;
                    }
                }

                Product = product;
            }
        }

        class Program
        {
            static void Main()
            {
                Console.WriteLine(Primes.Count);
                Console.WriteLine(Primes.Largest);
                Console.WriteLine(Primes.Product);
            }
        }
        """;

    /// <summary>Program's initializer uses Log's, which uses Timer's, which uses Clock's: they run Clock first.</summary>
    public const string InitializerOrder = """
        using System;

        static class Clock
        {
            public static readonly int TicksPerSecond = 1000;
            public static readonly int TicksPerMinute = TicksPerSecond * 60;
        }

        static class Timer
        {
            public static int Period;

            static Timer()
            {
                Period = Clock.TicksPerMinute / 4;
            }

            public static int Next(int now) => now + Period;
        }

        static class Log
        {
            public static int Lines = Timer.Period + Clock.TicksPerSecond;
        }

        class Program
        {
            static int start = Log.Lines;

            static void Main()
            {
                Console.WriteLine(start);
                Console.WriteLine(Timer.Next(5));
                Console.WriteLine(Log.Lines);
                Console.WriteLine(Clock.TicksPerMinute);
            }
        }
        """;

    /// <summary>A's static constructor uses B, whose static constructor uses A: B sees A's field still zero.</summary>
    public const string Cycle = """
        using System;

        static class A
        {
            public static int X;

            static A()
            {
                X = B.Y + 1;
            }
        }

        static class B
        {
            public static int Y;

            static B()
            {
                Y = A.X + 10;
            }
        }

        class Program
        {
            static void Main()
            {
                Console.WriteLine(A.X);
                Console.WriteLine(B.Y);
            }
        }
        """;

    public const string EveryType = """
        using System;

        class Program
        {
            static bool flag = true;
            static sbyte i8 = -100;
            static byte u8 = 200;
            static short i16 = -30000;
            static ushort u16 = 60000;
            static int i32 = -2_000_000_000;
            static uint u32 = 4_000_000_000;
            static long i64 = long.MinValue + 1;
            static ulong u64 = ulong.MaxValue - 1;
            static nint ni = -123456;
            static nuint nu = 3_000_000;
            static volatile int v = 5;

            static void Step()
            {
                flag = !flag;
                i8 += 100;
                u8 += 100;
                i16 -= 10000;
                u16 += 10000;
                i32 -= 500_000_000;
                u32 += 500_000_000;
                i64 -= 2;
                u64 += 3;
                ni *= 1000;
                nu += 1;
                v = v * 3;
            }

            static void Print()
            {
                Console.WriteLine(flag);
                Console.WriteLine(i8);
                Console.WriteLine(u8);
                Console.WriteLine(i16);
                Console.WriteLine(u16);
                Console.WriteLine(i32);
                Console.WriteLine(u32);
                Console.WriteLine(i64);
                Console.WriteLine(u64);
                Console.WriteLine((long)ni);
                Console.WriteLine((ulong)nu);
                Console.WriteLine(v);
            }

            static void Main()
            {
                Print();
                Step();
                Print();
            }
        }
        """;

    /// <summary>Every program above, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Counters), Counters),
        (nameof(Initializers), Initializers),
        (nameof(StaticReadonly), StaticReadonly),
        (nameof(InitializerOrder), InitializerOrder),
        (nameof(Cycle), Cycle),
        (nameof(EveryType), EveryType),
    ];
}
