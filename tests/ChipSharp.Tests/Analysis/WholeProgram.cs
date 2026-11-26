using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>Compiles C# with Roslyn and runs the whole-program pass on it; the assembly is called Test.dll in messages.</summary>
internal static class WholeProgram
{
    /// <summary>The line a message ends with when the program calls a library method M0 does not have.</summary>
    public const string LibraryCalls = "M0 supports these library calls: System.Console.WriteLine(), "
        + "System.Console.WriteLine(int), System.Console.WriteLine(uint), System.Console.WriteLine(long), "
        + "System.Console.WriteLine(ulong), System.Console.WriteLine(bool), System.Console.WriteLine(float), "
        + "System.Console.WriteLine(double), " + MathCalls + ", " + HardwareCalls + ".";

    private const string MathCalls = "System.Math.Abs(double), System.Math.Abs(float), System.Math.Min(double, double), "
        + "System.Math.Min(float, float), System.Math.Max(double, double), System.Math.Max(float, float), "
        + "System.Math.Sqrt(double), System.MathF.Abs(float), System.MathF.Min(float, float), "
        + "System.MathF.Max(float, float), System.MathF.Sqrt(float)";

    private const string HardwareCalls = "ChipSharp.Hardware.Pin.Output(int), ChipSharp.Hardware.Pin.Input(int), "
        + "ChipSharp.Hardware.Pin.Write(int, bool), ChipSharp.Hardware.Pin.Toggle(int), ChipSharp.Hardware.Pin.Read(int), "
        + "ChipSharp.Hardware.Timing.DelayMilliseconds(int), ChipSharp.Hardware.Timing.DelayMicroseconds(int), "
        + "ChipSharp.Hardware.Registers.Read(uint), ChipSharp.Hardware.Registers.Write(uint, uint)";

    public static IlProgram Analyze(string source) => WholeProgramPass.Analyze(Load(source));

    public static AnalysisResult Check(string source) => WholeProgramPass.Check(Load(source));

    /// <summary>The display names of the program's methods, in the pass's order.</summary>
    public static string[] MethodNames(string source) => [.. Analyze(source).Methods.Select(m => m.DisplayName)];

    public static ChipSharpException Error(string source) => Assert.Throws<ChipSharpException>(() => Analyze(source));

    /// <summary>The problems one per place, as the details list them (without the heading and the indent).</summary>
    public static string[] Problems(string source)
    {
        var analysis = Check(source);
        Assert.False(analysis.IsSupported);
        Assert.Equal(analysis.Problems.Count, analysis.Groups.Sum(group => group.Problems.Count));
        return [.. analysis.Problems.Select(problem => problem.Line)];
    }

    private static IlAssembly Load(string source) => IlAssembly.Load(CSharpCompiler.Compile(source), "Test.dll");
}
