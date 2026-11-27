using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Whole numbers divided as .NET divides them, on the PC and on the simulated Pico: by a variable that holds 0 the
/// program stops with DivideByZeroException's message, the smallest value divided by -1 with OverflowException's (the
/// remainder too), and every other division gives .NET's result. LLVM's division instructions leave those cases
/// undefined (the PC printed 0 and went on), so ChipSharp checks the divisor first (DivisionFunctions).
/// </summary>
public sealed class DivisionTests : IDisposable
{
    private const string DivideByZero =
        "Unhandled exception. System.DivideByZeroException: Attempted to divide by zero.\r\n";

    private const string Overflow =
        "Unhandled exception. System.OverflowException: Arithmetic operation resulted in an overflow.\r\n";

    /// <summary>Divisions by variables (so C# folds none) of every whole-number type, by -1 too, that .NET answers.</summary>
    private const string Answers = """
        using System;

        class Program
        {
            static int zero, minusOne = -1, seven = 7;
            static long minusOneL = -1, sevenL = 7;
            static uint allOnes = uint.MaxValue, sevenU = 7;
            static ulong allOnesUL = ulong.MaxValue;
            static nint minusOneN = -1;

            static void Main()
            {
                Console.WriteLine(-45 / seven + " " + -45 % seven + " " + 45 / minusOne + " " + 45 % minusOne);
                Console.WriteLine((int.MinValue + 1) / minusOne + " " + int.MinValue / seven + " " + 0 / minusOne);
                Console.WriteLine(long.MinValue / sevenL + " " + (long.MinValue + 1) / minusOneL + " " + long.MaxValue % minusOneL);
                Console.WriteLine(uint.MaxValue / sevenU + " " + 4000000000u / allOnes + " " + uint.MaxValue % allOnes);
                Console.WriteLine(ulong.MaxValue / allOnesUL + " " + 5UL % allOnesUL);
                Console.WriteLine((long)((nint)(-9) / minusOneN) + " " + (short)-32768 / (short)minusOne);
                Console.WriteLine(zero / seven + " " + zero % seven);
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

    /// <summary>A division that .NET throws for, and the message ChipSharp stops with.</summary>
    public static TheoryData<string, string> Failures() => new()
    {
        { "10 / zero", DivideByZero },
        { "10L % zeroL", DivideByZero },
        { "10u / zeroU", DivideByZero },
        { "int.MinValue / minusOne", Overflow },
        { "long.MinValue % minusOneL", Overflow },
    };

    [HostFact]
    public async Task Divisions_by_variables_give_what_dotnet_gives()
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(Answers, "Program"), "Program",
            Path.Combine(_scratch, "dotnet"));

        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var assembly = CSharpCompiler.Compile(Answers, "Program", OutputKind.ConsoleApplication, optimization);
            var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll")));

            var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, optimization.ToString()));

            Assert.Equal(dotnet.Text, run.Text);
            Assert.Equal(0, run.ExitCode);
        }
    }

    [HostTheory]
    [MemberData(nameof(Failures))]
    public async Task A_division_dotnet_throws_for_stops_the_program_with_its_message(string division, string message)
    {
        var assembly = CSharpCompiler.Compile(Failing(division), "Program");
        var dotnet = await HostRun.RunOnDotnetAsync(assembly, "Program", Path.Combine(_scratch, "dotnet"), failing: true);
        var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll")));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.StartsWith(message, dotnet.Errors);
        Assert.Equal(("before\r\n", 1, message), (run.Text, run.ExitCode, run.Errors));
    }

    [SimulatorFact]
    public async Task On_the_Pico_divisions_by_variables_give_what_dotnet_gives()
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(Answers, "Program"), "Program",
            Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(Answers, "Divisions", Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Output, run.Uart0);
    }

    [SimulatorTheory]
    [MemberData(nameof(Failures))]
    public async Task On_the_Pico_a_division_dotnet_throws_for_says_so_on_UART0_and_stops_at_a_breakpoint(string division,
        string message)
    {
        var program = PicoBuild.Build(Failing(division), "Division", Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
        Assert.Equal("before\r\n" + message, System.Text.Encoding.ASCII.GetString(run.Uart0));
    }

    /// <summary>A program that prints "before", then the result of <paramref name="division"/>, which .NET throws for.</summary>
    private static string Failing(string division) => $$"""
        using System;

        class Program
        {
            static int zero, minusOne = -1;
            static long zeroL, minusOneL = -1;
            static uint zeroU;

            static void Main()
            {
                Console.WriteLine("before");
                Console.WriteLine({{division}});
                Console.WriteLine("after");
            }
        }
        """;
}
