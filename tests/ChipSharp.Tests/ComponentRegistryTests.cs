using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Tests;

public sealed class ComponentRegistryTests
{
    [Fact]
    public void Finds_a_part_by_name_ignoring_case()
    {
        var llvm = new FakeCodeGenerator("llvm");
        var registry = new ComponentRegistry().Register(llvm);

        Assert.Same(llvm, registry.Get<ICodeGenerator>("LLVM"));
    }

    [Fact]
    public void Unregistered_name_fails_loudly_and_lists_what_is_registered()
    {
        var registry = new ComponentRegistry()
            .Register(new FakeCodeGenerator("llvm"))
            .Register(new FakeCodeGenerator("c"));

        var error = Assert.Throws<ChipSharpException>(() => registry.Get<ICodeGenerator>("xyz"));

        Assert.Equal("Code generator 'xyz' is not registered. Registered: c, llvm.", error.Message);
    }

    [Fact]
    public void Asking_for_a_kind_with_nothing_registered_says_so()
    {
        var error = Assert.Throws<ChipSharpException>(() => new ComponentRegistry().Get<IFlasher>("esptool"));

        Assert.Equal("Flasher 'esptool' is not registered. No flashers are registered.", error.Message);
    }

    [Fact]
    public void The_same_name_twice_is_rejected()
    {
        var registry = new ComponentRegistry().Register(new FakeCodeGenerator("llvm"));

        var error = Assert.Throws<ChipSharpException>(() => registry.Register(new FakeCodeGenerator("llvm")));

        Assert.Equal("Code generator 'llvm' is already registered.", error.Message);
    }

    [Fact]
    public void A_part_with_two_contracts_is_found_as_either()
    {
        var both = new FakeToolchainAndFlasher("wch");
        var registry = new ComponentRegistry().Register(both);

        Assert.Same(both, registry.Get<IToolchain>("wch"));
        Assert.Same(both, registry.Get<IFlasher>("wch"));
    }

    private sealed class FakeCodeGenerator(string name) : ICodeGenerator
    {
        public string Name { get; } = name;
        public GeneratedCode Generate(TirModule module, TargetDescription target) => new([]);
    }

    private sealed class FakeToolchainAndFlasher(string name) : IToolchain, IFlasher
    {
        public string Name { get; } = name;
        public LinkedBinary Build(GeneratedCode code, TargetDescription target) => new("program.elf");

        public Task FlashAsync(string imagePath, TargetDescription target, FlashOptions options,
            IProgress<FlashProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
