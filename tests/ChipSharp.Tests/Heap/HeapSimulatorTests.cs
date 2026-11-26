using ChipSharp.Targets;
using ChipSharp.Tests.Pico;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Heap;

/// <summary>
/// The heap and the garbage collector on the Raspberry Pi Pico, in the simulator: the same hand-written TIR as on the PC
/// (<see cref="HeapPrograms"/>), built with the rp2040 runtime, for the pico target and for ones that inherit it with a
/// small heap, so that the collector runs many times. What the program prints comes over UART0.
/// </summary>
public sealed class HeapSimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
    private TargetLoader? _targets;

    private TargetLoader Targets => _targets ??= HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K");

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [SimulatorFact]
    public async Task Short_lived_objects_reuse_a_small_heap_while_lists_trees_and_cycles_survive_every_collection()
    {
        const int Churn = 100_000;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (result, run, _) = await HeapRun.OnPicoAsync(HeapPrograms.Survivors(Churn, 100, 6), Build("survivors"),
            Targets.Load("pico-16K"));
        output.WriteLine($"Pico, 16 KB heap: {Churn} nodes made in {result.Time.TotalMilliseconds:0} ms simulated, " +
            $"{clock.ElapsedMilliseconds} ms real\n{result}\n{run.Stopped}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(99 * 100 / 2, result.Number(0));
        Assert.Equal(HeapPrograms.SumOfTree(6), result.Number(1));
        Assert.Equal(3000, result.Number(2));
        var (collections, allocated, inUse, heap) = (result.Number(3), result.Number(4), result.Number(5), result.Number(6));
        Assert.InRange(heap, (16 << 10) * 15 / 16, 16 << 10);
        Assert.True(allocated >= Churn * 16L, $"{allocated} bytes made");
        Assert.True(collections >= allocated / heap, $"{collections} collections for {allocated} bytes in a heap of {heap}");
        Assert.InRange(inUse, 0, heap);
    }

    private string Build(string name) => Path.Combine(_scratch, name);
}
