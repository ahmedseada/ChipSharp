using System.Diagnostics;
using ChipSharp.Backend.Llvm;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Llvm;

/// <summary>What a program printed and returned.</summary>
internal sealed record ToolResult(int ExitCode, string Output, string Errors)
{
    public override string ToString() => $"exit code {ExitCode}\n{Output}\n{Errors}";
}

/// <summary>
/// Finds clang and the LLVM tools next to it, runs them, and builds TIR into a Windows program with no C runtime.
/// clang is the one in <c>CHIPSHARP_CLANG</c>, else <c>C:\Program Files\LLVM\bin\clang.exe</c>, else clang on PATH.
/// </summary>
internal static class LlvmTools
{
    private const string InstalledClang = @"C:\Program Files\LLVM\bin\clang.exe";

    private static readonly Lazy<string> ClangPath = new(FindClang);

    public static string Clang => ClangPath.Value;

    /// <summary>An LLVM tool such as llvm-dlltool: the one next to clang, else the one on PATH.</summary>
    public static string Tool(string name)
    {
        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var besideClang = Path.Combine(Path.GetDirectoryName(Clang) ?? "", fileName);
        return File.Exists(besideClang) ? besideClang : OnPath(fileName) ?? throw new InvalidOperationException(
            $"{name} was not found next to clang ('{Clang}') or on PATH. It comes with LLVM; install LLVM " +
            "(e.g. 'winget install LLVM.LLVM').");
    }

    public static async Task<ToolResult> RunAsync(string program, IEnumerable<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"'{program}' did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{program} {string.Join(' ', start.ArgumentList)}' took more than 2 minutes.");
        }

        return new ToolResult(process.ExitCode, await output, await errors);
    }

    /// <summary>Runs clang in <paramref name="folder"/> and fails the test, showing what clang said, unless it succeeds.</summary>
    public static async Task ClangAsync(string folder, params string[] arguments)
    {
        var result = await RunAsync(Clang, arguments, folder);
        Assert.True(result.ExitCode == 0, $"clang {string.Join(' ', arguments)} failed:\n{result}");
    }

    /// <summary>
    /// Generates <paramref name="module"/> for the host, builds it into a standalone .exe with <see cref="Runtime"/>
    /// (no C runtime: kernel32 only, entry chipsharp_start), runs it, and returns what it printed and its exit code.
    /// </summary>
    public static async Task<ToolResult> BuildAndRunAsync(TirModule module, string folder)
    {
        Directory.CreateDirectory(folder);
        var ll = Assert.Single(new LlvmCodeGenerator().Generate(module, LlvmTargets.Host).Files);
        await File.WriteAllTextAsync(Path.Combine(folder, ll.FileName), ll.Content);
        await File.WriteAllTextAsync(Path.Combine(folder, "runtime.c"), Runtime);
        await File.WriteAllTextAsync(Path.Combine(folder, "kernel32.def"),
            "LIBRARY kernel32.dll\nEXPORTS\n    GetStdHandle\n    WriteFile\n    ExitProcess\n");

        var dlltool = await RunAsync(Tool("llvm-dlltool"),
            ["-d", "kernel32.def", "-l", "kernel32.lib", "-m", "i386:x86-64"], folder);
        Assert.True(dlltool.ExitCode == 0, $"llvm-dlltool failed:\n{dlltool}");

        await ClangAsync(folder, "--target=x86_64-pc-windows-msvc", "-O2", "-nostdlib", "-fuse-ld=lld",
            "-Wno-override-module", "runtime.c", ll.FileName, "kernel32.lib", "-o", "program.exe",
            "-Wl,-entry:chipsharp_start", "-Wl,-subsystem:console");

        return await RunAsync(Path.Combine(folder, "program.exe"), [], folder);
    }

    /// <summary>
    /// A small stand-in for the real runtime, enough to run test programs: the console functions of the
    /// runtime contract, and chipsharp_start, which runs chipsharp_entry and exits with its result.
    /// </summary>
    public const string Runtime = """
        typedef void *HANDLE;
        typedef unsigned long DWORD;
        typedef int BOOL;
        __declspec(dllimport) HANDLE __stdcall GetStdHandle(DWORD handle);
        __declspec(dllimport) BOOL __stdcall WriteFile(HANDLE file, const void *buffer, DWORD length, DWORD *written, void *overlapped);
        __declspec(dllimport) __declspec(noreturn) void __stdcall ExitProcess(unsigned int code);

        int _fltused; /* the MSVC target asks for it when a program uses floating point */
        int chipsharp_entry(void);

        static void write(const char *text, DWORD length)
        {
            DWORD written;
            WriteFile(GetStdHandle((DWORD)-11), text, length, &written, 0);
        }

        static void write_number(unsigned long long value, int negative)
        {
            char digits[24];
            int start = sizeof digits;
            digits[--start] = '\n';
            digits[--start] = '\r';
            do
            {
                digits[--start] = (char)('0' + value % 10);
                value /= 10;
            } while (value != 0);
            if (negative)
            {
                digits[--start] = '-';
            }
            write(digits + start, (DWORD)(sizeof digits - start));
        }

        void ChipSharp_Runtime_WriteLine(void) { write("\r\n", 2); }
        void ChipSharp_Runtime_WriteLineInt64(long long value)
        {
            unsigned long long magnitude = value < 0 ? 0 - (unsigned long long)value : (unsigned long long)value;
            write_number(magnitude, value < 0);
        }

        void ChipSharp_Runtime_WriteLineUInt64(unsigned long long value) { write_number(value, 0); }
        void ChipSharp_Runtime_WriteLineInt32(int value) { ChipSharp_Runtime_WriteLineInt64(value); }
        void ChipSharp_Runtime_WriteLineUInt32(unsigned int value) { write_number(value, 0); }
        void ChipSharp_Runtime_WriteLineBool(int value) { if (value) write("True\r\n", 6); else write("False\r\n", 7); }

        void chipsharp_start(void) { ExitProcess((unsigned int)chipsharp_entry()); }
        """;

    private static string FindClang()
    {
        var configured = Environment.GetEnvironmentVariable("CHIPSHARP_CLANG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : throw new InvalidOperationException(
                $"CHIPSHARP_CLANG is '{configured}', but there is no file there. Point it at clang, or unset it.");
        }

        if (File.Exists(InstalledClang))
        {
            return InstalledClang;
        }

        return OnPath(OperatingSystem.IsWindows() ? "clang.exe" : "clang") ?? throw new InvalidOperationException(
            "clang was not found, so the generated LLVM IR cannot be checked. Install LLVM (e.g. 'winget install " +
            $"LLVM.LLVM', which puts clang at '{InstalledClang}'), put clang on PATH, or set CHIPSHARP_CLANG to it.");
    }

    private static string? OnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), fileName))
            .FirstOrDefault(File.Exists);
}
