using System.Globalization;
using System.Text;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Simulators;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// A differential fuzz test of the code generators: random programs of arithmetic (<see cref="FuzzPrograms"/>), from a
/// fixed set of seeds, built for the PC through the llvm code generator (host) and through the C one and clang (host-c),
/// and run, print what .NET prints for them, byte for byte; smaller ones, of the integers up to 32 bits, also through C
/// and SDCC on the simulated STM8S103, in chipsim and in SDCC's own uCsim (<see cref="UcsimStm8"/>), an STM8 core no
/// one here wrote. So every integer width, float and double, C#'s promotions and conversions (a float out of an
/// integer's range, NaN, a negative made a uint) and shifts by any amount are checked against .NET itself, not against
/// what a test's author thinks C# does. To try more seeds: CHIPSHARP_FUZZ_SEEDS=first-last (e.g. 1-500), for both.
/// </summary>
public sealed class DifferentialFuzzTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<int> PcSeeds => Seeds(1, 24);

    public static TheoryData<int> Stm8Seeds => Seeds(1, 6);

    [HostTheory]
    [MemberData(nameof(PcSeeds))]
    public async Task A_random_program_prints_through_each_backend_on_the_PC_what_dotnet_prints(int seed)
    {
        var source = FuzzPrograms.Make(seed, FuzzPrograms.Pc);
        foreach (var target in new[] { "host", "host-c" })
        {
            await PrintsWhatDotnetPrints(target, seed, source);
        }
    }

    [SdccTheory]
    [MemberData(nameof(Stm8Seeds))]
    public async Task A_random_program_of_integers_prints_on_the_simulated_STM8_in_chipsim_and_in_uCsim_what_dotnet_prints(
        int seed)
    {
        var source = FuzzPrograms.Make(seed, FuzzPrograms.Stm8);
        var dotnet = await PrintsWhatDotnetPrints("stm8s103-board", seed, source);
        if (UcsimStm8.Path is null)
        {
            return; // an SDCC without its simulator: chipsim's run is the check
        }

        // the same program on SDCC's own STM8 core
        var name = "Fuzz" + seed.ToString(CultureInfo.InvariantCulture);
        var (module, binary, skip) = BackendRun.BuildForChip(BackendTargets.Get("stm8s103-board").Load(), name, source,
            Path.Combine(_scratch, "ucsim"), UcsimStm8.ReportFile);
        Assert.True(skip is null, $"seed {seed} for uCsim: {skip?.Why}");
        var (output, failure, ended) = UcsimStm8.Run(binary!.Path, module!, TimeSpan.FromMinutes(2));
        Assert.True(failure is null && ended, $"seed {seed} in uCsim: {(failure is null ? "it did not end" : "stopped by " + failure)}");
        Same(dotnet, Encoding.UTF8.GetString(output), $"seed {seed} in uCsim", source);
    }

    /// <summary>Runs the program on <paramref name="target"/> and checks that it prints what .NET does; .NET's text.</summary>
    private async Task<string> PrintsWhatDotnetPrints(string target, int seed, string source)
    {
        var name = "Fuzz" + seed.ToString(CultureInfo.InvariantCulture);
        var outcome = await BackendRun.RunAsync(BackendTargets.Get(target), name, source, Path.Combine(_scratch, target));

        Assert.True(outcome.Skipped is null, $"seed {seed} on {target} is skipped: {outcome.Skipped}\n{outcome.Why}");
        var (run, dotnet) = (outcome.Run!, outcome.Dotnet!);
        Assert.True(run.ExitCode == 0, $"seed {seed} on {target}: {run}");
        Same(dotnet.Text, run.Text, $"seed {seed} on {target}", source);
        return dotnet.Text;
    }

    /// <summary>Checks that <paramref name="actual"/> is .NET's text, line by line, saying where it is not.</summary>
    private static void Same(string expected, string actual, string what, string source)
    {
        var (want, got) = (expected.Split('\n'), actual.Split('\n'));
        for (var line = 0; line < Math.Max(want.Length, got.Length); line++)
        {
            var (a, b) = (line < want.Length ? want[line] : "(nothing)", line < got.Length ? got[line] : "(nothing)");
            Assert.True(a == b, $"{what}, line {line + 1}: .NET prints {a.TrimEnd()}, ChipSharp {b.TrimEnd()}\n\n{source}");
        }
    }

    /// <summary>The seeds from <paramref name="first"/> to <paramref name="last"/>, or CHIPSHARP_FUZZ_SEEDS's.</summary>
    private static TheoryData<int> Seeds(int first, int last)
    {
        if (Environment.GetEnvironmentVariable("CHIPSHARP_FUZZ_SEEDS") is { } range && range.Split('-') is [var from, var to])
        {
            (first, last) = (int.Parse(from, CultureInfo.InvariantCulture), int.Parse(to, CultureInfo.InvariantCulture));
        }

        var data = new TheoryData<int>();
        for (var seed = first; seed <= last; seed++)
        {
            data.Add(seed);
        }

        return data;
    }
}
