using ChipSharp.Abstractions;
using ChipSharp.Tests.Backends;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Ch32v003;

/// <summary>
/// The samples and the test corpora (<see cref="ConformancePrograms"/>) for the CH32V003, up to the linked .elf: every
/// program that makes no object compiles with the llvm code generator for riscv32 with the ilp32e ABI, and links with
/// the riscv32e and ch32v003 runtimes (and the tests' console, <see cref="Ch32v003Build.ConsoleSink"/>) into the 16 KB
/// of flash and the 2 KB of SRAM, or, one of <see cref="TooLarge"/>, is refused by the build for its size, with the
/// memory report's numbers; one that makes objects is refused, as the chip has no heap, with the whole-program pass
/// saying so place by place. The backend conformance suite runs them in chipsim's ch32v003 against .NET
/// (<see cref="BackendConformanceTests"/>).
/// </summary>
public sealed class Ch32v003ProgramTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    /// <summary>
    /// The programs without objects whose code and constants need more than the 16 KB of flash: the text of doubles,
    /// dates and times (DateTime's and TimeSpan's formats), the transcendental functions, and a sort of doubles through
    /// a comparer.
    /// </summary>
    public static readonly IReadOnlySet<string> TooLarge = new HashSet<string>(StringComparer.Ordinal)
    {
        "ArrayMethods/Sorting", "DateTime/Members", "DateTime/Text", "TimeSpan/Members", "samples/MathFunctions",
    };

    public static TheoryData<string> ProgramNames => [.. ConformancePrograms.All.Select(program => program.Name)];

    [Ch32v003Theory]
    [MemberData(nameof(ProgramNames))]
    public void Each_program_without_objects_links_into_flash_and_SRAM_or_is_refused_for_its_size_or_its_objects(string name)
    {
        var file = name.Replace('/', '_');
        var assembly = CSharpCompiler.Compile(ConformancePrograms.Source(name), file);
        var target = Ch32v003Build.WithConsole();
        var check = Ch32v003Build.Check(assembly, file, target);
        if (!check.IsSupported)
        {
            Assert.All(check.Groups, group => Assert.Matches("needs? a heap, and the target 'ch32v003-evt' " +
                @"\(2 KB RAM\) has no heap", group.Line));
            Assert.DoesNotContain(name, TooLarge);
            return;
        }

        if (TooLarge.Contains(name))
        {
            var error = Assert.Throws<ChipSharpException>(() => Ch32v003Build.Build(assembly, file, Path.Combine(_scratch, "ch32"), target));
            Assert.Matches(@"^\S+ does not fit in target 'ch32v003-evt': its code, constants and the starting values of its " +
                @"variables need [0-9.]+ KB of flash, [0-9.]+ KB more than the target's 16 KB", error.Message);
            return;
        }

        var program = Ch32v003Build.Build(assembly, file, Path.Combine(_scratch, "ch32"), target);

        var elf = program.File;
        Assert.Equal(elf.Symbols["chipsharp_reset"], elf.Entry);
        Assert.Equal(Ch32v003Build.FlashStart, elf.Symbols["ch32v003_vector_table"]);
        Assert.All(elf.Segments.Where(s => s.Data.Length > 0), segment =>
            Assert.InRange(segment.PhysicalAddress + (uint)segment.Data.Length, Ch32v003Build.FlashStart, Ch32v003Build.FlashEnd));
        Assert.InRange(elf.Symbols["__chipsharp_bss_end"], Ch32v003Build.SramStart, Ch32v003Build.SramEnd - 512);
        Assert.Contains("chipsharp_entry", elf.Symbols.Keys);
    }
}
