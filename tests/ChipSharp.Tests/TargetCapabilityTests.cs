using ChipSharp.Abstractions;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests;

/// <summary>
/// The capabilities that decide what a program may use on a target: "heap" (false, or the most the heap may take) and
/// "console", as the target files give them and inherit them.
/// </summary>
public sealed class TargetCapabilityTests : IDisposable
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Theory]
    [InlineData("host", 256UL << 20)]
    [InlineData("pico", 224UL << 10)]
    [InlineData("pico-w", 224UL << 10)]
    [InlineData("rp2040", 224UL << 10)]
    public void The_PC_and_the_Pico_have_a_heap_budget_and_a_console(string name, ulong heap)
    {
        var target = Shipped.Load(name);

        Assert.True(target.HasHeap);
        Assert.Equal(heap, target.HeapSize);
        Assert.True(target.HasConsole);
    }

    [Fact]
    public void The_Picos_heap_leaves_room_in_its_RAM_for_the_stack_and_the_static_fields()
    {
        var pico = Shipped.Load("pico");

        var stack = SizeParser.Parse(pico.Setting("link.stack")!);

        Assert.Equal(8UL << 10, stack);
        Assert.Equal(32UL << 10, pico.Memory["ram"].Size - pico.HeapSize - stack);
    }

    [Fact]
    public void An_architecture_has_neither_a_heap_nor_a_console_until_a_chip_gives_them()
    {
        var armv6m = Shipped.Load("armv6m");

        Assert.False(armv6m.HasHeap);
        Assert.Null(armv6m.HeapSize);
        Assert.False(armv6m.HasConsole);
        Assert.Equal("false", armv6m.Capabilities["heap"]);
        Assert.Equal("false", armv6m.Capabilities["console"]);
    }

    [Fact]
    public void A_chip_that_inherits_an_architecture_inherits_its_capabilities()
    {
        var tiny = new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name);

        Assert.Equal(["tiny", "armv6m"], tiny.InheritanceChain);
        Assert.True(tiny.IsComplete);
        Assert.False(tiny.HasHeap);
        Assert.False(tiny.HasConsole);
        Assert.Equal(new MemoryRegion(0x20000000, 2048), tiny.Memory["ram"]);
    }

    [Fact]
    public void A_target_that_says_nothing_has_no_heap_and_no_console()
    {
        var target = Load("""{ "backend": "llvm", "pointerSize": 4, "endian": "little" }""");

        Assert.Null(target.HeapSize);
        Assert.False(target.HasConsole);
    }

    [Theory]
    [InlineData("\"64K\"", 64UL << 10)]
    [InlineData("4096", 4096UL)]
    [InlineData("\"0x1000\"", 4096UL)]
    public void A_heap_budget_is_a_size(string heap, ulong bytes)
    {
        var target = Load($$"""{ "backend": "llvm", "pointerSize": 4, "endian": "little", "capabilities": { "heap": {{heap}} } }""");

        Assert.Equal(bytes, target.HeapSize);
        Assert.True(target.HasHeap);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("\"0\"")]
    public void A_heap_must_have_a_budget(string heap)
    {
        var error = Assert.Throws<ChipSharpException>(() =>
            Load($$"""{ "backend": "llvm", "pointerSize": 4, "endian": "little", "capabilities": { "heap": {{heap}} } }"""));

        Assert.Equal("Target 'x' (x): 'capabilities.heap' must be false (no heap) or the most the heap may take, e.g. " +
            "\"224K\".", error.Message);
    }

    [Fact]
    public void A_heap_budget_is_read_like_any_size()
    {
        var error = Assert.Throws<ChipSharpException>(() =>
            Load("""{ "backend": "llvm", "pointerSize": 4, "endian": "little", "capabilities": { "heap": "lots" } }"""));

        Assert.Equal("Target 'x' (x): 'capabilities.heap' is \"lots\"; write it like 2M, 264K, 0x10000000 or 4096.",
            error.Message);
    }

    [Fact]
    public void A_heap_that_with_the_stack_takes_all_the_RAM_is_an_error()
    {
        Write("arch/chip.json", """
            {
              "backend": "llvm", "pointerSize": 4, "endian": "little",
              "memory": { "sram": { "origin": "0x20000000", "size": "20K" } },
              "link": { "data": "sram", "stack": "4K" },
              "capabilities": { "heap": "8K" }
            }
            """);
        Write("board/greedy.json", """{ "inherits": "chip", "capabilities": { "heap": "16K" } }""");

        var loader = new TargetLoader(_scratch);
        var error = Assert.Throws<ChipSharpException>(() => loader.Load("greedy"));

        Assert.Equal(8UL << 10, loader.Load("chip").HeapSize);
        Assert.Equal("Target 'greedy' (greedy -> chip): 'capabilities.heap' is 16 KB, and with the stack (4 KB, " +
            "link.stack) that leaves nothing of the 20 KB of sram for the static fields: make the heap smaller.",
            error.Message);
    }

    [Fact]
    public void A_board_can_take_the_heap_away()
    {
        var targets = TargetFiles.WithShipped(_scratch,
            ("quiet", """{ "inherits": "pico", "capabilities": { "heap": false, "console": false } }"""));

        var quiet = new TargetLoader(targets).Load("quiet");

        Assert.False(quiet.HasHeap);
        Assert.False(quiet.HasConsole);
        Assert.True(quiet.HasCapability("romFloat"));
    }

    [Fact]
    public void Sizes_are_written_in_bytes_and_KB()
    {
        Assert.Equal("0 bytes", MemorySize.Text(0));
        Assert.Equal("1 byte", MemorySize.Text(1));
        Assert.Equal("1023 bytes", MemorySize.Text(1023));
        Assert.Equal("1 KB", MemorySize.Text(1024));
        Assert.Equal("1.1 KB", MemorySize.Text(1176));
        Assert.Equal("264.0 KB", MemorySize.Text(264 * 1024 + 8));
        Assert.Equal("2048 KB", MemorySize.Text(2 << 20));
        Assert.Equal("RAM", MemorySize.RegionName("ram"));
        Assert.Equal("flash", MemorySize.RegionName("flash"));
    }

    private TargetDescription Load(string json)
    {
        Write("x.json", json);
        return new TargetLoader(_scratch).Load("x");
    }

    private void Write(string relativePath, string json)
    {
        var path = Path.Combine(_scratch, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}
