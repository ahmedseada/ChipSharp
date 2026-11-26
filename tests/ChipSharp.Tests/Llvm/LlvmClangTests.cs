using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Llvm;

/// <summary>LLVM itself accepts what the generator writes: clang compiles it for a PC, a Cortex-M0+ and a RISC-V chip.</summary>
public sealed class LlvmClangTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public LlvmClangTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData("host", "-O0")]
    [InlineData("host", "-O2")]
    [InlineData("pico", "-O0")]
    [InlineData("pico", "-O2")]
    [InlineData("riscv32", "-O0")]
    [InlineData("riscv32", "-O2")]
    public async Task Clang_compiles_every_instruction_kind_on_every_type(string targetName, string optimization)
    {
        // -O0 makes machine code for every function as written; -O2 runs the optimizer over it first
        // (and drops what chipsharp_entry does not reach: every function but chipsharp_entry is internal)
        var (target, flags) = targetName switch
        {
            "host" => (LlvmTargets.Host, Array.Empty<string>()),
            "pico" => (LlvmTargets.Pico, new[] { $"-mcpu={LlvmTargets.Pico.Cpu}" }),
            _ => (LlvmTargets.Riscv32, new[] { "-march=rv32imac" }),
        };

        var objectFile = await CompileAsync(LlvmSamples.Everything(), target, [.. flags, optimization]);

        Assert.True(new FileInfo(objectFile).Length > 0);
    }

    /// <summary>
    /// Writes the module's .ll and compiles it with -c for the target's triple, treating warnings as errors, except
    /// the one clang always gives because it spells the triple in full (e.g. thumbv6m-unknown-none-eabi).
    /// </summary>
    private async Task<string> CompileAsync(TirModule module, TargetDescription target, string[] flags)
    {
        var ll = Assert.Single(new LlvmCodeGenerator().Generate(module, target).Files);
        await File.WriteAllTextAsync(Path.Combine(_scratch, ll.FileName), ll.Content);

        await LlvmTools.ClangAsync(_scratch,
            [$"--target={target.Triple}", .. flags, "-Werror", "-Wno-override-module", "-c", ll.FileName, "-o", "out.o"]);
        return Path.Combine(_scratch, "out.o");
    }
}
