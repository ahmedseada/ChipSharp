using ChipSharp.Abstractions;
using ChipSharp.Tests.Analysis;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Build;

/// <summary>Runs the real <c>chipsharp build</c> and <c>chipsharp tir</c>, and the programs build writes.</summary>
public sealed class BuildCliTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [HostFact]
    public async Task Build_writes_programs_that_print_what_dotnet_prints()
    {
        var output = Path.Combine(_scratch, "out");
        foreach (var name in new[] { "Square", "Fibonacci", "Primes" })
        {
            var dll = Samples.Compile(name, _scratch);

            var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-o", output);

            Assert.Equal("", error);
            Assert.Equal(0, exitCode);
            var program = Path.Combine(output, name + ".exe");
            Assert.Equal([program, HostSize(program)], Lines(printed));
            var run = await HostRun.RunAsync(program, [], _scratch);
            Assert.Equal(0, run.ExitCode);
            Assert.Equal(Samples.ExpectedOutput(name), run.Output);
        }
    }

    [HostFact]
    public async Task Build_with_keep_prints_the_program_then_the_files_it_kept()
    {
        var dll = Samples.Compile("Gcd", _scratch);
        var output = Path.Combine(_scratch, "out");

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "--target", "host", "--output", output, "--keep");

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        var files = new[] { Path.Combine(output, "Gcd.exe"), Path.Combine(output, "Gcd.tir"), Path.Combine(output, "Gcd.ll") };
        Assert.Equal([.. files, HostSize(files[0])], Lines(printed));
        Assert.All(files, path => Assert.True(File.Exists(path), path));
    }

    [HostFact]
    public async Task Build_writes_into_chipsharp_output_under_the_current_folder_by_default()
    {
        var dll = Samples.Compile("Square", Path.Combine(_scratch, "dll"));

        var (exitCode, printed, error) = await ChipSharpCli.RunInAsync(_scratch, "build", dll, "-t", "host");

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        var program = Path.Combine(_scratch, "chipsharp-output", "host", "Square.exe");
        Assert.Equal([program, HostSize(program)], Lines(printed));
        Assert.Equal(Samples.ExpectedOutput("Square"), (await HostRun.RunAsync(program, [], _scratch)).Output);
    }

    [Fact]
    public async Task Build_of_a_program_that_builds_text_fails_with_the_summary_check_gives()
    {
        var dll = CSharpCompiler.CompileToFile("""
            using System;

            class Program
            {
                static void Main()
                {
                    int answer = 42;
                    Console.WriteLine(double.Exp10(2.5) + answer);
                    Console.WriteLine(answer);
                    Console.WriteLine(int.Abs(answer).ToString());
                }
            }
            """, Path.Combine(_scratch, "Hello.dll"));

        var check = await ChipSharpCli.RunAsync("check", dll);
        var build = await ChipSharpCli.RunInAsync(_scratch, "build", dll);

        Assert.Equal(1, build.ExitCode);
        Assert.Equal("", build.Output);
        var lines = build.Error.ReplaceLineEndings("\n").TrimEnd().Split('\n');
        Assert.Equal($"error: {dll} uses 2 C# features that ChipSharp does not compile yet (2 places):", lines[0]);
        Assert.Equal(
        [
            "  the library method double.Exp10(double) is not available yet: 1 place in Program.Main() (IL_000c).",
            "  the library method int.Abs(int) is not available yet: 1 place in Program.Main() (IL_0020).",
        ], lines[1..3]);
        var calls = WholeProgram.LibraryCalls.Split('\n');
        Assert.Equal(calls, lines[3..(3 + calls.Length)]);
        Assert.Equal($"For every place, run: chipsharp check {ChipSharpCli.Quoted(dll)} --details", lines[3 + calls.Length]);
        Assert.Equal(4 + calls.Length, lines.Length);
        Assert.Equal(check.Error, build.Error);
        Assert.Equal(check.ExitCode, build.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(_scratch, "chipsharp-output")));
    }

    [Fact]
    public async Task Build_for_an_incomplete_or_unknown_target_fails_with_exit_code_1()
    {
        var dll = Samples.Compile("Square", _scratch);

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "rp2040");
        Assert.Equal(1, exitCode);
        Assert.Equal("", printed);
        Assert.StartsWith("error: Target 'rp2040' is incomplete (memory.flash.size); build a board that inherits it", error);

        (exitCode, _, error) = await ChipSharpCli.RunAsync("build", dll, "--target", "nowhere");
        Assert.Equal(1, exitCode);
        Assert.StartsWith("error: Target 'nowhere' not found in", error);

        (exitCode, _, error) = await ChipSharpCli.RunAsync("build", dll, "-o");
        Assert.Equal(1, exitCode);
        Assert.Equal("error: -o needs a value.", error.TrimEnd());
    }

    [Fact]
    public async Task Tir_prints_the_whole_program_as_TIR()
    {
        var dll = Samples.Compile("Square", _scratch);

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("tir", dll);

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        Assert.Equal("""
            module Square
            entrypoint Program.Main

            function void Program.Main()
            IL_0000:
              %0 = const i32 42
              %1 = call i32 Program.Square(%0)
              call void ChipSharp.Runtime.WriteLineInt32(%1)
              return

            function i32 Program.Square(i32 %n)
            IL_0000:
              %0 = mul i32 %n, %n
              return %0

            external function void ChipSharp.Runtime.WriteLineInt32(i32 %value)

            """.ReplaceLineEndings("\n"), printed);
    }

    [Fact]
    public async Task Tir_with_a_method_prints_only_that_function()
    {
        var dll = Samples.Compile("Square", _scratch);

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("tir", dll, "--method", "Program.Square");

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        Assert.Equal("""
            function i32 Program.Square(i32 %n)
            IL_0000:
              %0 = mul i32 %n, %n
              return %0

            """.ReplaceLineEndings("\n"), printed);
    }

    [Fact]
    public async Task Tir_says_whether_a_method_is_missing_or_only_unused()
    {
        var dll = CSharpCompiler.CompileToFile("""
            using System;

            class Program
            {
                static int Unused(int n) => n + 1;

                static void Main() => Console.WriteLine(1);
            }
            """, Path.Combine(_scratch, "Unused.dll"));

        var (exitCode, _, error) = await ChipSharpCli.RunAsync("tir", dll, "--method", "Program.Unused");
        Assert.Equal(1, exitCode);
        Assert.Equal(
            $"error: 'Program.Unused' is in {dll} but not in the program: the entry point never calls it, so it is not compiled.",
            error.TrimEnd());

        (exitCode, _, error) = await ChipSharpCli.RunAsync("tir", dll, "--method", "Program.Cube");
        Assert.Equal(1, exitCode);
        Assert.Equal($"error: {dll} has no method named 'Program.Cube' (write it as Type.Method, e.g. Program.Main).",
            error.TrimEnd());
    }

    /// <summary>The lines build printed.</summary>
    internal static string[] Lines(string printed) => printed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The line build ends with for a PC program: its file and its size, e.g. "Square.exe: 5.5 KB".</summary>
    private static string HostSize(string program) =>
        $"{Path.GetFileName(program)}: {MemorySize.Text((ulong)new FileInfo(program).Length)}";

    [Fact]
    public async Task The_usage_shows_build_and_tir()
    {
        var (exitCode, printed, _) = await ChipSharpCli.RunAsync("build");

        Assert.Equal(2, exitCode);
        Assert.Contains("chipsharp build <assembly.dll> [-t|--target <name>] [-o|--output <folder>] [--keep]", printed);
        Assert.Contains("chipsharp tir <assembly.dll> [--method <name>]", printed);
        Assert.Contains("chipsharp check <assembly.dll>", printed);
    }
}
