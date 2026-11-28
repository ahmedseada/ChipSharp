using System.Text;
using ChipSharp.Build;
using ChipSharp.Image.Uf2;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Memory;

/// <summary>
/// A program that never uses the heap gets none: <c>chipsharp build</c> builds it as for the target without one, so on
/// the Pico there is no heap region (the stack may go down to the variables) and none of the collector's code, and the
/// report says no "+ heap"; on the PC no memory is taken from Windows (no VirtualAlloc). A program that makes objects
/// keeps the target's heap and its report.
/// </summary>
public sealed class HeapOnlyWhenUsedTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string ShippedTargets = Path.Combine(AppContext.BaseDirectory, "targets");

    /// <summary>Symbols of the heap and of the collector, which a program without a heap has none of.</summary>
    private static readonly string[] HeapSymbols =
    [
        "__chipsharp_heap_start", "__chipsharp_heap_end", "chipsharp_heap_initialize", "chipsharp_new_object",
        "chipsharp_scan_stack", "chipsharp_new_array", "ChipSharp_Runtime_GcCollect",
    ];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [PicoFact]
    public void On_the_Pico_a_program_without_objects_has_no_heap_region_no_collector_and_no_heap_in_its_report()
    {
        var (result, elf) = BuildForPico("TrafficLights");
        output.WriteLine(result.Memory.Text);

        Assert.Equal(0UL, result.Memory.Heap);
        Assert.Matches(@"^flash \d+(\.\d)? KB of 2048 KB \(\d+\.\d%\), RAM \d+ bytes of 264 KB \(stack 8 KB reserved\)$",
            result.Memory.Text);
        Assert.DoesNotContain(HeapSymbols, elf.Symbols.ContainsKey);
        // the stack has the RAM above the variables, down to them
        Assert.Equal(elf.Symbols["__chipsharp_bss_end"], elf.Symbols["__chipsharp_stack_bottom"]);
        Assert.Equal(0x20042000u, elf.Symbols["__chipsharp_stack_top"]);
    }

    [PicoFact]
    public void On_the_Pico_a_program_that_makes_objects_keeps_the_targets_heap_and_its_report()
    {
        var (result, elf) = BuildForPico("Classes");
        output.WriteLine(result.Memory.Text);

        Assert.Equal(224UL << 10, result.Memory.Heap);
        Assert.Matches(@"^flash \d+(\.\d)? KB of 2048 KB \(\d+\.\d%\), RAM \d+(\.\d)? (bytes|KB) \+ heap 224 KB of 264 KB " +
            @"\(stack 8 KB reserved\)$", result.Memory.Text);
        Assert.All(HeapSymbols[..5], symbol => Assert.True(elf.Symbols.ContainsKey(symbol), symbol));
        Assert.Equal((0x20008000u, 0x20040000u), (elf.Symbols["__chipsharp_heap_start"], elf.Symbols["__chipsharp_heap_end"]));
        Assert.Equal(0x20040000u, elf.Symbols["__chipsharp_stack_bottom"]);
    }

    [HostFact]
    public async Task On_the_PC_a_program_without_objects_takes_no_heap_from_Windows_and_one_with_objects_does()
    {
        var plain = BuildForHost("TrafficLights");
        var objects = BuildForHost("Classes");

        Assert.False(Imports(plain, "VirtualAlloc"));
        Assert.True(Imports(objects, "VirtualAlloc"));
        Assert.Equal(Samples.ExpectedOutput("TrafficLights"), (await HostRun.RunAsync(plain, [], _scratch)).Output);
        Assert.Equal(Samples.ExpectedOutput("Classes"), (await HostRun.RunAsync(objects, [], _scratch)).Output);
    }

    private (BuildResult Result, ElfFile Elf) BuildForPico(string sample)
    {
        var folder = Path.Combine(_scratch, sample);
        var dll = Samples.Compile(sample, Path.Combine(folder, "dll"));
        var result = ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = "pico",
            TargetsFolder = ShippedTargets,
            OutputFolder = folder,
            KeepIntermediates = true,
        });
        return (result, ElfFile.Read(Path.Combine(folder, sample + ".elf")));
    }

    private string BuildForHost(string sample)
    {
        var folder = Path.Combine(_scratch, sample);
        var dll = Samples.Compile(sample, Path.Combine(folder, "dll"));
        return ChipSharpBuilder.Build(dll, new BuildOptions { TargetsFolder = ShippedTargets, OutputFolder = folder })
            .OutputPath;
    }

    /// <summary>True when the program's import table names the function (its name is in the file once imported).</summary>
    private static bool Imports(string program, string function) =>
        Encoding.ASCII.GetString(File.ReadAllBytes(program)).Contains(function + "\0", StringComparison.Ordinal);
}
