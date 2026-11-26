using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>Compiles C# with Roslyn and runs the whole-program pass on it; the assembly is called Test.dll in messages.</summary>
internal static class WholeProgram
{
    public static IlProgram Analyze(string source) =>
        WholeProgramPass.Analyze(IlAssembly.Load(CSharpCompiler.Compile(source), "Test.dll"));

    /// <summary>The display names of the program's methods, in the pass's order.</summary>
    public static string[] MethodNames(string source) => [.. Analyze(source).Methods.Select(m => m.DisplayName)];

    public static ChipSharpException Error(string source) => Assert.Throws<ChipSharpException>(() => Analyze(source));

    /// <summary>The problem lines of the error, without the heading and the indent.</summary>
    public static string[] Problems(string source)
    {
        var lines = Error(source).Message.Split('\n');
        Assert.StartsWith("Test.dll uses C# that M0 does not compile yet (", lines[0]);
        Assert.All(lines.Skip(1), line => Assert.StartsWith("  ", line));
        return [.. lines.Skip(1).Select(line => line[2..])];
    }
}
