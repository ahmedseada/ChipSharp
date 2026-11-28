using ChipSharp.Tests.Backends;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// The samples and the test corpora (<see cref="ConformancePrograms"/>) for the ESP32-C5, up to the linked .elf: every
/// program, with or without objects, compiles with the llvm code generator for riscv32, and links with the riscv32 and
/// esp32c5 runtimes (the console, the heap and the garbage collector among them) into SRAM. They run in the chip's
/// simulator in the backend conformance suite's slot for the chip.
/// </summary>
public sealed class Esp32c5ProgramTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string> ProgramNames => [.. ConformancePrograms.All.Select(program => program.Name)];

    [Esp32c5Theory]
    [MemberData(nameof(ProgramNames))]
    public void Each_program_links_into_SRAM_with_the_runtime(string name)
    {
        var file = name.Replace('/', '_');
        var assembly = CSharpCompiler.Compile(ConformancePrograms.Source(name), file);
        var target = Esp32c5Build.Board;
        var check = Esp32c5Build.Check(assembly, file, target);
        Assert.True(check.IsSupported, string.Join("\n", check.Groups.Select(group => group.Line)));

        var program = Esp32c5Build.Build(assembly, file, Path.Combine(_scratch, "esp32c5"), target);

        var elf = program.File;
        Assert.Equal(elf.Symbols["chipsharp_reset"], elf.Entry);
        Assert.All(elf.Segments.Where(s => s.Data.Length > 0), segment =>
            Assert.InRange(segment.PhysicalAddress + (uint)segment.Data.Length, Esp32c5Build.SramStart, Esp32c5Build.LoadEnd));
        Assert.Contains("chipsharp_entry", elf.Symbols.Keys);
    }
}
