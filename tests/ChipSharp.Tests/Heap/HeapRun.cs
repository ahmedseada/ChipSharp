using System.Diagnostics;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Image.Uf2;
using ChipSharp.Ir;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Runtime.Windows;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Heap;

/// <summary>What a heap program printed, line by line, and how long it ran (on the PC: real time; on the Pico: simulated).</summary>
internal sealed record HeapRunResult(int ExitCode, IReadOnlyList<string> Lines, string Errors, TimeSpan Time, string Raw)
{
    /// <summary>Line <paramref name="index"/> as a number.</summary>
    public long Number(int index) => long.Parse(Lines[index], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The exit code, the lines printed (one string) and the errors, to compare at once.</summary>
    public (int ExitCode, string Lines, string Errors) Outcome => (ExitCode, string.Join("\n", Lines), Errors);

    public override string ToString() => $"exit code {ExitCode} after {Time}\n{Raw}\n{Errors}";
}

/// <summary>
/// Builds hand-written TIR with the real runtimes, the way <c>chipsharp build</c> does, for the PC or the Pico, with the
/// shipped targets or ones that inherit them with another heap size, and runs it (the Pico's in the simulator).
/// </summary>
internal static class HeapRun
{
    /// <summary>The shipped targets, and "host-&lt;heap&gt;" and "pico-&lt;heap&gt;" with a heap of that size, in <paramref name="folder"/>.</summary>
    public static TargetLoader Targets(string folder, params string[] heaps) => new(TargetFiles.WithShipped(folder,
    [
        .. heaps.Select(heap => ($"host-{heap}", $$"""{ "inherits": "host", "capabilities": { "heap": "{{heap}}" } }""")),
        .. heaps.Select(heap => ($"pico-{heap}", $$"""{ "inherits": "pico", "capabilities": { "heap": "{{heap}}" } }""")),
    ]));

    public static async Task<HeapRunResult> OnHostAsync(TirModule module, string folder, TargetDescription host)
    {
        IArchRuntime runtime = new WindowsX64Runtime();
        var binary = new ClangToolchain(new ClangToolchainOptions { OutputFolder = folder }).Build(Code(module, host, runtime), host);
        var clock = Stopwatch.StartNew();
        var run = await HostRun.RunAsync(binary.Path, [], folder);
        clock.Stop();
        return new HeapRunResult(run.ExitCode, Lines(run.Text), run.Errors, clock.Elapsed, run.Text);
    }

    /// <summary>Builds the module for <paramref name="pico"/> and runs it in the simulator for at most <paramref name="milliseconds"/>.</summary>
    public static async Task<(HeapRunResult Result, SimulatorRun Run, PicoProgram Program)> OnPicoAsync(TirModule module,
        string folder, TargetDescription pico, int milliseconds = 10_000, params string[] options)
    {
        IArchRuntime runtime = new Rp2040Runtime();
        var binary = new ClangToolchain(new ClangToolchainOptions { OutputFolder = folder }).Build(Code(module, pico, runtime), pico);
        var uf2 = Path.ChangeExtension(binary.Path, ".uf2");
        using (var output = File.Create(uf2))
        {
            new Uf2ImageFormat().Package(binary, pico, output);
        }

        var run = await Rp2040Simulator.RunAsync(uf2, milliseconds, options);
        var text = System.Text.Encoding.UTF8.GetString(run.Uart0);
        var stopped = System.Text.RegularExpressions.Regex.Match(run.Stopped, @"\[\s*([0-9.]+) ms\]");
        var time = stopped.Success
            ? TimeSpan.FromMilliseconds(double.Parse(stopped.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            : TimeSpan.Zero;
        return (new HeapRunResult(run.ExitCode, Lines(text), run.Errors, time, text), run,
            new PicoProgram(binary.Path, uf2));
    }

    private static GeneratedCode Code(TirModule module, TargetDescription target, IArchRuntime runtime) => new(
    [
        .. new LlvmCodeGenerator().Generate(module, target).Files,
        .. runtime.Startup(target).Files,
        .. runtime.InterruptEntry(target).Files,
        .. runtime.Atomics(target).Files,
    ]);

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
}
