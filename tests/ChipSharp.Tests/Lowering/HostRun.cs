using System.Diagnostics;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Ir;
using ChipSharp.Runtime.Windows;
using ChipSharp.Targets;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Lowering;

/// <summary>What a program wrote to standard output, byte for byte, and its exit code.</summary>
internal sealed record ProgramRun(byte[] Output, int ExitCode, string Errors)
{
    /// <summary>The output as text (it is ASCII: numbers, True and False).</summary>
    public string Text => System.Text.Encoding.UTF8.GetString(Output);

    public override string ToString() => $"exit code {ExitCode}\n{Text}\n{Errors}";
}

/// <summary>
/// Runs programs the way the tests check them: a TIR module built for the real "host" target (the llvm code generator,
/// the windows-x64 runtime and the clang toolchain), and the same C# on real .NET for comparison.
/// </summary>
internal static class HostRun
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    /// <summary>What each assembly printed on .NET, by its name and the hash of its bytes.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<ProgramRun>>> DotnetRuns =
        new(StringComparer.Ordinal);

    /// <summary>Builds <paramref name="module"/> into <paramref name="folder"/> as a Windows program and runs it.</summary>
    public static Task<ProgramRun> BuildAndRunAsync(TirModule module, string folder) =>
        BuildAndRunAsync(module, folder, Targets.Load("host"));

    /// <summary>
    /// Builds <paramref name="module"/> into <paramref name="folder"/> as a Windows program for <paramref name="target"/>
    /// (the "host" target or one like it, such as "host-c") with the code generator its "backend" names, and runs it:
    /// how one program goes through every backend on the PC.
    /// </summary>
    public static async Task<ProgramRun> BuildAndRunAsync(TirModule module, string folder, TargetDescription target)
    {
        IArchRuntime runtime = new WindowsX64Runtime();
        var code = new GeneratedCode(
        [
            .. CodeGenerators.Get<ICodeGenerator>(target.Backend).Generate(module, target).Files,
            .. runtime.Startup(target).Files,
            .. runtime.InterruptEntry(target).Files,
            .. runtime.Atomics(target).Files,
        ]);

        var binary = new ClangToolchain(new ClangToolchainOptions { OutputFolder = folder }).Build(code, target);
        return await RunAsync(binary.Path, [], folder);
    }

    /// <summary>Every code generator, by the name a target's "backend" gives.</summary>
    private static readonly ComponentRegistry CodeGenerators = new ComponentRegistry()
        .Register(new LlvmCodeGenerator())
        .Register(new ChipSharp.Backend.C.CCodeGenerator());

    /// <summary>
    /// Runs a compiled C# program on .NET (the version the tests run on) the way samples/update-expected.ps1 makes the
    /// expected outputs: in the invariant culture (1.5 and Infinity, whatever the PC's regional settings). A program
    /// that <paramref name="fails"/> may write to standard error. <paramref name="folder"/> is made, for the test's own
    /// files; the program runs from a folder of the test run's own (see <see cref="DotnetFolder"/>).
    /// </summary>
    public static async Task<ProgramRun> RunOnDotnetAsync(byte[] assembly, string name, string folder) =>
        await RunOnDotnetAsync(assembly, name, folder, failing: false);

    /// <summary>
    /// Like <see cref="RunOnDotnetAsync(byte[], string, string)"/>; a program that is <paramref name="failing"/> may write
    /// to standard error (an exception nothing catches).
    /// </summary>
    public static async Task<ProgramRun> RunOnDotnetAsync(byte[] assembly, string name, string folder, bool failing)
    {
        Directory.CreateDirectory(folder);

        // the same assembly prints the same: it runs on .NET once per test run (the tests of the PC and of the Pico,
        // in Release and in Debug, compare with the same program)
        var key = $"{name}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(assembly))}";
        var run = await DotnetRuns.GetOrAdd(key, _ => new Lazy<Task<ProgramRun>>(() => RunOnceOnDotnetAsync(assembly, name)))
            .Value;
        Assert.True(failing || run.Errors.Length == 0, $"dotnet {name}.dll failed:\n{run}");
        return run;
    }

    /// <summary>
    /// The folder the programs run on .NET from, a folder of their own each: one for the test run, so that no test's
    /// scratch folder holds a program .NET (or Windows' error reporting, after an exception) may still have open when
    /// the test deletes it. A later test run deletes it.
    /// </summary>
    private static string DotnetFolder => DotnetRunsFolder.Value;

    private static readonly Lazy<string> DotnetRunsFolder = new(() =>
    {
        var parent = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "dotnet");
        if (Directory.Exists(parent))
        {
            foreach (var old in new DirectoryInfo(parent).EnumerateDirectories()
                .Where(folder => DateTime.UtcNow - folder.LastWriteTimeUtc > TimeSpan.FromHours(12)))
            {
                try
                {
                    old.Delete(recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // still in use: another time
                }
            }
        }

        return Directory.CreateDirectory(Path.Combine(parent, Guid.NewGuid().ToString("N"))).FullName;
    });

    private static async Task<ProgramRun> RunOnceOnDotnetAsync(byte[] assembly, string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(DotnetFolder, Guid.NewGuid().ToString("N"))).FullName;
        var dll = Path.Combine(folder, name + ".dll");
        await File.WriteAllBytesAsync(dll, assembly);
        var (major, minor) = (Environment.Version.Major, Environment.Version.Minor);
        await File.WriteAllTextAsync(Path.Combine(folder, name + ".runtimeconfig.json"), $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{major}}.{{minor}}",
                "framework": { "name": "Microsoft.NETCore.App", "version": "{{major}}.{{minor}}.0" },
                "configProperties": { "System.Globalization.Invariant": true }
              }
            }
            """);

        return await RunAsync(Dotnet(), [dll], folder);
    }

    public static async Task<ProgramRun> RunAsync(string program, IEnumerable<string> arguments, string folder)
    {
        var start = new ProcessStartInfo(ProgramStore.Runnable(program))
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"'{program}' did not start.");
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{program}' took more than a minute.");
        }

        await copy;
        return new ProgramRun(output.ToArray(), process.ExitCode, await errors);
    }

    /// <summary>The dotnet the tests run on: the shared framework lives in dotnet/shared/Microsoft.NETCore.App/&lt;version&gt;.</summary>
    private static string Dotnet()
    {
        var framework = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var beside = Path.GetFullPath(Path.Combine(framework, "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return File.Exists(beside) ? beside : "dotnet";
    }
}
