using ChipSharp.Abstractions;
using ChipSharp.Tests.Backends;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// The samples and the test corpora (<see cref="ConformancePrograms"/>) for the ESP32-C5, up to the linked .elf: every
/// program that makes no object compiles with the llvm code generator for riscv32, and links with the riscv32 and
/// esp32c5 runtimes (and the tests' console, <see cref="Esp32c5Build.ConsoleSink"/>) into SRAM; one that makes objects is
/// refused, as the chip has no heap yet, with the whole-program pass saying so place by place. They run once the
/// chip's simulator and runtime come (M4's wave 1: the backend conformance suite's slot for the chip).
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
    public void Each_program_without_objects_links_into_SRAM_and_each_with_objects_is_refused_for_the_heap(string name)
    {
        var file = name.Replace('/', '_');
        var assembly = CSharpCompiler.Compile(ConformancePrograms.Source(name), file);
        var target = Esp32c5Build.WithConsole();
        var check = Esp32c5Build.Check(assembly, file, target);
        if (!check.IsSupported)
        {
            Assert.All(check.Groups, group => Assert.Matches("needs? a heap, and the target 'esp32c5-devkitc' " +
                @"\([0-9.]+ KB RAM\) has no heap", group.Line));
            return;
        }

        var program = Esp32c5Build.Build(assembly, file, Path.Combine(_scratch, "esp32c5"), target);

        var elf = program.File;
        Assert.Equal(elf.Symbols["chipsharp_reset"], elf.Entry);
        Assert.All(elf.Segments.Where(s => s.Data.Length > 0), segment =>
            Assert.InRange(segment.PhysicalAddress + (uint)segment.Data.Length, Esp32c5Build.SramStart, Esp32c5Build.LoadEnd));
        Assert.Contains("chipsharp_entry", elf.Symbols.Keys);
    }
}
