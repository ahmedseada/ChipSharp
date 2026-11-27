using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// System.Math and System.MathF on the Arduino Uno, one function a program (each with the soft float it needs is up to
/// most of the 32 KB of flash): what it prints for special and random values in the simulator are the very bytes the PC
/// prints for the same C#. The functions are the shared runtime's, computed with + - * / and integers, each step
/// rounded as IEEE 754 says, so the AVR's soft float and the PC's SSE2 get the same bits; here, where C's int has 16
/// bits and the tables stay in flash, as the Uno has 2 KB of RAM. A program that calls none of these needs none of it.
/// </summary>
public sealed class UnoMathTests : IDisposable
{
    private const int RandomValues = 40;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>Every function of Math and MathF the runtime's transcendental C has, as "Math.Sin" or "MathF.Pow".</summary>
    public static TheoryData<string> Functions =>
    [
        .. new[]
        {
            "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2", "Sinh", "Cosh", "Tanh", "Asinh", "Acosh", "Atanh", "Exp",
            "Log", "Log2", "Log10", "Pow", "Cbrt",
        }.SelectMany(name => new[] { "Math." + name, "MathF." + name }),
    ];

    /// <summary>
    /// The functions too large for the Uno in a program that prints doubles: their range reductions and tables with
    /// compiler-rt's double add, multiply and divide (3 to 4 KB each on an AVR) and the text of floats need more than
    /// its 31.5 KB. They give the PC's bits on an AVR all the same (<see cref="AvrMathTests"/>, on a larger core).
    /// </summary>
    private static readonly HashSet<string> TooLarge = ["Sin", "Cos", "Tan", "Atan2", "Asinh", "Acosh", "Log", "Log2", "Pow"];

    [AvrSimulatorTheory]
    [MemberData(nameof(Functions))]
    public async Task Each_function_prints_on_the_Uno_the_bytes_it_prints_on_the_PC_or_does_not_fit(string function)
    {
        var name = function.Replace('.', '_');
        var assembly = CSharpCompiler.Compile(Program(function), name);
        if (TooLarge.Contains(function[(function.IndexOf('.', StringComparison.Ordinal) + 1)..]))
        {
            var error = Assert.Throws<ChipSharpException>(() => UnoBuild.Build(assembly, name, Path.Combine(_scratch, "uno")));
            Assert.Matches(@"does not fit in target 'uno-console': its code, constants and the starting values of its " +
                @"variables need [0-9.]+ KB of flash, [0-9.]+ (KB|bytes) more than the target's 31\.5 KB", error.Message);
            return;
        }

        var pc = await HostRun.BuildAndRunAsync(IlToTir.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, name + ".dll"))),
            Path.Combine(_scratch, "pc"));
        var uno = UnoBuild.Build(assembly, name, Path.Combine(_scratch, "uno"));

        var run = await AvrSimulator.RunAsync(uno.Hex, 120_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("the program has finished", run.Stopped);
        Assert.Equal(0, pc.ExitCode);
        Assert.True(pc.Text.Split('\n').Length > RandomValues, "The program prints too little to test much.");
        Assert.Equal(pc.Text, Encoding.ASCII.GetString(run.Usart0));
        Assert.True(uno.Memory.Data!.Used < 256, $"RAM: {uno.Memory.Text}");
    }

    /// <summary>
    /// A program that prints <paramref name="function"/> of each value of a table (a pair for Atan2 and Pow): the special
    /// values, then random ones of every range (see <see cref="MathProgram"/>), read from a constant table in flash.
    /// </summary>
    private static string Program(string function)
    {
        var isFloat = function.StartsWith("MathF.", StringComparison.Ordinal);
        var binary = function.EndsWith("Atan2", StringComparison.Ordinal) || function.EndsWith("Pow", StringComparison.Ordinal);
        var (type, suffix) = isFloat ? ("float", "f") : ("double", "");
        string[] specials = isFloat
            ? ["0f", "-0f", "1f", "-1f", "0.5f", "-0.5f", "2f", "10f", "100f", "1e-10f", "1e10f", "3.14159265f", "-2.5f",
                "1e38f", "-1e38f", "1e-45f", "float.NaN", "float.PositiveInfinity", "float.NegativeInfinity"]
            : ["0", "-0.0", "1", "-1", "0.5", "-0.5", "2", "10", "100", "1e-10", "1e10", "Math.PI", "-2.5", "1e300", "-1e300",
                "5e-324", "1e22", "double.NaN", "double.PositiveInfinity", "double.NegativeInfinity"];
        var randoms = isFloat
            ? MathProgram.RandomFloats(RandomValues, function.Length).Where(float.IsFinite)
                .Select(value => value.ToString("R", CultureInfo.InvariantCulture) + "f")
            : MathProgram.RandomDoubles(RandomValues, function.Length).Where(double.IsFinite)
                .Select(value => value.ToString("R", CultureInfo.InvariantCulture));
        var call = binary ? $"{function}(a, b)" : $"{function}(a)";
        return $$"""
            using System;

            class Program
            {
                static readonly {{type}}[] Values = { {{string.Join(", ", specials.Concat(randoms))}} };

                static void Main()
                {
                    {{type}} b = 0.75{{suffix}};
                    foreach (var a in Values)
                    {
                        Console.WriteLine({{call}});
                        b = a;
                    }
                }
            }
            """;
    }
}
