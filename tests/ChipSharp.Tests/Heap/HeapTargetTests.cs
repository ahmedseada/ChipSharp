using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Runtime.Windows;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Heap;

/// <summary>
/// Where the heap is on each target, and what the runtimes and the toolchain bring for it: the linker script's heap
/// region on a chip, the heap's size on the PC, and nothing of either on a target without a heap.
/// </summary>
public sealed class HeapTargetTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private TargetLoader Targets() => new(TargetFiles.WithShipped(_scratch,
        ("tiny", """{ "inherits": "pico", "memory": { "ram": { "size": "64K" } }, "capabilities": { "heap": false } }"""),
        ("small", """{ "inherits": "pico", "capabilities": { "heap": "16K" } }"""),
        ("greedy", """{ "inherits": "pico", "capabilities": { "heap": "260K" } }"""),
        ("pc-without-heap", """{ "inherits": "host", "capabilities": { "heap": false } }""")));

    [Fact]
    public void A_chip_with_a_heap_keeps_its_bytes_between_the_variables_and_the_stack()
    {
        var small = Targets().Load("small");

        var script = LinkerScript.Write(small, ChipSettings.Read(small));

        Assert.Contains("ram (rwx) : ORIGIN = 0x20000000, LENGTH = 0x3c000", script);
        Assert.Contains("heap (rw) : ORIGIN = 0x2003c000, LENGTH = 0x4000", script);
        Assert.Contains("stack (rw) : ORIGIN = 0x20040000, LENGTH = 0x2000", script);
        Assert.Contains("__chipsharp_heap_start = ORIGIN(heap);", script);
    }

    [Fact]
    public void A_chip_without_a_heap_has_no_heap_region_and_no_heap_in_its_runtime()
    {
        var tiny = Targets().Load("tiny");

        var script = LinkerScript.Write(tiny, ChipSettings.Read(tiny));
        var files = new Rp2040Runtime().Startup(tiny).Files.Select(f => f.FileName).ToList();

        Assert.Contains("ram (rwx) : ORIGIN = 0x20000000, LENGTH = 0xe000", script);
        Assert.DoesNotContain("heap", script);
        Assert.DoesNotContain(ArmV6MRuntime.HeapFileName, files);
        Assert.DoesNotContain(ArmV6MRuntime.HeapChipFileName, files);
    }

    [Fact]
    public void A_heap_and_a_stack_bigger_than_the_RAM_are_an_error_of_the_target()
    {
        var targets = Targets();

        // the target file is refused when it is read, so the linker script never has to fit them in
        var error = Assert.Throws<ChipSharpException>(() => targets.Load("greedy"));

        Assert.Contains("'capabilities.heap' is 260 KB, and with the stack (8 KB, link.stack) that leaves nothing of the " +
            "264 KB of ram for the static fields", error.Message);
    }

    [Fact]
    public void A_RAM_overflow_counts_the_heap_and_the_stack_the_target_keeps()
    {
        var pico = Targets().Load("pico");

        var message = MemoryOverflow.Message(
            "ld.lld: error: section '.bss' will not fit in region 'ram': overflowed by 1024 bytes", "Big", pico,
            ChipSettings.Read(pico));

        Assert.Equal("Big does not fit in target 'pico': its variables (33 KB), the heap (224 KB, capabilities.heap) and " +
            "the stack (8 KB, link.stack) need 265 KB of RAM, 1 KB more than the target's 264 KB. Make the program " +
            "smaller, or build it for a chip with more memory.", message);
    }

    [Fact]
    public void The_PC_runtime_takes_the_targets_heap_size_from_windows_and_a_PC_without_a_heap_has_none()
    {
        var targets = Targets();

        var host = new WindowsX64Runtime().Startup(targets.Load("host")).Files;
        var without = new WindowsX64Runtime().Startup(targets.Load("pc-without-heap")).Files;

        Assert.Equal("// The heap of target 'host', from its \"heap\" capability: the bytes chipsharp_heap_windows.c takes " +
            "from Windows.\nconst unsigned long long chipsharp_heap_size = 268435456ull;\n",
            host.Single(f => f.FileName == WindowsX64Runtime.HeapSizeFileName).Content);
        Assert.Contains("VirtualAlloc(0, (uintptr_t)chipsharp_heap_size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE)",
            host.Single(f => f.FileName == WindowsX64Runtime.HeapWindowsFileName).Content);
        Assert.Equal(WindowsX64Runtime.Read(WindowsX64Runtime.HeapFileName),
            new Rp2040Runtime().Startup(targets.Load("pico")).Files.Single(f => f.FileName == ArmV6MRuntime.HeapFileName).Content);
        Assert.DoesNotContain(without, f => f.FileName.Contains("heap", StringComparison.Ordinal));
    }
}
