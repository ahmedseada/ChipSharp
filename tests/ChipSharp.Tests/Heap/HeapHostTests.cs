using ChipSharp.Targets;
using ChipSharp.Tests.Host;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Heap;

/// <summary>
/// The heap and the garbage collector on the PC: hand-written TIR built with the windows-x64 runtime, for the host target
/// and for ones that inherit it with a small heap, so that the collector runs many times.
/// </summary>
public sealed class HeapHostTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
    private TargetLoader? _targets;

    private TargetLoader Targets => _targets ??= HeapRun.Targets(Path.Combine(_scratch, "targets"), "64K", "16K", "1M");

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [HostFact]
    public async Task A_million_short_lived_objects_reuse_a_small_heap_while_lists_trees_and_cycles_survive_every_collection()
    {
        const int Churn = 1_000_000;
        var result = await HeapRun.OnHostAsync(HeapPrograms.Survivors(Churn, 100, 8), Build("survivors"),
            Targets.Load("host-64K"));
        output.WriteLine($"PC, 64 KB heap: {Churn} nodes made in {result.Time.TotalMilliseconds:0} ms\n{result}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(99 * 100 / 2, result.Number(0));        // the list in a static field
        Assert.Equal(HeapPrograms.SumOfTree(8), result.Number(1)); // the tree in a local
        Assert.Equal(3000, result.Number(2));                // the cycle
        var (collections, allocated, inUse, heap) = (result.Number(3), result.Number(4), result.Number(5), result.Number(6));
        Assert.InRange(heap, (64 << 10) * 15 / 16, 64 << 10);
        Assert.True(allocated >= Churn * 32L, $"{allocated} bytes made");
        Assert.True(collections >= allocated / heap, $"{collections} collections for {allocated} bytes in a heap of {heap}");
        Assert.InRange(inUse, 0, heap);
    }

    [HostFact]
    public async Task Objects_held_only_by_a_caller_across_a_call_that_collects_survive_also_deep_in_a_recursion()
    {
        var result = await HeapRun.OnHostAsync(HeapPrograms.AcrossCalls(200_000, 200), Build("calls"), Targets.Load("host-16K"));
        output.WriteLine(result.ToString());

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(["11111", "22222", "33333", "44444", (200 * 201 / 2).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            result.Lines.Take(5));
        Assert.True(result.Number(5) >= 2 * 200_000 * 32L / (16 << 10), $"{result.Number(5)} collections");
    }

    [HostFact]
    public async Task Objects_reachable_only_from_array_elements_struct_fields_and_static_structs_survive_and_the_mark_stack_overflows_safely()
    {
        const int Count = 3000; // more than the 1024 objects the mark stack holds
        var result = await HeapRun.OnHostAsync(HeapPrograms.Arrays(Count, 1_000_000), Build("arrays"), Targets.Load("host-1M"));
        output.WriteLine(result.ToString());

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(Count * (Count - 1) / 2, result.Number(0));     // the nodes of the array in a static field
        Assert.Equal(3 * Count * (Count - 1) / 2, result.Number(1)); // the pairs: their keys and their nodes
        Assert.Equal(Count * (Count - 1) / 2, result.Number(2));     // the int[], zeroed then filled
        Assert.Equal(9999, result.Number(3));                        // the node in the static struct
        Assert.Equal(100 + 2 + 4 + 8, result.Number(4));             // the holder's struct and fixed array
        Assert.Equal(Count, result.Number(5));
        Assert.Equal(Count, result.Number(6));
        Assert.True(result.Number(7) >= 20, $"{result.Number(7)} collections");
    }

    [HostFact]
    public async Task Garbage_cycles_are_collected()
    {
        var result = await HeapRun.OnHostAsync(HeapPrograms.Cycles(500_000), Build("cycles"), Targets.Load("host-16K"));
        output.WriteLine(result.ToString());

        // a million nodes in a 16 KB heap: only collected cycles make room for the next ones
        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.True(result.Number(0) > 1000, $"{result.Number(0)} collections");
        Assert.Equal(500_000L * 2 * 32, result.Number(1));
        Assert.InRange(result.Number(2), 0, 256); // after GC.Collect: at most what the stack still points to
    }

    [HostFact]
    public async Task A_heap_full_of_live_objects_stops_the_program_with_dotnets_out_of_memory_message_and_exit_code_1()
    {
        const string Message =
            "Unhandled exception. System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.\r\n";

        var full = await HeapRun.OnHostAsync(HeapPrograms.OutOfMemory(array: false), Build("full"), Targets.Load("host-64K"));
        var huge = await HeapRun.OnHostAsync(HeapPrograms.OutOfMemory(array: true), Build("huge"), Targets.Load("host"));

        Assert.Equal((1, "1", Message), full.Outcome);
        Assert.Equal((1, "1", Message), huge.Outcome);
    }

    [HostFact]
    public async Task A_null_check_of_null_and_an_array_of_a_negative_length_stop_with_dotnets_messages()
    {
        var nothing = await HeapRun.OnHostAsync(HeapPrograms.NullReference(), Build("null"), Targets.Load("host"));
        var negative = await HeapRun.OnHostAsync(HeapPrograms.NegativeLength(), Build("negative"), Targets.Load("host"));

        Assert.Equal((1, "1", "Unhandled exception. System.NullReferenceException: Object reference not set to an " +
            "instance of an object.\r\n"), nothing.Outcome);
        Assert.Equal((1, "1", "Unhandled exception. System.OverflowException: Arithmetic operation resulted in an " +
            "overflow.\r\n"), negative.Outcome);
    }

    private string Build(string name) => Path.Combine(_scratch, name);
}
