using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Host;

/// <summary>
/// C# float and double math compiled by ChipSharp for the PC prints exactly what .NET prints: every operation, for
/// the special values with each other and for random ones (see <see cref="FloatMathProgram"/>).
/// </summary>
public sealed class FloatMathTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [HostFact]
    public async Task Tens_of_thousands_of_float_and_double_results_match_dotnet_on_the_PC()
    {
        var program = new FloatMathProgram()
            .Double([.. FloatMathProgram.SpecialDoubles, .. FloatMathProgram.RandomDoubles(600, 10)])
            .Float([.. FloatMathProgram.SpecialFloats, .. FloatMathProgram.RandomFloats(600, 11)])
            .Doubles(FloatMathProgram.AllPairs(FloatMathProgram.SpecialDoubles))
            .Doubles(FloatMathProgram.Pairs(FloatMathProgram.RandomDoubles(1200, 12)))
            .Floats(FloatMathProgram.AllPairs(FloatMathProgram.SpecialFloats))
            .Floats(FloatMathProgram.Pairs(FloatMathProgram.RandomFloats(1200, 13)))
            .Mixed(FloatMathProgram.SpecialFloats.Zip(FloatMathProgram.SpecialDoubles.Reverse()))
            .Mixed(FloatMathProgram.RandomFloats(300, 14).Zip(FloatMathProgram.RandomDoubles(300, 15)))
            .Int32([.. FloatMathProgram.SpecialInt32s, .. FloatMathProgram.RandomInt64s(300, 16).Select(l => (int)l)])
            .Int64([.. FloatMathProgram.SpecialInt64s, .. FloatMathProgram.RandomInt64s(300, 17)]);
        var assembly = CSharpCompiler.Compile(program.Source(), "FloatMath");

        var dotnet = await HostRun.RunOnDotnetAsync(assembly, "FloatMath", Path.Combine(_scratch, "dotnet"));
        var module = IlToTir.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "FloatMath.dll")));
        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "host"));

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.True(program.Count > 100_000, $"{program.Count} values");
        program.AssertSameOutput(dotnet.Text, run.Text);
    }
}
