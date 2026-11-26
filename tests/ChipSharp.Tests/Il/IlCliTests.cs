using System.Diagnostics;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Il;

/// <summary>Runs the real chipsharp command (copied next to the tests) on a compiled program.</summary>
public sealed class IlCliTests : IDisposable
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
    public async Task Il_lists_one_method()
    {
        var dll = CompileSquareProgram();

        var (exitCode, output, error) = await RunAsync("il", dll, "--method", "Program.Main");

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        Assert.Equal("""
            .method static void Program::Main()
            {
              .entrypoint
              .maxstack 8
              IL_0000:  ldc.i4.s   42
              IL_0002:  call       int32 Program::Square(int32)
              IL_0007:  call       void [System.Console]System.Console::WriteLine(int32)
              IL_000c:  ret
            }

            """.ReplaceLineEndings("\n"), output);
    }

    [Fact]
    public async Task Il_lists_every_method_without_the_option()
    {
        var dll = CompileSquareProgram();

        var (exitCode, output, _) = await RunAsync("il", dll);

        Assert.Equal(0, exitCode);
        Assert.Equal(
        [
            ".method static int32 Program::Square(int32 n)",
            ".method static void Program::Main()",
            ".method instance void Program::.ctor()",
        ], output.Split('\n').Where(line => line.StartsWith(".method", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Il_reports_a_missing_method_and_a_missing_file()
    {
        var dll = CompileSquareProgram();

        var (exitCode, _, error) = await RunAsync("il", dll, "--method", "Program.Cube");
        Assert.Equal(1, exitCode);
        Assert.Contains("has no method named 'Program.Cube'", error);

        (exitCode, _, error) = await RunAsync("il", Path.Combine(_scratch, "missing.dll"));
        Assert.Equal(1, exitCode);
        Assert.StartsWith("error: Cannot read", error);
    }

    [Fact]
    public async Task The_targets_commands_and_the_usage_still_work()
    {
        var (exitCode, output, _) = await RunAsync("targets", "list");
        Assert.Equal(0, exitCode);
        Assert.Contains("pico", output.Split(Environment.NewLine));

        (exitCode, output, _) = await RunAsync("help");
        Assert.Equal(2, exitCode);
        Assert.Contains("chipsharp il <assembly.dll> [--method <name>]", output);
    }

    private string CompileSquareProgram() => CSharpCompiler.CompileToFile("""
        using System;

        class Program
        {
            static int Square(int n) => n * n;

            static void Main()
            {
                Console.WriteLine(Square(42));
            }
        }
        """, Path.Combine(_scratch, "Square.dll"));

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "chipsharp.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }
}
