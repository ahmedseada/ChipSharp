using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Build;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Avr;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Narrowing;

/// <summary>
/// Narrowing changes no result: the programs of <see cref="NarrowingPrograms"/> and the samples, with their arithmetic
/// narrowed, print on the PC and on the simulated Pico (where narrowing is turned on for the test, as its targets leave
/// it off) exactly what .NET prints, and on the Uno (where it is on) Main returns what .NET's Result returns, on the C#
/// AVR core and in avr8js. And it is worth it there: the samples take less flash.
/// </summary>
public sealed class NarrowingProgramTests(ITestOutputHelper output) : IDisposable
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

    // Random100, Random108 and Random114: LLVM 22 got their large functions wrong on the AVR, narrowed or not (see
    // the code generator's LlvmAvrCalls), until each large function with a loop stayed out of line
    public static TheoryData<string> Programs =>
        ["Edges", "Random1", "Random2", "Random3", "Random4", "Random100", "Random108", "Random114"];

    [HostTheory]
    [MemberData(nameof(Programs))]
    public async Task On_the_PC_each_program_prints_with_its_arithmetic_narrowed_what_dotnet_prints(string name)
    {
        var source = Source(name, returnsResult: false);
        var assembly = CSharpCompiler.Compile(source, name);
        var dotnet = await HostRun.RunOnDotnetAsync(assembly, name, Path.Combine(_scratch, "dotnet"));
        var module = IlToTir.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, name + ".dll"), Narrowed(Targets.Load("host"))));
        AssertNarrowed(module);

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "host"));

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text, run.Text);
    }

    [SimulatorTheory]
    [MemberData(nameof(Programs))]
    public async Task On_the_Pico_each_program_prints_with_its_arithmetic_narrowed_what_dotnet_prints(string name)
    {
        var source = Source(name, returnsResult: false);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(source, name, Path.Combine(_scratch, "pico"), Narrowed(PicoBuild.Pico));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 30_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.ASCII.GetString(run.Uart0));
    }

    [AvrTheory]
    [MemberData(nameof(Programs))]
    public void On_the_Uno_where_narrowing_is_on_each_program_returns_what_dotnet_returns(string name)
    {
        var source = Source(name, returnsResult: true);
        var (_, elf, tir) = BuildForUno(name, source);
        Assert.Matches(@"= (add|sub|mul|ult|ule|udiv|urem|and|or|xor) i8 ", tir);

        var program = AvrCoreProgram.FromElf(elf);
        var cpu = program.Load();

        Assert.Equal(AvrCpu.StopReason.Sleep, cpu.Run(400_000_000));
        Assert.Equal(DotnetResult(source, name), BitConverter.ToInt32(cpu.Data, program.Address("chipsharp_exit_code")));
    }

    /// <summary>
    /// One operation on 400 pseudo-random pairs on the Uno, hashed: of ints, and of bytes, sbytes, shorts and ushorts,
    /// which narrowing divides in 8 and 16 bits (the 8- and 16-bit helpers, signed and unsigned), as .NET does.
    /// </summary>
    [AvrTheory]
    [InlineData("a / b", "int")]
    [InlineData("a % b", "int")]
    [InlineData("a * b", "int")]
    [InlineData("a << (b & 31)", "int")]
    [InlineData("a >> (b & 31)", "int")]
    [InlineData("a >>> (b & 31)", "int")]
    [InlineData("(int)((uint)a / (uint)b)", "int")]
    [InlineData("(int)((uint)a % (uint)b)", "int")]
    [InlineData("(short)a / (short)b", "short")]
    [InlineData("(short)a % (short)b", "short")]
    [InlineData("(sbyte)a / (sbyte)b", "sbyte")]
    [InlineData("(sbyte)a % (sbyte)b", "sbyte")]
    [InlineData("(ushort)a / (ushort)b", "ushort")]
    [InlineData("(byte)a % (byte)b", "byte")]
    public void On_the_Uno_each_operation_of_each_width_gives_what_dotnet_gives(string expression, string type)
    {
        var source = $$"""
            using System;

            class Program
            {
                static volatile int input = 1;

                public static int Result()
                {
                    unchecked
                    {
                        uint x = (uint)input * 2463534242u;
                        int hash = 0;
                        for (int k = 0; k < 400; k++)
                        {
                            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                            int a = (int)x;
                            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                            int b = (int)(x >> (int)(x & 31));
                            if (({{type}})b == 0 || (({{type}})b == -1 && ({{type}})a == {{type}}.MinValue)) continue;
                            hash = hash * 31 + ({{expression}});
                        }

                        return hash;
                    }
                }

                static int Main() => Result();
            }
            """;
        var name = "Arithmetic" + string.Concat(expression.Where(char.IsAsciiLetterOrDigit));
        var (_, elf, _) = BuildForUno(name, source);
        var program = AvrCoreProgram.FromElf(elf);
        var cpu = program.Load();
        Assert.Equal(AvrCpu.StopReason.Sleep, cpu.Run(400_000_000));
        Assert.Equal(DotnetResult(source, name), BitConverter.ToInt32(cpu.Data, program.Address("chipsharp_exit_code")));
    }

    [AvrSimulatorFact]
    public async Task In_the_avr_simulator_the_narrowed_edges_finish_as_on_the_AVR_core()
    {
        var (hex, _, _) = BuildForUno("Edges", NarrowingPrograms.Edges(returnsResult: true));

        var run = await AvrSimulator.RunAsync(hex, 10_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("the program has finished", run.Stopped);
    }

    [HostFact]
    public async Task On_the_PC_every_sample_prints_with_its_arithmetic_narrowed_what_dotnet_prints()
    {
        var host = Narrowed(Targets.Load("host"));
        var failures = await Concurrently.SelectAsync(Samples.Names, async name =>
        {
            var dll = Samples.Compile(name, Path.Combine(_scratch, "dll"));
            var module = IlToTir.Lower(WholeProgramPass.Analyze(IlAssembly.Load(dll), host));
            var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, name));
            return run.ExitCode != 0 || !run.Output.SequenceEqual(Samples.ExpectedOutput(name)) ? $"{name}: {run}" : null;
        });

        Assert.Empty(failures.OfType<string>());
    }

    /// <summary>
    /// The samples that compile for the Uno (a console added, which the target is to get), their LLVM compiled by Zig at
    /// -Os with narrowing and without: the code of the programs, without the runtime. ForLoops, all counters and sums of
    /// small numbers, takes much less; none of them takes more.
    /// </summary>
    [AvrFact]
    public async Task On_the_Uno_narrowing_makes_the_samples_code_smaller()
    {
        var on = AvrCode.Uno();
        var off = AvrCode.Uno(("narrowing", "false"));
        var sizes = (await Concurrently.SelectAsync(Samples.Names, async name =>
        {
            var dll = Samples.Compile(name, Path.Combine(_scratch, "dll"));
            if (!WholeProgramPass.Check(IlAssembly.Load(dll), on).IsSupported)
            {
                return ((string Name, long Off, long On)?)null; // it needs a heap
            }

            return (name, await CodeSize(dll, off, name + ".off"), await CodeSize(dll, on, name + ".on"));
        })).OfType<(string Name, long Off, long On)>().ToList();

        foreach (var (name, before, after) in sizes)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name,-16} {before,6} {after,6}"));
        }

        var (totalOff, totalOn) = (sizes.Sum(size => size.Off), sizes.Sum(size => size.On));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{"total",-16} {totalOff,6} {totalOn,6}"));
        Assert.True(sizes.Count >= 20, $"{sizes.Count} samples compile for the Uno");
        Assert.All(sizes, size => Assert.True(size.On <= size.Off, $"{size.Name}: {size.Off} bytes, {size.On} narrowed"));
        var loops = sizes.Single(size => size.Name == "ForLoops");
        Assert.True(loops.On * 10 < loops.Off * 7, $"ForLoops: {loops.Off} bytes, {loops.On} narrowed");
        Assert.True(totalOn * 100 < totalOff * 97, $"{totalOff} bytes, {totalOn} narrowed");
    }

    private static string Source(string name, bool returnsResult) => name == "Edges"
        ? NarrowingPrograms.Edges(returnsResult)
        : NarrowingPrograms.Random(int.Parse(name["Random".Length..], CultureInfo.InvariantCulture), 12, returnsResult);

    /// <summary><paramref name="target"/> with narrowing turned on.</summary>
    internal static TargetDescription Narrowed(TargetDescription target) => new()
    {
        Name = target.Name,
        InheritanceChain = target.InheritanceChain,
        Backend = target.Backend,
        Toolchain = target.Toolchain,
        Runtime = target.Runtime,
        Triple = target.Triple,
        Cpu = target.Cpu,
        PointerSize = target.PointerSize,
        Endian = target.Endian,
        Image = target.Image,
        Flasher = target.Flasher,
        Memory = target.Memory,
        Pins = target.Pins,
        Capabilities = new Dictionary<string, string>(target.Capabilities, StringComparer.OrdinalIgnoreCase)
        {
            ["narrowing"] = "true",
        },
        HeapSize = target.HeapSize,
        Settings = target.Settings,
        MissingValues = target.MissingValues,
    };

    /// <summary>The program's own functions do arithmetic and comparisons of 8 or 16 bits.</summary>
    private static void AssertNarrowed(TirModule module)
    {
        var narrowed = module.Functions.Where(function => function.Name.StartsWith("Program.", StringComparison.Ordinal))
            .SelectMany(function => function.Blocks).SelectMany(block => block.Instructions)
            .Count(instruction => instruction is TirBinary { Type.Kind: TirTypeKind.I8 or TirTypeKind.I16 }
                or TirCompare { Left.Type.Kind: TirTypeKind.I8 or TirTypeKind.I16 });
        Assert.True(narrowed >= 10, $"{narrowed} operations are narrowed");
    }

    /// <summary>The program built for the Uno as <c>chipsharp build -t uno</c> builds it: its .hex, .elf and TIR.</summary>
    private (string Hex, string Elf, string Tir) BuildForUno(string name, string source)
    {
        var dll = CSharpCompiler.CompileToFile(source, Path.Combine(_scratch, "uno-dll", name + ".dll"));
        var result = ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = "uno",
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = Path.Combine(_scratch, "uno", name),
            KeepIntermediates = true,
        });
        var elf = Assert.Single(result.IntermediatePaths, path => path.EndsWith(".elf", StringComparison.Ordinal));
        var tir = Assert.Single(result.IntermediatePaths, path => path.EndsWith(".tir", StringComparison.Ordinal));
        return (result.OutputPath, elf, File.ReadAllText(tir));
    }

    /// <summary>What the program's Result returns on .NET.</summary>
    private int DotnetResult(string source, string name)
    {
        var dll = CSharpCompiler.CompileToFile(source, Path.Combine(_scratch, "dotnet-result", name + ".dll"));
        var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(dll));
        return (int)assembly.GetType("Program")!.GetMethod("Result")!.Invoke(null, null)!;
    }

    /// <summary>The bytes of code the program's LLVM compiles to for the Uno, its functions' sections summed.</summary>
    private async Task<long> CodeSize(string dll, TargetDescription target, string name)
    {
        var module = ChipSharpBuilder.Translate(dll, target);
        var ll = Assert.Single(new LlvmCodeGenerator().Generate(module, target).Files);
        var folder = Path.Combine(_scratch, "sizes");
        var objectFile = await AvrCode.CompileAsync(folder, name + ".ll", ll.Content, "-Os");
        var result = await Llvm.LlvmTools.RunAsync(PicoTools.LlvmTool("llvm-size"), ["-A", objectFile], folder);
        Assert.True(result.ExitCode == 0, result.ToString());
        return result.Output.Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[0].StartsWith(".text", StringComparison.Ordinal))
            .Sum(parts => long.Parse(parts[1], CultureInfo.InvariantCulture));
    }
}
