using ChipSharp.Tests.CBackend;
using ChipSharp.Tests.Simulators;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Sdcc;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// The narrowing pass on SDCC's chips, whose target files turn it on ("narrowing": true): each program of the corpora
/// that a chip takes, built through C and SDCC with its arithmetic narrowed to 8 and 16 bits where that gives C#'s
/// result and without, takes less program memory narrowed, all of them together. The numbers are in the target files
/// and the README.
/// </summary>
public sealed class SdccNarrowingTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> Chips => [.. BackendTargets.All.Where(target => target.Chip is not null).Select(target => target.Name)];

    [SdccTheory]
    [RunsLong]
    [MemberData(nameof(Chips))]
    public async Task Narrowed_the_programs_a_chip_takes_need_less_program_memory(string chip)
    {
        var narrowed = BackendTargets.Get(chip).Load();
        Assert.Equal("true", narrowed.Capabilities["narrowing"]);
        var wide = CSdcc.With(narrowed, "narrowing", "false");
        var sizes = await Concurrently.SelectAsync(ConformancePrograms.All, program => Task.Run(() =>
        {
            var file = program.Name.Replace('/', '_');
            var (_, small, _) = BackendRun.BuildForChip(narrowed, file, program.Source, Path.Combine(_scratch, "narrowed", file));
            var (_, large, _) = BackendRun.BuildForChip(wide, file, program.Source, Path.Combine(_scratch, "wide", file));
            return small is null || large is null
                ? ((string Name, ulong Narrowed, ulong Wide)?)null
                : (program.Name, SdccMemoryUse.Read(small, narrowed).CodeUsed, SdccMemoryUse.Read(large, wide).CodeUsed);
        }));

        var built = sizes.OfType<(string Name, ulong Narrowed, ulong Wide)>().ToList();
        foreach (var (name, small, large) in built)
        {
            output.WriteLine($"{name,-40} {small,6} {large,6}");
        }

        var (narrowedTotal, wideTotal) = ((ulong)built.Sum(size => (long)size.Narrowed), (ulong)built.Sum(size => (long)size.Wide));
        output.WriteLine($"{built.Count} programs: {narrowedTotal} bytes narrowed, {wideTotal} without");
        Assert.NotEmpty(built);
        Assert.True(narrowedTotal < wideTotal, $"narrowed {narrowedTotal} bytes, without {wideTotal}");
    }
}
