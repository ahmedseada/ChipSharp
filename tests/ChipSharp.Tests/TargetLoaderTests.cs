using ChipSharp.Abstractions;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests;

public sealed class TargetLoaderTests : IDisposable
{
    private static readonly string ShippedTargets = Path.Combine(AppContext.BaseDirectory, "targets");
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void Pico_gets_values_from_its_chip_and_its_architecture()
    {
        var pico = new TargetLoader(ShippedTargets).Load("pico");

        Assert.Equal(["pico", "rp2040", "armv6m"], pico.InheritanceChain);
        // from armv6m
        Assert.Equal("llvm", pico.Backend);
        Assert.Equal("thumbv6m-none-eabi", pico.Triple);
        Assert.Equal(4, pico.PointerSize);
        Assert.Equal(Endianness.Little, pico.Endian);
        Assert.False(pico.HasCapability("hwDivide"));
        Assert.Equal("none", pico.Capabilities["atomics"]);
        // from rp2040, merged into armv6m's capabilities rather than replacing them
        Assert.Equal("cortex-m0plus", pico.Cpu);
        Assert.True(pico.HasCapability("romFloat"));
        Assert.Equal("boot2_w25q080.bin", pico.Setting("boot.stage2"));
        Assert.Equal(new MemoryRegion(0x20000000, 264 * 1024), pico.Memory["ram"]);
        // flash: origin from rp2040, size from pico
        Assert.Equal(new MemoryRegion(0x10000000, 2 * 1024 * 1024), pico.Memory["flash"]);
        // from pico itself
        Assert.Equal("25", pico.Pins["LED"]);
        Assert.True(pico.IsComplete);
    }

    [Fact]
    public void A_chip_whose_flash_size_comes_from_the_board_loads_as_incomplete()
    {
        var rp2040 = new TargetLoader(ShippedTargets).Load("rp2040");

        Assert.False(rp2040.IsComplete);
        Assert.Equal(["memory.flash.size"], rp2040.MissingValues);
        Assert.False(rp2040.Memory.ContainsKey("flash"));
        Assert.Equal(new MemoryRegion(0x20000000, 264 * 1024), rp2040.Memory["ram"]);
    }

    [Fact]
    public void Pico_w_overrides_the_LED_and_keeps_everything_else()
    {
        var picoW = new TargetLoader(ShippedTargets).Load("pico-w");

        Assert.Equal(["pico-w", "pico", "rp2040", "armv6m"], picoW.InheritanceChain);
        Assert.Equal("cyw43:0", picoW.Pins["LED"]);
        Assert.Equal(2UL * 1024 * 1024, picoW.Memory["flash"].Size);
    }

    [Fact]
    public void Every_shipped_target_loads()
    {
        var loader = new TargetLoader(ShippedTargets);

        Assert.Contains("pico", loader.Names);
        foreach (var name in loader.Names)
        {
            Assert.Equal(name, loader.Load(name).Name);
        }
    }

    [Fact]
    public void Unknown_target_says_which_targets_exist()
    {
        var error = Assert.Throws<ChipSharpException>(() => new TargetLoader(ShippedTargets).Load("pico3"));

        Assert.Contains("Target 'pico3' not found", error.Message);
        Assert.Contains("pico-w", error.Message);
    }

    [Fact]
    public void Inheritance_loop_is_reported_with_the_whole_chain()
    {
        Write("a.json", """{ "inherits": "b" }""");
        Write("b.json", """{ "inherits": "a" }""");

        var error = Assert.Throws<ChipSharpException>(() => new TargetLoader(_scratch).Load("a"));

        Assert.Contains("inheritance loop: a -> b -> a", error.Message);
    }

    [Fact]
    public void Missing_required_value_names_the_target_and_the_value()
    {
        Write("arch/base.json", """{ "pointerSize": 4, "endian": "little" }""");
        Write("board/myboard.json", """{ "inherits": "base" }""");

        var error = Assert.Throws<ChipSharpException>(() => new TargetLoader(_scratch).Load("myboard"));

        Assert.Equal("Target 'myboard' (myboard -> base): 'backend' is missing.", error.Message);
    }

    [Fact]
    public void Bad_size_explains_how_to_write_one()
    {
        Write("x.json", """
            { "backend": "llvm", "pointerSize": 4, "endian": "little",
              "memory": { "ram": { "origin": "0x20000000", "size": "lots" } } }
            """);

        var error = Assert.Throws<ChipSharpException>(() => new TargetLoader(_scratch).Load("x"));

        Assert.Contains("'memory.ram.size' is \"lots\"", error.Message);
    }

    [Fact]
    public void Two_files_with_the_same_name_are_rejected()
    {
        Write("arch/same.json", "{}");
        Write("chip/same.json", "{}");

        var error = Assert.Throws<ChipSharpException>(() => new TargetLoader(_scratch));

        Assert.Contains("Two target files are named 'same'", error.Message);
    }

    [Fact]
    public void Name_inside_the_file_must_match_the_file_name()
    {
        Write("pico.json", """{ "name": "pico2" }""");

        var error = Assert.Throws<ChipSharpException>(() => new TargetLoader(_scratch).Load("pico"));

        Assert.Contains("They must match", error.Message);
    }

    private void Write(string relativePath, string json)
    {
        var path = Path.Combine(_scratch, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}
