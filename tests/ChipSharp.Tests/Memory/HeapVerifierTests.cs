using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Memory;

/// <summary>
/// The heap verifier (<c>chipsharp build --verify-heap</c>): the heap checked as a whole before and after every
/// collection. The stress programs of the heap's tests pass it, printing what they print without it, on the PC and the
/// Pico; a heap the program corrupts (hand-written TIR that writes where C# never could: a reference into the middle of
/// an object, a header that is no type) stops the program at the next collection with what is wrong and where.
/// </summary>
public sealed class HeapVerifierTests(ITestOutputHelper output) : IDisposable
{
    private const int FatalErrorExitCode = unchecked((int)0x80131506);

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
    private TargetLoader? _targets;

    private TargetLoader Targets => _targets ??= HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K");

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    /// <summary>The heap's stress programs, each with what it must print first.</summary>
    public static TheoryData<string> StressPrograms => new("survivors", "calls", "arrays", "cycles");

    [HostTheory]
    [MemberData(nameof(StressPrograms))]
    public async Task On_the_PC_the_heap_stress_programs_pass_the_verifier_and_print_what_they_print_without_it(string name)
    {
        var host = Targets.Load("host-16K");
        var plain = await HeapRun.OnHostAsync(Stress(name), Path.Combine(_scratch, "plain"), host);
        var verified = await HeapRun.OnHostAsync(Stress(name), Path.Combine(_scratch, "verified"), host, verifyHeap: true);
        output.WriteLine($"without the verifier: {plain.Time.TotalMilliseconds:0} ms, with it: {verified.Time.TotalMilliseconds:0} ms");

        Assert.True(verified.ExitCode == 0, verified.ToString());
        Assert.Equal(plain.Outcome, verified.Outcome);
    }

    [SimulatorTheory]
    [MemberData(nameof(StressPrograms))]
    public async Task On_the_Pico_the_heap_stress_programs_pass_the_verifier_and_print_what_they_print_without_it(string name)
    {
        var pico = Targets.Load("pico-16K");
        var plain = await HeapRun.OnPicoAsync(Stress(name, pico: true), Path.Combine(_scratch, "plain"), pico, 60_000);
        var verified = await HeapRun.OnPicoAsync(Stress(name, pico: true), Path.Combine(_scratch, "verified"), pico, 120_000,
            verifyHeap: true);
        output.WriteLine($"simulated: {plain.Result.Time.TotalMilliseconds:0} ms without the verifier, " +
            $"{verified.Result.Time.TotalMilliseconds:0} ms with it");

        Assert.True(verified.Run.ExitCode == 0, verified.Run.ToString());
        Assert.Equal(plain.Result.Raw, verified.Result.Raw);
    }

    [HostFact]
    public async Task On_the_PC_a_reference_into_the_middle_of_an_object_stops_the_program_at_the_next_collection()
    {
        var run = await HeapRun.OnHostAsync(Corrupted(header: false), Path.Combine(_scratch, "middle"), Targets.Load("host"),
            verifyHeap: true);

        Assert.Equal(FatalErrorExitCode, run.ExitCode);
        Assert.Equal(["1"], run.Lines);
        Assert.Matches(@"^Fatal error\. The heap is corrupted: a reference into the middle of a block at 0x[0-9a-f]{16} " +
            @"\(found before collection 1\)\.\r\n$", run.Errors);
    }

    [HostFact]
    public async Task On_the_PC_a_header_that_is_no_type_stops_the_program_at_the_next_collection()
    {
        var run = await HeapRun.OnHostAsync(Corrupted(header: true), Path.Combine(_scratch, "header"), Targets.Load("host"),
            verifyHeap: true);

        Assert.Equal(FatalErrorExitCode, run.ExitCode);
        Assert.Matches(@"^Fatal error\. The heap is corrupted: an object whose header is not the address of a type at " +
            @"0x[0-9a-f]{16} \(found before collection 1\)\.\r\n$", run.Errors);
    }

