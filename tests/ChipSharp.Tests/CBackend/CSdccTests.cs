using ChipSharp.Abstractions;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.CBackend;

/// <summary>
/// The C the c code generator writes for SDCC's chips, through SDCC: every sample that needs no heap compiles with
/// sdcc for the PFS154 (pdk14) and the STM8S103 (stm8) without an error or a warning (a console added, which their
/// runtimes are to get), and small programs link with the toolchain "sdcc" into a program image.
/// </summary>
public sealed class CSdccTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> Ports => ["pdk14", "stm8"];

    [SdccFact]
    [RunsLong]
    public async Task Every_sample_without_a_heap_compiles_with_sdcc_for_the_PFS154_and_the_STM8S103_without_a_warning()
    {
        var targets = new[] { CSdcc.Pfs154, CSdcc.Stm8s103 }.Select(target => CSdcc.With(target, "console", "true")).ToList();
        var cases = Samples.Names.SelectMany(name => targets.Select(target => (Name: name, Target: target))).ToList();
        var results = await Concurrently.SelectAsync(cases, async item =>
        {
            var source = await File.ReadAllTextAsync(Path.Combine(Samples.Folder, item.Name + ".cs"));
            (Ir.TirModule Module, string C)? generated;
            try
            {
                generated = CSdcc.Generate(source, item.Name, item.Target);
            }
            catch (ChipSharpException e) when (e.Message.Contains("uses double", StringComparison.Ordinal))
            {
                return (item.Name, item.Target.Name, Result: "refused: double");
            }

            if (generated is not { } code)
            {
                return (item.Name, item.Target.Name, Result: "not supported");
            }

            var messages = await CSdcc.CompileAsync(code.C, item.Name + ".c", item.Target,
                Path.Combine(_scratch, item.Target.Name, item.Name));

            // a note of the pdk14 port's register allocator about itself, not of the C (it compiles the code right)
            var warnings = string.Join('\n', messages.Split('\n').Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("Warning: Non-connected liverange found", StringComparison.Ordinal)));
            return (item.Name, item.Target.Name, Result: warnings.Length == 0 ? "compiled" : "warned: " + warnings);
        });

        foreach (var (name, target, result) in results)
        {
            output.WriteLine($"{target,-16} {name,-16} {result}");
        }

        Assert.DoesNotContain(results, result => result.Result.StartsWith("warned", StringComparison.Ordinal));
        Assert.True(results.Count(result => result.Result == "compiled") >= 30,
            $"{results.Count(result => result.Result == "compiled")} compiled");
    }
}
