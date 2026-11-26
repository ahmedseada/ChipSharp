using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>Runs the real <c>chipsharp check</c> on compiled programs.</summary>
public sealed class CheckCliTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public async Task Check_lists_the_methods_of_a_supported_program()
    {
        var dll = CSharpCompiler.CompileToFile("""
            using System;

            class Program
            {
                static int Unused() => 0;
                static int Square(int n) => n * n;
                static bool IsBig(long n) => n > 1000;

                static void Main()
                {
                    Console.WriteLine(Square(42));
                    Console.WriteLine(IsBig(Square(42)));
                }
            }
            """, Path.Combine(_scratch, "Square.dll"));

        var (exitCode, output, error) = await ChipSharpCli.RunAsync("check", dll);

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        Assert.Equal($"""
            {dll}: supported by M0. The program uses 3 methods, entry point first:
              Program.Main()
              Program.Square(int)
              Program.IsBig(long)

            """.ReplaceLineEndings("\n"), output.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Check_lists_the_problems_of_a_program_M0_cannot_compile()
    {
        var dll = CSharpCompiler.CompileToFile("""
            using System;

            class Program
            {
                static double Half(int n) => n / 2.0;

                static void Main()
                {
                    Console.WriteLine((int)Half(7));
                    Console.WriteLine(Math.Abs(-3));
                }
            }
            """, Path.Combine(_scratch, "Half.dll"));

        var (exitCode, output, error) = await ChipSharpCli.RunAsync("check", dll);

        Assert.Equal("", output);
        Assert.Equal(1, exitCode);
        Assert.Equal($"""
            error: {dll} uses C# that M0 does not compile yet (4 problems):
              Program.Main() at IL_000e (call): the library method System.Math.Abs(int) is not available in M0. M0 supports these library calls: System.Console.WriteLine(), System.Console.WriteLine(int), System.Console.WriteLine(uint), System.Console.WriteLine(long), System.Console.WriteLine(ulong), System.Console.WriteLine(bool), ChipSharp.Hardware.Pin.Output(int), ChipSharp.Hardware.Pin.Input(int), ChipSharp.Hardware.Pin.Write(int, bool), ChipSharp.Hardware.Pin.Toggle(int), ChipSharp.Hardware.Pin.Read(int), ChipSharp.Hardware.Timing.DelayMilliseconds(int), ChipSharp.Hardware.Timing.DelayMicroseconds(int), ChipSharp.Hardware.Registers.Read(uint), ChipSharp.Hardware.Registers.Write(uint, uint).
              Program.Half(int), return type (double): floating point (float/double) arrives after M0. For now, use whole numbers: int, long and the other integer types.
              Program.Half(int) at IL_0001 (conv.r8): floating point (float/double) arrives after M0. For now, use whole numbers: int, long and the other integer types.
              Program.Half(int) at IL_0002 (ldc.r8): floating point (float/double) arrives after M0. For now, use whole numbers: int, long and the other integer types.

            """.ReplaceLineEndings("\n"), error.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Check_reports_a_library_and_a_missing_file()
    {
        var dll = CSharpCompiler.CompileToFile("""
            public static class Library
            {
                public static int Square(int n) => n * n;
            }
            """, Path.Combine(_scratch, "Library.dll"));

        var (exitCode, _, error) = await ChipSharpCli.RunAsync("check", dll);
        Assert.Equal(1, exitCode);
        Assert.Equal($"error: {dll} has no entry point: a program needs a static void Main() (or top-level statements) to start from.",
            error.TrimEnd());

        (exitCode, _, error) = await ChipSharpCli.RunAsync("check", Path.Combine(_scratch, "missing.dll"));
        Assert.Equal(1, exitCode);
        Assert.StartsWith("error: Cannot read", error);
    }

    [Fact]
    public async Task The_usage_shows_check()
    {
        var (exitCode, output, _) = await ChipSharpCli.RunAsync("check");

        Assert.Equal(2, exitCode);
        Assert.Contains("chipsharp check <assembly.dll>", output);
    }
}
