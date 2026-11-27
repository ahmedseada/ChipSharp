using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Host;

/// <summary>
/// Programs that use System.Math and System.MathF, compiled by ChipSharp for the PC and run next to .NET (see
/// <see cref="MathProgram"/>): every member of doubles, floats and whole numbers, generic math, and Round by every
/// MidpointRounding to every number of digits print what .NET prints, but for the last bits of a few transcendental
/// results; where .NET throws, the program stops with .NET's message.
/// </summary>
public sealed class MathProgramTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>
    /// The program the PC test runs with <paramref name="random"/> random values of each kind; the Pico test runs it with
    /// fewer, and whole numbers below 2^62 only (the simulator's SBCS fault, see the README's Tests).
    /// </summary>
    internal static MathProgram Program(int random, bool pico = false)
    {
        long[] integers = [.. FloatMathProgram.SpecialInt64s.Where(value => !pico || (value > -(1L << 62) && value < 1L << 62))];
        return new MathProgram()
            .Doubles(FloatMathProgram.SpecialDoubles.Zip(FloatMathProgram.SpecialDoubles.Reverse()))
            .Doubles(MathProgram.RandomDoubles(random, 41).Zip(MathProgram.RandomDoubles(random, 42)))
            .Floats(FloatMathProgram.SpecialFloats.Zip(FloatMathProgram.SpecialFloats.Reverse()))
            .Floats(MathProgram.RandomFloats(random, 43).Zip(MathProgram.RandomFloats(random, 44)))
            .Rounding([.. MathProgram.RoundingValues, .. MathProgram.RandomDoubles(random / 5, 45)])
            .Integers(integers.Zip(integers.Reverse()))
            .Integers(FloatMathProgram.RandomInt64s(random, 46).Zip(FloatMathProgram.RandomInt64s(random, 47))
                .Select(pair => pico ? (pair.First >> 2, pair.Second >> 2) : pair));
    }

    [HostFact]
    [RunsLong]
    public async Task Every_member_prints_what_dotnet_prints_but_the_last_bit_of_some_transcendental_results()
    {
        var source = Program(400).Source();
        var assembly = CSharpCompiler.Compile(source, "MathMembers");
        var dotnet = await HostRun.RunOnDotnetAsync(assembly, "MathMembers", Path.Combine(_scratch, "dotnet"));
        var module = IlToTir.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "MathMembers.dll")));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "host"));

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.True(dotnet.Text.Split('\n').Length > 30_000, "The program prints too little to test much.");
        var differing = MathProgram.AssertCloseToDotnet(dotnet.Text, run.Text);
        output.WriteLine(string.Join('\n', differing.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}: {pair.Value} lines")));
        Assert.All(differing.Keys, tag => Assert.Contains(tag[(tag.IndexOf('.', StringComparison.Ordinal) + 1)..],
            MathProgram.Transcendental.Keys));
    }

    /// <summary>
    /// What .NET throws and ChipSharp says the same way: Sign of NaN, Clamp given a min above its max (of every type,
    /// with the numbers as .NET writes them), Round given too many digits or an undefined MidpointRounding, Abs of the
    /// lowest value, DivRem by zero and of the lowest value by -1.
    /// </summary>
    public static TheoryData<string, string> Failures => new()
    {
        { "Math.Sign(D(double.NaN))", "System.ArithmeticException: Function does not accept floating point Not-a-Number values." },
        { "MathF.Sign((float)D(double.NaN))", "System.ArithmeticException: Function does not accept floating point Not-a-Number values." },
        { "Math.Clamp(D(2.5), 3.5, 1.25)", "System.ArgumentException: '3.5' cannot be greater than 1.25." },
        { "Math.Clamp((float)D(2.5), 0.1f, -1e30f)", "System.ArgumentException: '0.1' cannot be greater than -1E+30." },
        { "Math.Clamp((int)D(2), 5, 1)", "System.ArgumentException: '5' cannot be greater than 1." },
        { "Math.Clamp((long)D(2), 5000000000L, -9223372036854775807L)", "System.ArgumentException: '5000000000' cannot be greater than -9223372036854775807." },
        { "Math.Clamp((ulong)D(2), 18446744073709551615UL, 1UL)", "System.ArgumentException: '18446744073709551615' cannot be greater than 1." },
        { "Math.Clamp((sbyte)D(2), (sbyte)-1, (sbyte)-128)", "System.ArgumentException: '-1' cannot be greater than -128." },
        { "Math.Clamp((byte)D(2), (byte)200, (byte)100)", "System.ArgumentException: '200' cannot be greater than 100." },
        { "Math.Clamp((nint)D(2), 3, -3)", "System.ArgumentException: '3' cannot be greater than -3." },
        { "Math.Clamp(D(2), double.PositiveInfinity, 0)", "System.ArgumentException: 'Infinity' cannot be greater than 0." },
        { "Math.Round(D(2.5), 16)", "System.ArgumentOutOfRangeException: Rounding digits must be between 0 and 15, inclusive. (Parameter 'digits')" },
        { "MathF.Round((float)D(2.5), -1)", "System.ArgumentOutOfRangeException: Rounding digits must be between 0 and 6, inclusive. (Parameter 'digits')" },
        { "Math.Round(D(2.5), (MidpointRounding)7)", "System.ArgumentException: The value '7' is not valid for this usage of the type MidpointRounding. (Parameter 'mode')" },
        { "Math.Round(D(2.5), 2, (MidpointRounding)(-1))", "System.ArgumentException: The value '-1' is not valid for this usage of the type MidpointRounding. (Parameter 'mode')" },
        { "Math.Abs((int)D(int.MinValue))", "System.OverflowException: Negating the minimum value of a twos complement number is invalid." },
        { "Math.Abs((short)D(short.MinValue))", "System.OverflowException: Negating the minimum value of a twos complement number is invalid." },
        { "Math.DivRem((int)D(5), 0).Quotient", "System.DivideByZeroException: Attempted to divide by zero." },
        { "Math.DivRem((int)D(int.MinValue), -1).Remainder", "System.OverflowException: Arithmetic operation resulted in an overflow." },
    };

    /// <summary>A program that prints 1, then does <paramref name="statement"/> (where D(x) is x, hidden from the compiler).</summary>
    public static string Failing(string statement) => $$"""
        using System;

        class Program
        {
            static double D(double x) => Math.Max(x, x);

            static void Main()
            {
                Console.WriteLine(1);
                Console.WriteLine({{statement}});
            }
        }
        """;

    [HostTheory]
    [MemberData(nameof(Failures))]
    public async Task Where_dotnet_throws_the_program_stops_with_dotnets_message_and_exit_code_1(string statement, string message)
    {
        var source = Failing(statement);
        var assembly = CSharpCompiler.Compile(source, "Failing");
        var dotnet = await HostRun.RunOnDotnetAsync(assembly, "Failing", Path.Combine(_scratch, "dotnet"), failing: true);
        var module = IlToTir.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Failing.dll")));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "host"));

        Assert.Equal($"Unhandled exception. {message}", dotnet.Errors.ReplaceLineEndings("\n").Split('\n')[0]);
        Assert.Equal("1\r\n", run.Text);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal($"Unhandled exception. {message}\r\n", run.Errors);
    }
}
