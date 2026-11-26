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

    [SimulatorFact]
    public async Task Objects_held_only_by_a_caller_across_a_call_that_collects_survive_also_deep_in_a_recursion()
    {
        var (result, run, _) = await HeapRun.OnPicoAsync(HeapPrograms.AcrossCalls(20_000, 100), Build("calls"),
            Targets.Load("pico-16K"));
        output.WriteLine($"{result}\n{run.Stopped}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["11111", "22222", "33333", "44444", "5050"], result.Lines.Take(5));
        Assert.True(result.Number(5) >= 2 * 20_000 * 16L / (16 << 10), $"{result.Number(5)} collections");
    }

    [SimulatorFact]
    public async Task Objects_reachable_only_from_array_elements_struct_fields_and_static_structs_survive_and_the_mark_stack_overflows_safely()
    {
        const int Count = 1000; // more than the 64 objects the mark stack holds on a chip
        var (result, run, _) = await HeapRun.OnPicoAsync(HeapPrograms.Arrays(Count, 30_000), Build("arrays"),
            Targets.Load("pico"));
        output.WriteLine($"{result}\n{run.Stopped}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal([Count * (Count - 1) / 2, 3 * Count * (Count - 1) / 2, Count * (Count - 1) / 2, 9999, 114, Count, Count],
            Enumerable.Range(0, 7).Select(result.Number));
        Assert.True(result.Number(7) >= 1, $"{result.Number(7)} collections");
    }

    [SimulatorFact]
    public async Task Garbage_cycles_are_collected()
    {
        var (result, run, _) = await HeapRun.OnPicoAsync(HeapPrograms.Cycles(20_000), Build("cycles"), Targets.Load("pico-16K"));
        output.WriteLine($"{result}\n{run.Stopped}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.True(result.Number(0) > 20, $"{result.Number(0)} collections");
        Assert.Equal(20_000L * 2 * 16, result.Number(1));
        Assert.InRange(result.Number(2), 0, 128);
    }

    [SimulatorFact]
    public async Task Out_of_memory_a_null_check_of_null_and_a_negative_length_stop_at_a_breakpoint_with_dotnets_messages()
    {
        var full = await HeapRun.OnPicoAsync(HeapPrograms.OutOfMemory(array: false), Build("full"), Targets.Load("pico-16K"));
        var huge = await HeapRun.OnPicoAsync(HeapPrograms.OutOfMemory(array: true), Build("huge"), Targets.Load("pico"));
        var nothing = await HeapRun.OnPicoAsync(HeapPrograms.NullReference(), Build("null"), Targets.Load("pico"));
        var negative = await HeapRun.OnPicoAsync(HeapPrograms.NegativeLength(), Build("negative"), Targets.Load("pico"));

        const string Unhandled = "1\r\nUnhandled exception. System.";
        Assert.All(new[] { full, huge, nothing, negative }, stop =>
        {
            Assert.Equal(3, stop.Run.ExitCode);
            Assert.Contains("hit a breakpoint", stop.Run.Stopped);
        });
        Assert.Equal(Unhandled + "OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.\r\n",
            full.Result.Raw);
        Assert.Equal(full.Result.Raw, huge.Result.Raw);
        Assert.Equal(Unhandled + "NullReferenceException: Object reference not set to an instance of an object.\r\n",
            nothing.Result.Raw);
        Assert.Equal(Unhandled + "OverflowException: Arithmetic operation resulted in an overflow.\r\n", negative.Result.Raw);
    }

    [SimulatorFact]
    public async Task An_interrupt_handler_that_makes_an_object_stops_the_program_saying_why()
    {
        var (result, run, _) = await HeapRun.OnPicoAsync(HeapPrograms.AllocationInInterrupt(), Build("interrupt"),
            Targets.Load("pico"));

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("hit a breakpoint", run.Stopped);
        Assert.Equal("1\r\nUnhandled exception. System.InvalidOperationException: An interrupt handler cannot create " +
            "objects (new ...): the heap may be in the middle of an allocation or a collection when the interrupt comes. " +
            "Create what the handler needs before, e.g. in a static field.\r\n", result.Raw);
    }

    [SimulatorFact]
    public async Task The_heap_is_the_targets_heap_bytes_between_the_variables_and_the_stack_and_the_report_says_so()
    {
        var (shipped, _, program) = await HeapRun.OnPicoAsync(HeapPrograms.Cycles(10), Build("placement"), Targets.Load("pico"));
        var (small, _, smallProgram) = await HeapRun.OnPicoAsync(HeapPrograms.Cycles(10), Build("small"),
            Targets.Load("pico-16K"));
        var elf = ChipSharp.Image.Uf2.ElfFile.Read(program.Elf);
        var smallElf = ChipSharp.Image.Uf2.ElfFile.Read(smallProgram.Elf);

        // 264 KB of RAM at 0x20000000: the variables, then the heap, then the 8 KB of stack at the end
        Assert.Equal((0x20008000u, 0x20040000u), (elf.Symbols["__chipsharp_heap_start"], elf.Symbols["__chipsharp_heap_end"]));
        Assert.Equal((0x2003c000u, 0x20040000u),
            (smallElf.Symbols["__chipsharp_heap_start"], smallElf.Symbols["__chipsharp_heap_end"]));
        Assert.True(elf.Symbols["__chipsharp_bss_end"] <= elf.Symbols["__chipsharp_heap_start"]);
        Assert.Equal(0x20042000u, elf.Symbols["__chipsharp_stack_top"]);

        // what the program has for objects: the heap less its bitmap and mark stack
        Assert.InRange(shipped.Number(3), (224 << 10) * 15 / 16, 224 << 10);
        Assert.InRange(small.Number(3), (16 << 10) * 15 / 16, 16 << 10);

        var report = ChipSharp.Build.MemoryReport.Read(new ChipSharp.Abstractions.LinkedBinary(program.Elf), Targets.Load("pico"));
        Assert.Equal(224UL << 10, report.Heap);
        Assert.Matches(@"^flash \d+(\.\d)? KB of 2048 KB \(\d+\.\d%\), RAM \d+(\.\d)? (bytes|KB) \+ heap 224 KB of 264 KB " +
            @"\(stack 8 KB reserved\)$", report.Text);
    }

    private string Build(string name) => Path.Combine(_scratch, name);
}
