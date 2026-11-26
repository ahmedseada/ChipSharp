using System.Diagnostics;
using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Runtime.Windows;
using ChipSharp.Targets;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Host;

/// <summary>
/// Builds hand-written LLVM IR with the clang toolchain and the real windows-x64 runtime, runs the program and
/// checks what it prints. The IR uses the symbols the LLVM code generator writes.
/// </summary>
public sealed class ClangToolchainTests : IDisposable
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [HostFact]
    public void Square_of_42_prints_1764()
    {
        var (output, exitCode) = BuildAndRun("Square.ll", """
            declare void @ChipSharp_Runtime_WriteLineInt32(i32)

            define i32 @Program_Square(i32 %n) {
            entry:
              %0 = mul i32 %n, %n
              ret i32 %0
            }

            define void @Program_Main() {
            entry:
              %0 = call i32 @Program_Square(i32 42)
              call void @ChipSharp_Runtime_WriteLineInt32(i32 %0)
              ret void
            }

            define i32 @chipsharp_entry() {
            entry:
              call void @Program_Main()
              ret i32 0
            }
            """);

        Assert.Equal("1764\r\n", output);
        Assert.Equal(0, exitCode);
    }

    [HostFact]
    public void Every_print_function_writes_what_dotnet_writes_and_the_entry_result_is_the_exit_code()
    {
        var (output, exitCode) = BuildAndRun("Prints.ll", """
            declare void @ChipSharp_Runtime_WriteLine()
            declare void @ChipSharp_Runtime_WriteLineInt32(i32)
            declare void @ChipSharp_Runtime_WriteLineUInt32(i32)
            declare void @ChipSharp_Runtime_WriteLineInt64(i64)
            declare void @ChipSharp_Runtime_WriteLineUInt64(i64)
            declare void @ChipSharp_Runtime_WriteLineBool(i32)

            define i32 @chipsharp_entry() {
            entry:
              call void @ChipSharp_Runtime_WriteLineInt32(i32 0)
              call void @ChipSharp_Runtime_WriteLineInt32(i32 -7)
              call void @ChipSharp_Runtime_WriteLineInt32(i32 2147483647)
              call void @ChipSharp_Runtime_WriteLineInt32(i32 -2147483648)
              call void @ChipSharp_Runtime_WriteLineUInt32(i32 0)
              call void @ChipSharp_Runtime_WriteLineUInt32(i32 -1)
              call void @ChipSharp_Runtime_WriteLineUInt32(i32 -1294967296)
              call void @ChipSharp_Runtime_WriteLineInt64(i64 0)
              call void @ChipSharp_Runtime_WriteLineInt64(i64 -1234567890123)
              call void @ChipSharp_Runtime_WriteLineInt64(i64 9223372036854775807)
              call void @ChipSharp_Runtime_WriteLineInt64(i64 -9223372036854775808)
              call void @ChipSharp_Runtime_WriteLineUInt64(i64 10)
              call void @ChipSharp_Runtime_WriteLineUInt64(i64 -1)
              call void @ChipSharp_Runtime_WriteLineUInt64(i64 -9223372036854775808)
              call void @ChipSharp_Runtime_WriteLineBool(i32 1)
              call void @ChipSharp_Runtime_WriteLineBool(i32 0)
              call void @ChipSharp_Runtime_WriteLine()
              call void @ChipSharp_Runtime_WriteLineInt32(i32 1)
              ret i32 42
            }
            """);

        var expected = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
        expected.WriteLine(0);
        expected.WriteLine(-7);
        expected.WriteLine(int.MaxValue);
        expected.WriteLine(int.MinValue);
        expected.WriteLine(0u);
        expected.WriteLine(uint.MaxValue);
        expected.WriteLine(3000000000u);
        expected.WriteLine(0L);
        expected.WriteLine(-1234567890123L);
        expected.WriteLine(long.MaxValue);
        expected.WriteLine(long.MinValue);
        expected.WriteLine(10UL);
        expected.WriteLine(ulong.MaxValue);
        expected.WriteLine(9223372036854775808UL);
        expected.WriteLine(true);
        expected.WriteLine(false);
        expected.WriteLine();
        expected.WriteLine(1);
        Assert.Equal(expected.ToString(), output);
        Assert.Equal(42, exitCode);
    }

    [HostFact]
    public void The_program_is_written_into_the_output_folder_with_everything_it_was_built_from()
    {
        var host = Targets.Load("host");
        var code = WithRuntime(host, new GeneratedFile("Empty.ll", "define i32 @chipsharp_entry() {\n  ret i32 0\n}\n"));

        var binary = new ClangToolchain(new ClangToolchainOptions { OutputFolder = _scratch, ProgramName = "app" })
            .Build(code, host);

        Assert.Equal(Path.Combine(_scratch, "app.exe"), binary.Path);
        Assert.True(File.Exists(Path.Combine(_scratch, "Empty.ll")));
        Assert.True(File.Exists(Path.Combine(_scratch, WindowsX64Runtime.SourceFileName)));
        Assert.True(File.Exists(Path.Combine(_scratch, "kernel32.lib")));
        Assert.Equal(0, Run(binary.Path).ExitCode);
    }

    [HostFact]
    public void Broken_IR_fails_with_the_clang_command_and_clangs_own_error()
    {
        var host = Targets.Load("host");
        var code = WithRuntime(host, new GeneratedFile("Broken.ll", """
            define i32 @chipsharp_entry() {
            entry:
              ret i32 %nothing
            }
            """));

        var error = Assert.Throws<ChipSharpException>(
            () => new ClangToolchain(new ClangToolchainOptions { OutputFolder = _scratch }).Build(code, host));

        Assert.Contains("clang failed", error.Message);
        Assert.Contains("--target=x86_64-pc-windows-msvc", error.Message);
        Assert.Contains("Broken.ll", error.Message);
        Assert.Contains("use of undefined value '%nothing'", error.Message);
    }

    [Fact]
    public void A_chip_is_not_linked_yet_and_the_error_says_when_it_will_be()
    {
        var pico = Targets.Load("pico");
        var code = new GeneratedCode([new GeneratedFile("Blink.ll", "")]);

        var error = Assert.Throws<ChipSharpException>(() => new ClangToolchain().Build(code, pico));

        Assert.StartsWith("Target 'pico': it has no 'link' settings", error.Message);
        Assert.Contains("milestone M1", error.Message);
    }

    [Fact]
    public void A_clang_that_is_not_there_is_reported_with_where_it_was_expected()
    {
        var missing = Path.Combine(_scratch, "no-such-clang.exe");
        var code = new GeneratedCode([new GeneratedFile("Square.ll", "")]);

        var error = Assert.Throws<ChipSharpException>(
            () => new ClangToolchain(new ClangToolchainOptions { ClangPath = missing }).Build(code, Targets.Load("host")));

        Assert.Equal($"clang was not found at '{missing}' (set by the ClangPath option).", error.Message);
    }

    [Fact]
    public void An_environment_variable_that_points_nowhere_is_an_error_not_a_silent_fallback()
    {
        var missing = Path.Combine(_scratch, "llvm", "bin");
        var toolchain = new ClangToolchain(null, name => name == ClangToolchain.LlvmBinVariable ? missing : null);

        var error = Assert.Throws<ChipSharpException>(toolchain.FindClang);

        Assert.Contains($"'{Path.Combine(missing, "clang")}", error.Message);
        Assert.Contains("set by the environment variable CHIPSHARP_LLVM_BIN", error.Message);
    }

    [HostFact]
    public void The_LLVM_bin_folder_from_the_environment_is_where_both_tools_are_taken_from()
    {
        var bin = Path.GetDirectoryName(new ClangToolchain().FindClang())!;
        var toolchain = new ClangToolchain(null, name => name == ClangToolchain.LlvmBinVariable ? bin : null);

        Assert.Equal(Path.Combine(bin, "clang.exe"), toolchain.FindClang());
        Assert.Equal(Path.Combine(bin, "llvm-dlltool.exe"), toolchain.FindDlltool());
    }

    [Fact]
    public void Two_generated_files_with_the_same_name_are_rejected()
    {
        var code = new GeneratedCode([new GeneratedFile("program.ll", ""), new GeneratedFile("Program.ll", "")]);

        var error = Assert.Throws<ChipSharpException>(() => new ClangToolchain().Build(code, Targets.Load("host")));

        Assert.Contains("Two generated files are named 'Program.ll'", error.Message);
    }

    private (string Output, int ExitCode) BuildAndRun(string fileName, string ir)
    {
        var host = Targets.Load("host");
        var code = WithRuntime(host, new GeneratedFile(fileName, ir));

        var binary = new ClangToolchain(new ClangToolchainOptions { OutputFolder = _scratch }).Build(code, host);

        Assert.Equal(Path.Combine(_scratch, Path.ChangeExtension(fileName, ".exe")), binary.Path);
        return Run(binary.Path);
    }

    /// <summary>The program's files plus the runtime's, as the build pipeline hands them to the toolchain.</summary>
    private static GeneratedCode WithRuntime(TargetDescription target, GeneratedFile program)
    {
        IArchRuntime runtime = new WindowsX64Runtime();
        return new GeneratedCode(
        [
            program,
            .. runtime.Startup(target).Files,
            .. runtime.InterruptEntry(target).Files,
            .. runtime.Atomics(target).Files,
        ]);
    }

    private static (string Output, int ExitCode) Run(string program)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill();
            Assert.Fail($"{program} did not finish within 30 seconds.");
        }

        return (output.GetAwaiter().GetResult(), process.ExitCode);
    }
}
