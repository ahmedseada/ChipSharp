using ChipSharp.Tests.Esp32c5;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Simulators;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// The backends are interchangeable: every program of every corpus (<see cref="ConformancePrograms"/>) built for every
/// target of <see cref="BackendTargets"/> prints, byte for byte, what .NET prints for it. On the PC through the llvm
/// code generator (host) and the C one (host-c), as chipsharp build builds; on the simulated PFS154 and STM8S103
/// through C and SDCC, with the tests' startup and console (<see cref="ChipConsole"/>); on the simulated ESP32-C5 as
/// chipsharp build builds it, and on the simulated CH32V003 through llvm with the shared console and the tests' own
/// end of it (<see cref="BackendRun"/>). A program a target cannot take
/// (no heap, a double of 32 bits, too little memory, a runtime function the chip's runtime is still to have) is
/// skipped exactly for the reason <see cref="BackendSkips"/> gives, and only then; one that prints otherwise by what the
/// chip is (16-bit nint, big-endian memory) runs to its end and prints as many lines, as its list says.
/// </summary>
public sealed class BackendConformanceTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string, string> OnThePc => Cases(target => target.Chip is null);

    public static TheoryData<string, string> OnTheChips => Cases(target => target.IsSdcc);

    public static TheoryData<string, string> OnTheEsp32c5 => Cases(target => target.Chip == "esp32c5");

    public static TheoryData<string, string> OnTheCh32v003 => Cases(target => target.Chip == "ch32v003");

    [HostTheory]
    [MemberData(nameof(OnThePc))]
    public Task Each_program_through_each_backend_on_the_PC_prints_what_dotnet_prints(string target, string program) =>
        PrintsWhatDotnetPrints(target, program);

    [SdccTheory]
    [MemberData(nameof(OnTheChips))]
    public Task Each_program_through_C_and_SDCC_on_each_simulated_chip_prints_what_dotnet_prints(string target,
        string program) => PrintsWhatDotnetPrints(target, program);

    [Esp32c5Theory]
    [MemberData(nameof(OnTheEsp32c5))]
    public Task Each_program_through_llvm_on_the_simulated_ESP32_C5_prints_what_dotnet_prints(string target, string program) =>
        PrintsWhatDotnetPrints(target, program);

    [Ch32v003.Ch32v003Theory]
    [MemberData(nameof(OnTheCh32v003))]
    public Task Each_program_through_llvm_on_the_simulated_CH32V003_prints_what_dotnet_prints(string target, string program) =>
        PrintsWhatDotnetPrints(target, program);

    /// <summary>
    /// The skips and the differences name only targets and programs there are (one taken out of the corpora leaves the
    /// lists too), and only chips: on the PC every backend runs every program as .NET does.
    /// </summary>
    [Fact]
    public void The_skips_name_chips_and_programs_there_are()
    {
        var programs = ConformancePrograms.All.Select(program => program.Name).ToHashSet(StringComparer.Ordinal);
        var chips = BackendTargets.All.Where(target => target.Chip is not null).Select(target => target.Name).ToList();
        foreach (var (target, names) in BackendSkips.ByTarget.Select(pair => (pair.Key, pair.Value.Keys))
                     .Concat(BackendSkips.Differences.Select(pair => (pair.Key, pair.Value.Keys))))
        {
            Assert.Contains(target, chips);
            Assert.All(names, program => Assert.Contains(program, programs));
        }

        Assert.Empty(BackendSkips.ByTarget.SelectMany(pair => pair.Value.Keys.Intersect(
            BackendSkips.Differences.GetValueOrDefault(pair.Key)?.Keys ?? [])));
    }

    private async Task PrintsWhatDotnetPrints(string target, string program)
    {
        var outcome = await BackendRun.RunAsync(BackendTargets.Get(target), program, ConformancePrograms.Source(program),
            _scratch);

        var expected = BackendSkips.For(target, program);
        Assert.True(expected == outcome.Skipped, expected is null
            ? $"{program} on {target} is skipped: {outcome.Skipped}\n{outcome.Why}"
            : $"{program} on {target} should be skipped ({expected}), but {(outcome.Skipped is null ? "it runs" : "for " + outcome.Skipped)}" +
              $"\n{outcome.Why}");
        if (outcome.Skipped is not null)
        {
            return;
        }

        var (run, dotnet) = (outcome.Run!, outcome.Dotnet!);
        Assert.True(run.ExitCode == 0, $"{program} on {target}: {run}");
        if (BackendSkips.DifferenceOf(target, program) is { } why)
        {
            // it runs to the end, and prints as many lines, but otherwise: the list says why
            Assert.Equal(dotnet.Text.Split('\n').Length, run.Text.Split('\n').Length);
            Assert.True(dotnet.Text != run.Text, $"{program} on {target} prints what .NET prints now: take it off the " +
                $"differences ({why})");
            return;
        }

        Assert.Equal(dotnet.Text, run.Text);
        Assert.Equal(dotnet.Output, run.Output);
    }

    private static TheoryData<string, string> Cases(Func<BackendTarget, bool> which)
    {
        var data = new TheoryData<string, string>();
        foreach (var target in BackendTargets.All.Where(which))
        {
            foreach (var (name, _) in ConformancePrograms.All)
            {
                data.Add(target.Name, name);
            }
        }

        return data;
    }
}
