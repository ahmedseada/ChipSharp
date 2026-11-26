using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// Float and double math on the Pico, which has no float hardware: C# built for it computes with the armv6m
/// runtime's soft-float routines (LLVM's compiler-rt) and prints, over UART0 in the simulator, exactly what .NET
/// prints (see <see cref="FloatMathProgram"/>); the routines need nothing else, and a program links only those it
/// uses.
/// </summary>
public sealed partial class PicoFloatMathTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>
    /// Every operation for every special value and for each pair of half of them, for random pairs, and the
    /// conversions from integers: tens of thousands of results, each computed on the simulated Cortex-M0+.
    /// </summary>
    [SimulatorFact]
    public async Task Thousands_of_float_and_double_results_on_the_simulated_Pico_match_dotnet()
    {
        var doubles = FloatMathProgram.SpecialDoubles;
        var floats = FloatMathProgram.SpecialFloats;
        var program = new FloatMathProgram()
            .Double([.. doubles, .. FloatMathProgram.RandomDoubles(100, 20)])
            .Float([.. floats, .. FloatMathProgram.RandomFloats(100, 21)])
            .Doubles(FloatMathProgram.AllPairs([.. doubles.Where((_, i) => i % 2 == 0)]))
            .Doubles(FloatMathProgram.Pairs(FloatMathProgram.RandomDoubles(400, 22)))
            .Floats(FloatMathProgram.AllPairs([.. floats.Where((_, i) => i % 2 == 0)]))
            .Floats(FloatMathProgram.Pairs(FloatMathProgram.RandomFloats(400, 23)))
            .Mixed(floats.Zip(doubles.Reverse()))
            .Int32(FloatMathProgram.SpecialInt32s)
            .Int64([.. FloatMathProgram.SpecialInt64s, .. FloatMathProgram.RandomInt64s(100, 24)]);
        var source = program.Source();
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "FloatMath"), "FloatMath",
            Path.Combine(_scratch, "dotnet"));
        var built = PicoBuild.Build(source, "FloatMath", Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(built.Uf2, 600_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.True(program.Count > 30_000, $"{program.Count} values");
        program.AssertSameOutput(dotnet.Text, Encoding.ASCII.GetString(run.Uart0));
    }

    /// <summary>
    /// compiler-rt's routines, the comparisons and the shared float math, built for the Cortex-M0+ as a program
    /// builds them, call only each other and the armv6m helpers (64-bit multiply and shifts), and all of them
    /// together take under 10 KB of flash.
    /// </summary>
    [PicoFact]
    public void The_float_routines_need_only_each_other_and_the_armv6m_helpers_and_take_under_10_KB()
    {
        Directory.CreateDirectory(_scratch);
        var pico = PicoBuild.Pico;
        var flags = pico.Settings["clang"]!["flags"]!.AsArray().Select(f => f!.GetValue<string>())
            .Where(f => f != "-fuse-ld=lld" && f != "-nostdlib");
        var files = new ArmV6MRuntime().Startup(pico).Files;
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_scratch, file.FileName), file.Content);
        }

        string[] sources =
        [
            .. CompilerRt.Units.Select(unit => unit.Name), ArmV6MRuntime.FloatCompareFileName,
            ArmV6MRuntime.FloatMathFileName,
        ];
        Run(new ClangToolchain().FindClang(), [$"--target={pico.Triple}", $"-mcpu={pico.Cpu}", .. flags, "-c", .. sources]);
        string[] objects = [.. sources.Select(source => Path.ChangeExtension(source, ".o"))];

        var defined = Run(PicoTools.LlvmTool("llvm-nm"), ["--defined-only", "--just-symbol-name", .. objects]);
        var undefined = Run(PicoTools.LlvmTool("llvm-nm"), ["--undefined-only", "--just-symbol-name", .. objects]);
        var sections = Run(PicoTools.LlvmTool("llvm-size"), ["-A", .. objects]);

        var names = defined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        string[] calls = ["__aeabi_fadd", "__aeabi_fsub", "__aeabi_fmul", "__aeabi_fdiv", "__aeabi_dadd", "__aeabi_dsub",
            "__aeabi_dmul", "__aeabi_ddiv", "__aeabi_f2d", "__aeabi_d2f", "__aeabi_f2iz", "__aeabi_f2uiz", "__aeabi_f2lz",
            "__aeabi_f2ulz", "__aeabi_d2iz", "__aeabi_d2uiz", "__aeabi_d2lz", "__aeabi_d2ulz", "__aeabi_i2f",
            "__aeabi_ui2f", "__aeabi_l2f", "__aeabi_ul2f", "__aeabi_i2d", "__aeabi_ui2d", "__aeabi_l2d", "__aeabi_ul2d",
            "__aeabi_fcmpeq", "__aeabi_fcmplt", "__aeabi_fcmple", "__aeabi_fcmpge", "__aeabi_fcmpgt", "__aeabi_fcmpun",
            "__aeabi_dcmpeq", "__aeabi_dcmplt", "__aeabi_dcmple", "__aeabi_dcmpge", "__aeabi_dcmpgt", "__aeabi_dcmpun",
            "fmodf", "fmod"];
        Assert.All(calls, call => Assert.Contains(call, names));
        var outside = undefined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !names.Contains(name) && !name.EndsWith(".o:", StringComparison.Ordinal))
            .Distinct().Order();
        Assert.Equal(["__aeabi_llsl", "__aeabi_llsr", "__aeabi_lmul"], outside);
        var size = CodeSection().Matches(sections).Sum(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.InRange(size, 5000, 10 * 1024 - 1);
    }

    /// <summary>A program that uses no float links none of the float routines: they cost it no flash.</summary>
    [PicoFact]
    public void A_program_without_floats_links_none_of_the_float_routines()
    {
        var program = PicoBuild.Build("""
            class Program
            {
                static long Square(long n) => n * n;
                static void Main() => System.Console.WriteLine(Square(123456789) / 7);
            }
            """, "NoFloats", _scratch);

        var symbols = Run(PicoTools.LlvmTool("llvm-nm"), ["--just-symbol-name", program.Elf]);

        Assert.Contains("__aeabi_lmul", symbols);
        Assert.DoesNotMatch(@"__aeabi_[fd](add|sub|mul|div|cmp)|__aeabi_[fdil]2|__aeabi_u[il]2|fmod|Sqrt|__adddf3", symbols);
    }

    private string Run(string tool, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(tool)
        {
            WorkingDirectory = _scratch,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{tool} {string.Join(' ', start.ArgumentList)} failed:\n{output}\n{errors.Result}");
        return output;
    }

    /// <summary>A code or constants section in llvm-size -A's list, with its size.</summary>
    [GeneratedRegex(@"^\.(?:text|rodata)\S*\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex CodeSection();
}
