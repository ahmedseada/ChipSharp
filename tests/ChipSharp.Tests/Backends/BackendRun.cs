using System.Buffers.Binary;
using ChipSharp.Abstractions;
using ChipSharp.Backend.C;
using ChipSharp.Build;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Simulators;
using ChipSharp.Tests.Ch32v003;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Strings;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// What came of a program on a target: what it printed (<see cref="Run"/>, with .NET's for the same program), or why the
/// target does not take it (<see cref="Skipped"/>, one of <see cref="BackendSkips"/>' reasons, and the message that said
/// so).
/// </summary>
internal sealed record BackendOutcome(string? Skipped, string? Why, ProgramRun? Run, ProgramRun? Dotnet)
{
    public static BackendOutcome Skip(string reason, string why) => new(reason, why, null, null);
}

/// <summary>
/// Builds a program for a target of <see cref="BackendTargets"/> and runs it: on the PC as chipsharp build does it, for
/// the target's code generator, and with .NET; on an SDCC chip through the C code generator and SDCC with the tests'
/// startup and console (<see cref="ChipConsole"/>), on the ESP32-C5 and the CH32V003 through llvm, in chipsim, in
/// process.
/// </summary>
internal static class BackendRun
{
    private static readonly string ShippedTargets = Path.Combine(AppContext.BaseDirectory, "targets");
    private static readonly string Cache = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "sdcc-cache");

    /// <summary>How long a chip may run, in simulated milliseconds: every program ends well before.</summary>
    private const double ChipMilliseconds = 600_000;

    public static async Task<BackendOutcome> RunAsync(BackendTarget target, string name, string source, string folder)
    {
        var file = name.Replace('/', '_');
        var outcome = target.Chip switch
        {
            null => await OnThePcAsync(target, file, source, folder),
            "esp32c5" => OnTheEsp32c5(target, file, source, folder),
            "ch32v003" => OnTheCh32v003(target, file, source, folder),
            _ => OnTheChip(target, file, source, folder),
        };
        if (outcome.Skipped is not null)
        {
            return outcome;
        }

        // .NET with a UTF-8 console, without the byte order mark: the text ChipSharp's runtimes write
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile([source, StringPrograms.Utf8Console], file), file,
            Path.Combine(folder, "dotnet"));
        return outcome with { Dotnet = dotnet };
    }

    private static async Task<BackendOutcome> OnThePcAsync(BackendTarget target, string file, string source, string folder)
    {
        var dll = CSharpCompiler.CompileToFile(source, Path.Combine(folder, "dll", file + ".dll"));
        BuildResult result;
        try
        {
            result = ChipSharpBuilder.Build(dll, new BuildOptions
            {
                Target = target.Name,
                TargetsFolder = ShippedTargets,
                OutputFolder = Path.Combine(folder, target.Name),
            });
        }
        catch (ChipSharpException e)
        {
            return BackendOutcome.Skip(BackendSkips.Classify(e.Message), e.Message);
        }

        return new BackendOutcome(null, null, await HostRun.RunAsync(result.OutputPath, [], folder), null);
    }

    /// <summary>
    /// The program built for a chip (<paramref name="description"/>, its target with a console) through the C code
    /// generator and SDCC, with the tests' startup and console, into <paramref name="folder"/>: the module and the .ihx,
    /// or why the chip does not take it.
    /// </summary>
    public static (TirModule? Module, LinkedBinary? Binary, BackendOutcome? Skip) BuildForChip(TargetDescription description,
        string file, string source, string folder)
    {
        var analysis = WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile(source, file), file + ".dll"), description);
        if (!analysis.IsSupported)
        {
            return (null, null, BackendOutcome.Skip(BackendSkips.Classify(analysis), analysis.Summary));
        }

        var module = IlToTir.Lower(analysis.ToProgram());
        if (ChipConsole.Missing(module) is { Count: > 0 } missing)
        {
            return (module, null, BackendOutcome.Skip(BackendSkips.Runtime(missing), string.Join(", ", missing)));
        }

        try
        {
            var code = new CCodeGenerator().Generate(module, description);
            return (module, new SdccToolchain(new SdccToolchainOptions
            {
                OutputFolder = folder,
                ProgramName = file,
                CacheFolder = Cache,
            }).Build(new GeneratedCode([.. code.Files, ChipConsole.File(module, description), ChipConsole.ReportFile]),
                description), null);
        }
        catch (ChipSharpException e) when (BackendSkips.Classify(e.Message) is var reason && reason != BackendSkips.Other)
        {
            return (module, null, BackendOutcome.Skip(reason, e.Message));
        }
    }

    /// <summary>
    /// The program built for the ESP32-C5 as chipsharp build builds it (the llvm code generator for riscv32, the esp32c5
    /// runtime, the ESP image), and run in chipsim's esp32c5: what it writes to the USB Serial/JTAG console (serial port
    /// "USB") is what it prints.
    /// </summary>
    private static BackendOutcome OnTheEsp32c5(BackendTarget target, string file, string source, string folder)
    {
        var dll = CSharpCompiler.CompileToFile(source, Path.Combine(folder, "dll", file + ".dll"));
        BuildResult result;
        try
        {
            result = ChipSharpBuilder.Build(dll, new BuildOptions
            {
                Target = target.Name,
                TargetsFolder = ShippedTargets,
                OutputFolder = Path.Combine(folder, target.Name),
            });
        }
        catch (ChipSharpException e)
        {
            return BackendOutcome.Skip(BackendSkips.Classify(e.Message), e.Message);
        }

        var run = Simulation.Run(new SimulationOptions
        {
            Chip = target.Chip!,
            Image = result.OutputPath,
            Milliseconds = ChipMilliseconds,
        });
        var ended = run.Stopped.StartsWith("stopped: the program has finished", StringComparison.Ordinal);
        var errors = ended && run.ExitCode == 0 ? "" : $"{run.Stopped} (exit code {run.ExitCode}){Environment.NewLine}{run.Output}";
        return new BackendOutcome(null, null, new ProgramRun(run.SerialOf("USB"), ended ? run.ExitCode : 1, errors), null);
    }

    /// <summary>
    /// The program built for the CH32V003 (the llvm code generator for riscv32 with the ilp32e ABI, the riscv32e and
    /// ch32v003 runtimes) with the shared Console.Write and WriteLine and the tests' console
    /// (<see cref="Ch32v003Build.ConsoleSink"/>), until the chip's runtime has its own on USART1, and run in chipsim's
    /// ch32v003: what the program prints is the bytes of every call of chipsharp_console_write, read as it is called. A
    /// stack check that finds the 2 KB run out (chipsharp_stack_overflow) is the reason the chip does not take it.
    /// </summary>
    private static BackendOutcome OnTheCh32v003(BackendTarget target, string file, string source, string folder)
    {
        var assembly = CSharpCompiler.Compile(source, file);
        var description = Ch32v003Build.WithConsole();
        var analysis = Ch32v003Build.Check(assembly, file, description);
        if (!analysis.IsSupported)
        {
            return BackendOutcome.Skip(BackendSkips.Classify(analysis), analysis.Summary);
        }

        Ch32v003Program program;
        try
        {
            program = Ch32v003Build.Build(assembly, file, Path.Combine(folder, target.Name), description);
        }
        catch (ChipSharpException e) when (BackendSkips.Classify(e.Message) is var reason && reason != BackendSkips.Other)
        {
            return BackendOutcome.Skip(reason, e.Message);
        }

        var run = Ch32v003Build.Run(program, ChipMilliseconds, CStackChecks.OverflowSymbol);
        var errors = run.Ended ? "" : $"{run.Simulation.Stopped} (exit code {run.Simulation.ExitCode}){Environment.NewLine}" +
            run.Simulation.Output;
        return run.Called.Contains(CStackChecks.OverflowSymbol)
            ? BackendOutcome.Skip(BackendSkips.Stack, errors)
            : new BackendOutcome(null, null, new ProgramRun(run.Output, run.Ended ? 0 : 1, errors), null);
    }

    private static BackendOutcome OnTheChip(BackendTarget target, string file, string source, string folder)
    {
        var description = target.Load();
        var (built, binary, skip) = BuildForChip(description, file, source, Path.Combine(folder, target.Name));
        if (skip is not null)
        {
            return skip;
        }

        var module = built!;

        var symbols = SdccPrograms.MapSymbols(Path.ChangeExtension(binary!.Path, ".map"));
        var (report, kind, value) = ((int)symbols["_report"], (int)symbols["_cs_kind"], (int)symbols["_cs_value"]);
        var bigEndian = description.Endian == Endianness.Big;
        var reports = new List<(byte Kind, uint Value)>();
        var run = Simulation.Run(new SimulationOptions
        {
            Chip = target.Chip!,
            Image = binary.Path,
            Milliseconds = ChipMilliseconds,
        }, setUp: chip => chip.Hooks = new Dictionary<int, Action<SimulatedChip>>
        {
            [report] = c => reports.Add((c.Ram[kind], bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(c.Ram.AsSpan(value, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(c.Ram.AsSpan(value, 4)))),
        });

        var (output, failure, ended) = ChipConsole.Text(reports, module);
        var errors = (failure is null ? "" : $"stopped by {failure}\n") +
            (ended ? "" : $"it did not end: {run.Stopped} (at 0x{run.Chip.ProgramCounter:x})\n") +
            (run.ExitCode == 0 ? "" : run.Output);

        // a stack check found the stack run out (or the simulator, a stack past the end of RAM): the chip has too little
        if (failure == CStackChecks.OverflowSymbol || run.Stopped.Contains("the stack has grown past", StringComparison.Ordinal))
        {
            return BackendOutcome.Skip(BackendSkips.Stack, errors);
        }

        return new BackendOutcome(null, null, new ProgramRun(output, run.ExitCode != 0 || failure is not null || !ended ? 1 : 0,
            errors), null);
    }
}
