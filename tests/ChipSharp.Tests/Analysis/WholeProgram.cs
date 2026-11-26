using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>Compiles C# with Roslyn and runs the whole-program pass on it; the assembly is called Test.dll in messages.</summary>
internal static class WholeProgram
{
    /// <summary>
    /// The lines a summary ends with, before how to see every place, when the program calls a library method ChipSharp
    /// does not have.
    /// </summary>
    public static readonly string LibraryCalls = """
        ChipSharp can call these library methods (--details lists each one with its parameters):
          Console.WriteLine of (nothing), int, uint, long, ulong, bool, float, double
          Math and MathF: Abs, Min, Max, Sqrt
          TimeSpan: FromTicks, FromDays, FromHours, FromMinutes, FromSeconds, FromMilliseconds, FromMicroseconds, Add, Subtract, Negate, Multiply, Divide, Duration, Compare, CompareTo, Equals, its constructors, properties and operators
          ChipSharp.Hardware: Pin, Timing, Registers (see the README)
        """.ReplaceLineEndings("\n");

    /// <summary>The lines the details end with when the program calls a library method ChipSharp does not have.</summary>
    public static readonly string LibraryCallDetails = """
        ChipSharp can call these library methods:
          System.Console: WriteLine(), WriteLine(int), WriteLine(uint), WriteLine(long), WriteLine(ulong), WriteLine(bool), WriteLine(float), WriteLine(double)
          System.Math: Abs(double), Abs(float), Min(double, double), Min(float, float), Max(double, double), Max(float, float), Sqrt(double)
          System.MathF: Abs(float), Min(float, float), Max(float, float), Sqrt(float)
          System.TimeSpan: Zero, MaxValue, MinValue, new TimeSpan(long), new TimeSpan(int, int, int), new TimeSpan(int, int, int, int), new TimeSpan(int, int, int, int, int), new TimeSpan(int, int, int, int, int, int), Ticks, Days, Hours, Minutes, Seconds, Milliseconds, Microseconds, Nanoseconds, TotalDays, TotalHours, TotalMinutes, TotalSeconds, TotalMilliseconds, TotalMicroseconds, TotalNanoseconds, FromTicks(long), FromDays(int), FromHours(int), FromMinutes(long), FromSeconds(long), FromMilliseconds(long), FromMicroseconds(long), FromDays(double), FromHours(double), FromMinutes(double), FromSeconds(double), FromMilliseconds(double), FromMicroseconds(double), Add(System.TimeSpan), Subtract(System.TimeSpan), Negate(), Multiply(double), Divide(double), Divide(System.TimeSpan), Duration(), Compare(System.TimeSpan, System.TimeSpan), CompareTo(System.TimeSpan), Equals(System.TimeSpan), Equals(System.TimeSpan, System.TimeSpan), operator -(System.TimeSpan), operator -(System.TimeSpan, System.TimeSpan), operator +(System.TimeSpan), operator +(System.TimeSpan, System.TimeSpan), operator *(System.TimeSpan, double), operator *(double, System.TimeSpan), operator /(System.TimeSpan, double), operator /(System.TimeSpan, System.TimeSpan), operator ==(System.TimeSpan, System.TimeSpan), operator !=(System.TimeSpan, System.TimeSpan), operator <(System.TimeSpan, System.TimeSpan), operator <=(System.TimeSpan, System.TimeSpan), operator >(System.TimeSpan, System.TimeSpan), operator >=(System.TimeSpan, System.TimeSpan)
          ChipSharp.Hardware.Pin: Output(int), Input(int), Write(int, bool), Toggle(int), Read(int), Watch(int), WatchRising(int), WatchFalling(int), Unwatch(int)
          ChipSharp.Hardware.Timing: DelayMilliseconds(int), DelayMicroseconds(int), EveryMilliseconds(int), EveryMicroseconds(int), AfterMilliseconds(int), AfterMicroseconds(int), Stop(), Delay(System.TimeSpan)
          ChipSharp.Hardware.Registers: Read(uint), Write(uint, uint)
        """.ReplaceLineEndings("\n");

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
