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
            {dll}: ChipSharp compiles it. The program uses 3 methods, entry point first:
              Program.Main()
              Program.Square(int)
              Program.IsBig(long)

            """.ReplaceLineEndings("\n"), output.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Check_lists_the_CSharp_features_of_a_program_ChipSharp_cannot_compile_yet()
    {
        var dll = CompileHalf();

        var (exitCode, output, error) = await ChipSharpCli.RunAsync("check", dll);

        Assert.Equal("", output);
        Assert.Equal(1, exitCode);
        Assert.Equal($"""
            error: {dll} uses 2 C# features that ChipSharp does not compile yet (2 places):
              the library method int.Abs(int) is not available yet: 1 place in Program.Main() (IL_000e).
              checked arithmetic arrives later: 1 place in Program.Half(int) (IL_000c). For now, leave out checked: ChipSharp's arithmetic wraps around on overflow, like unchecked C#.
            {WholeProgram.LibraryCalls}
            For every place, run: chipsharp check {ChipSharpCli.Quoted(dll)} --details

            """.ReplaceLineEndings("\n"), error.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Check_with_details_lists_every_place_on_a_line_of_its_own()
    {
        var dll = CompileHalf();

        var (exitCode, output, error) = await ChipSharpCli.RunAsync("check", dll, "--details");

        Assert.Equal("", output);
        Assert.Equal(1, exitCode);
        Assert.Equal($"""
            error: {dll} uses 2 C# features that ChipSharp does not compile yet (2 places):
              Program.Main() at IL_000e (call): the library method int.Abs(int) is not available yet.
              Program.Half(int) at IL_000c (conv.ovf.i4): checked arithmetic arrives later. For now, leave out checked: ChipSharp's arithmetic wraps around on overflow, like unchecked C#.
            {WholeProgram.LibraryCallDetails}

            """.ReplaceLineEndings("\n"), error.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Check_with_details_of_a_supported_program_lists_its_methods()
    {
        var dll = CSharpCompiler.CompileToFile("class Program { static void Main() => System.Console.WriteLine(1); }",
            Path.Combine(_scratch, "One.dll"));

        var (exitCode, output, error) = await ChipSharpCli.RunAsync("check", "--details", dll);

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        Assert.StartsWith($"{dll}: ChipSharp compiles it. The program uses 1 method, entry point first:", output);
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
        Assert.Contains("chipsharp check <assembly.dll> [--details]", output);
    }

    private string CompileHalf() => CSharpCompiler.CompileToFile("""
        using System;

        class Program
        {
            static double Half(int n) => checked((int)(n / 2.0));

            static void Main()
            {
                Console.WriteLine((int)Half(7));
                Console.WriteLine(int.Abs(-3));
            }
        }
        """, Path.Combine(_scratch, "Half.dll"));
}