    [SimulatorFact]
    public async Task On_the_Pico_a_corrupted_heap_says_so_over_UART0_and_stops_at_a_breakpoint()
    {
        var pico = PicoBuild.Pico;
        var middle = await HeapRun.OnPicoAsync(Corrupted(header: false, pointerSize: 4), Path.Combine(_scratch, "middle"), pico,
            verifyHeap: true);
        var header = await HeapRun.OnPicoAsync(Corrupted(header: true, pointerSize: 4), Path.Combine(_scratch, "header"), pico,
            verifyHeap: true);

        Assert.All(new[] { middle, header }, stop =>
        {
            Assert.Equal(3, stop.Run.ExitCode);
            Assert.Contains("hit a breakpoint", stop.Run.Stopped);
        });
        Assert.Matches(@"^1\r\nFatal error\. The heap is corrupted: a reference into the middle of a block at 0x2000[0-9a-f]{4} " +
            @"\(found before collection 1\)\.\r\n$", middle.Result.Raw);
        Assert.Matches(@"^1\r\nFatal error\. The heap is corrupted: an object whose header is not the address of a type at " +
            @"0x2000[0-9a-f]{4} \(found before collection 1\)\.\r\n$", header.Result.Raw);
    }

    [SimulatorFact]
    public async Task Chipsharp_build_verify_heap_links_the_verifier_in_and_the_program_runs_as_without_it()
    {
        var dll = Samples.Compile("Classes", Path.Combine(_scratch, "dll"));
        var plainFolder = Path.Combine(_scratch, "plain");
        var verifiedFolder = Path.Combine(_scratch, "verified");

        var plain = await ChipSharpCli.RunAsync("build", dll, "-t", "pico", "-o", plainFolder, "--keep");
        var verified = await ChipSharpCli.RunAsync("build", dll, "-t", "pico", "-o", verifiedFolder, "--keep", "--verify-heap");

        Assert.True(plain.ExitCode == 0 && verified.ExitCode == 0, plain.Error + verified.Error);
        Assert.False(ChipSharp.Image.Uf2.ElfFile.Read(Path.Combine(plainFolder, "Classes.elf")).Symbols.ContainsKey("chipsharp_fatal"));
        Assert.True(ChipSharp.Image.Uf2.ElfFile.Read(Path.Combine(verifiedFolder, "Classes.elf")).Symbols.ContainsKey("chipsharp_fatal"));
        var run = await Rp2040Simulator.RunAsync(Path.Combine(verifiedFolder, "Classes.uf2"), 20_000);
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(Samples.ExpectedOutput("Classes"), run.Uart0);
    }

    private static TirModule Stress(string name, bool pico = false) => name switch
    {
        "survivors" => HeapPrograms.Survivors(pico ? 20_000 : 100_000, 100, 6),
        "calls" => HeapPrograms.AcrossCalls(pico ? 5_000 : 20_000, 100),
        "arrays" => HeapPrograms.Arrays(pico ? 300 : 100, pico ? 5_000 : 30_000),
        _ => HeapPrograms.Cycles(pico ? 5_000 : 20_000),
    };

    /// <summary>
    /// Two nodes, then a store C# could not write: the second's Next set to the address of the first's Value (inside
    /// the first node), or the first's header set to 0x11 (no type's address). Prints 1, then collects.
    /// </summary>
    private static TirModule Corrupted(bool header, int pointerSize = 8)
    {
        var p = new HeapProgram(header ? "Header" : "Middle");
        var ir = p.Ir;
        var first = p.NewNode(ir, ir.Const(TirType.I32, 1));
        var second = p.NewNode(ir, ir.Const(TirType.I32, 2), first);
        if (header)
        {
            ir.Store(ir.FieldAddress(first, p.NodeType, 0), ir.IntToPtr(ir.Const(TirType.NativeInt, 0x11)));
        }
        else
        {
            // a ref to the first node's Value: an array's object is the address of its elements less their offset
            var value = ir.PtrToInt(ir.FieldAddress(first, p.NodeType, HeapProgram.ValueField), TirType.NativeInt);
            var elements = ir.Binary(TirBinaryOp.Add, value,
                ir.Const(TirType.NativeInt, new TirLayout(pointerSize).ArrayDataOffset));
            p.SetField(ir, second, HeapProgram.NextField, ir.ArrayObject(ir.IntToPtr(elements)));
        }

        p.Print(ir, ir.Const(TirType.I32, 1));
        p.Collect(ir);
        p.Print(ir, p.Value(ir, second));
        return p.Finish();
    }
}
