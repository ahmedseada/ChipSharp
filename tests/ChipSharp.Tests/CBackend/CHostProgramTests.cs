using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.CBackend;

/// <summary>
/// TIR programs on the values where C and C# part ways, built for the PC through each backend (host-c: the c code
/// generator and clang; host: the llvm code generator) and run: both must print what C#'s own arithmetic says
/// (<see cref="CPrograms"/>), the same, byte for byte.
/// </summary>
public sealed class CHostProgramTests : IDisposable
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string> Programs => ["IntegerArithmetic", "IntegerComparisons", "IntegerConversions", "Floats", "Memory"];

    [HostTheory]
    [MemberData(nameof(Programs))]
    public async Task Each_backend_prints_what_CSharp_computes(string name)
    {
        foreach (var target in new[] { "host-c", "host" })
        {
            // a module is generated once: each backend gets its own
            var program = Make(name);
            var run = await HostRun.BuildAndRunAsync(program.Module, Path.Combine(_scratch, target), Targets.Load(target));

            Assert.True(run.ExitCode == 0, $"{target}: {run}");
            Assert.True(program.Mismatch(run.Text) is null, $"{target}: {program.Mismatch(run.Text)}");
        }
    }

    private static CPrograms.Printer Make(string name)
    {
        var program = name switch
        {
            "IntegerArithmetic" => CPrograms.IntegerArithmetic(),
            "IntegerComparisons" => CPrograms.IntegerComparisons(),
            "IntegerConversions" => CPrograms.IntegerConversions(),
            "Floats" => CPrograms.Floats(),
            _ => CPrograms.Memory(),
        };
        program.Finish();
        return program;
    }
}
