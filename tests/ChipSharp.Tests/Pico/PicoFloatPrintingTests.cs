using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// Console.WriteLine(double) and Console.WriteLine(float) on the Pico: the shared float text built for the
/// Cortex-M0+ calls nothing (no float routines, no division, no memset) and stays small, and doubles and floats
/// printed over UART0 in the simulator are exactly what .NET writes.
/// </summary>
public sealed partial class PicoFloatPrintingTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [PicoFact]
    public void On_the_Cortex_M0_plus_the_float_text_calls_nothing_and_takes_under_2_KB()
    {
        Directory.CreateDirectory(_scratch);
        var pico = PicoBuild.Pico;
        var flags = pico.Settings["clang"]!["flags"]!.AsArray().Select(f => f!.GetValue<string>())
            .Where(f => f != "-fuse-ld=lld" && f != "-nostdlib");
        File.WriteAllText(Path.Combine(_scratch, "float_text.c"), Rp2040Runtime.Source(Rp2040Runtime.FloatTextFileName).Content);
        Run(new ClangToolchain().FindClang(),
            [$"--target={pico.Triple}", $"-mcpu={pico.Cpu}", .. flags, "-c", "float_text.c", "-o", "float_text.o"]);

        var undefined = Run(PicoTools.LlvmTool("llvm-nm"), ["--undefined-only", "float_text.o"]);
        var sections = Run(PicoTools.LlvmTool("llvm-size"), ["-A", "float_text.o"]);

        Assert.True(undefined.Trim().Length == 0, $"The float text calls:\n{undefined}");
        var size = CodeSection().Matches(sections).Sum(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.InRange(size, 1, 2047);
    }

    /// <summary>
    /// The edge cases and the powers of ten around both switches to exponent notation, and random values from a
    /// fixed seed, printed through UART0 by the runtime's WriteLineFloat64 and WriteLineFloat32.
    /// </summary>
    [SimulatorFact]
    public async Task Doubles_and_floats_print_over_UART0_what_dotnet_prints()
    {
        double[] doubles = [.. FloatPrinting.DoubleCases(), .. FloatPrinting.RandomDoubles(100, 2040)];
        float[] floats = [.. FloatPrinting.FloatCases(), .. FloatPrinting.RandomFloats(100, 2041)];
        var program = PicoBuild.Build(FloatPrinting.Program("Floats", doubles, floats), _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 60_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.True(doubles.Length + floats.Length > 400, $"{doubles.Length} doubles, {floats.Length} floats");
        FloatPrinting.AssertLines(FloatPrinting.Expected(doubles, floats), Encoding.ASCII.GetString(run.Uart0));
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
