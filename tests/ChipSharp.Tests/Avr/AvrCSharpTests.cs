using ChipSharp.Build;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// C# built for the Arduino Uno the whole way, as <c>chipsharp build -t uno</c> builds it (the code generator, the
/// toolchain "zig", the runtime "avr", the .hex), and run: in the avr8js simulator (tools/avr-sim), where it finishes,
/// or stops at BREAK when it fails; and on the C# AVR core (<see cref="AvrCpu"/>), where Main's result is .NET's. The
/// program needs every family of the runtime's helpers: 64- and 32-bit multiplication and division, double and float
/// arithmetic and conversions, a table in flash, a struct.
/// </summary>
public sealed class AvrCSharpTests : IDisposable
{
    private const string Computes = """
        using System;

        class Program
        {
            static readonly int[] Primes = { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29 };

            struct Point
            {
                public long X;
                public double Y;
            }

            static int Main() => Result();

            public static int Result()
            {
                long factorial = 1;
                for (int i = 1; i <= 20; i++)
                {
                    factorial *= i;
                }

                long quotient = factorial / 1234567, remainder = factorial % -1234567;
                double harmonic = 0;
                for (int i = 1; i <= 50; i++)
                {
                    harmonic += 1.0 / i;
                }

                float scaled = (float)harmonic * 3.5f;
                int sum = 0;
                foreach (var prime in Primes)
                {
                    sum += prime * prime;
                }

                var point = new Point { X = quotient, Y = harmonic };
                uint big = 4000000000u - (uint)sum;
                int result = (int)(point.X ^ remainder) ^ (int)(point.Y * 1e6) ^ (int)(scaled * 1000) ^ sum;
                result ^= (int)(big / 7u) ^ (int)(big % 13u) ^ (int)(ulong)(point.Y * 1e15) ^ (int)Math.Round(harmonic * 77);
                return result;
            }
        }
        """;

    private const string Fails = """
        using System;

        class Program
        {
            static int index = 3;

            static int Main()
            {
                Span<int> values = stackalloc int[3];
                values[index] = 1;
                return values[0];
            }
        }
        """;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [AvrSimulatorFact]
    public async Task A_program_that_computes_gets_dotnets_result_and_finishes()
    {
        var (hex, elf) = Build("Computes", Computes);

        var run = await AvrSimulator.RunAsync(hex, 2000);
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("the program has finished", run.Stopped);

        var program = AvrCoreProgram.FromElf(elf);
        var cpu = program.Load();
        Assert.Equal(AvrCpu.StopReason.Sleep, cpu.Run(50_000_000));
        var result = BitConverter.ToInt32(cpu.Data, program.Address("chipsharp_exit_code"));
        Assert.Equal(Dotnet(), result);
    }

    [AvrSimulatorFact]
    public async Task A_program_that_fails_stops_at_BREAK()
    {
        var (hex, _) = Build("Fails", Fails);

        var run = await AvrSimulator.RunAsync(hex, 2000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("breakpoint", run.Stopped);
    }

    /// <summary>What .NET's Main returns: the same C#, compiled and run here.</summary>
    private int Dotnet()
    {
        var dll = CSharpCompiler.CompileToFile(Computes, Path.Combine(_scratch, "Dotnet.dll"));
        var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(dll));
        return (int)assembly.GetType("Program")!.GetMethod("Result")!.Invoke(null, null)!;
    }

    /// <summary>The program built for the Uno: its .hex, and the .elf it was made from.</summary>
    private (string Hex, string Elf) Build(string name, string source)
    {
        var dll = CSharpCompiler.CompileToFile(source, Path.Combine(_scratch, name + ".dll"));
        var output = Path.Combine(_scratch, "output");
        var result = ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = "uno",
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = output,
            KeepIntermediates = true,
            UseCache = false,
        });
        var elf = Assert.Single(result.IntermediatePaths, path => path.EndsWith(".elf", StringComparison.Ordinal));
        return (result.OutputPath, elf);
    }
}
