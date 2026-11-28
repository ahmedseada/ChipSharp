using ChipSharp.Backend.Llvm;
using ChipSharp.Build;
using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Build;

/// <summary>Builds the samples for the real "host" target through <see cref="ChipSharpBuilder"/> and runs them.</summary>
public sealed class ChipSharpBuilderTests : IDisposable
{
    private static readonly string ShippedTargets = Path.Combine(AppContext.BaseDirectory, "targets");
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [HostFact]
    [RunsLong]
    public async Task Every_sample_builds_for_the_host_and_prints_exactly_what_dotnet_prints()
    {
        var output = Path.Combine(_scratch, "out");
        var results = await Concurrently.SelectAsync(Samples.Names, async name =>
        {
            var dll = Samples.Compile(name, Path.Combine(_scratch, "dll"));

            var result = ChipSharpBuilder.Build(dll, new BuildOptions { TargetsFolder = ShippedTargets, OutputFolder = output });

            Assert.Equal(Path.Combine(output, name + ".exe"), result.OutputPath);
            Assert.Empty(result.IntermediatePaths);
            Assert.Equal("host", result.Target.Name);
            var run = await HostRun.RunAsync(result.OutputPath, [], _scratch);
            return run.ExitCode != 0 || !run.Output.SequenceEqual(Samples.ExpectedOutput(name)) ? $"{name}: {run}" : null;
        });

        Assert.Empty(results.OfType<string>());
        // only the programs: the generated code, the runtime and the import libraries stay in the work folder
        Assert.Equal(Samples.Names.Select(name => name + ".exe"), FileNames(output));
    }

    [HostFact]
    public async Task Keep_writes_the_TIR_and_the_LLVM_IR_next_to_the_program()
    {
        var dll = Samples.Compile("Square", _scratch);
        var output = Path.Combine(_scratch, "out");

        var result = ChipSharpBuilder.Build(dll,
            new BuildOptions { TargetsFolder = ShippedTargets, OutputFolder = output, KeepIntermediates = true });

        Assert.Equal(Path.Combine(output, "Square.exe"), result.OutputPath);
        Assert.Equal([Path.Combine(output, "Square.tir"), Path.Combine(output, "Square.ll")], result.IntermediatePaths);
        Assert.Equal(["Square.exe", "Square.ll", "Square.tir"], FileNames(output));

        var module = ChipSharpBuilder.Translate(dll);
        var host = new TargetLoader(ShippedTargets).Load("host");
        Assert.Equal(TirPrinter.Print(module), await File.ReadAllTextAsync(result.IntermediatePaths[0]));
        Assert.Equal(Assert.Single(new LlvmCodeGenerator().Generate(module, host).Files).Content,
            await File.ReadAllTextAsync(result.IntermediatePaths[1]));
        Assert.StartsWith("module Square\nentrypoint Program.Main\n", await File.ReadAllTextAsync(result.IntermediatePaths[0]));
        Assert.Contains("define i32 @chipsharp_entry()", await File.ReadAllTextAsync(result.IntermediatePaths[1]));
    }

    [HostFact]
    public async Task The_program_name_names_every_file_of_the_build_and_a_second_build_replaces_them()
    {
        var dll = Samples.Compile("Fibonacci", _scratch);
        var output = Path.Combine(_scratch, "out");
        var options = new BuildOptions
        {
            TargetsFolder = ShippedTargets,
            OutputFolder = output,
            ProgramName = "fib",
            KeepIntermediates = true,
        };

        ChipSharpBuilder.Build(dll, options);
        var result = ChipSharpBuilder.Build(dll, options);

        Assert.Equal(Path.Combine(output, "fib.exe"), result.OutputPath);
        // the TIR file takes the program's name; the code generator names its own files (after the assembly)
        Assert.Equal([Path.Combine(output, "fib.tir"), Path.Combine(output, "Fibonacci.ll")], result.IntermediatePaths);
        var run = await HostRun.RunAsync(result.OutputPath, [], _scratch);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Samples.ExpectedOutput("Fibonacci"), run.Output);
    }

    [Fact]
    public void Translate_gives_the_program_the_whole_program_pass_found_as_TIR()
    {
        var dll = Samples.Compile("Square", _scratch);

        var module = ChipSharpBuilder.Translate(dll);

        Assert.Equal("Square", module.Name);
        Assert.Equal("Program.Main", module.EntryPoint!.Name);
        Assert.Equal(["Program.Main", "Program.Square", "ChipSharp.Runtime.WriteLineInt32"],
            module.Functions.Select(f => f.Name));
    }

    private static IEnumerable<string> FileNames(string folder) =>
        Directory.GetFiles(folder).Select(path => Path.GetFileName(path)).Order(StringComparer.OrdinalIgnoreCase);
}
