using System.Buffers.Binary;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Image.Uf2;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Targets;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The clang toolchain links chip programs: the linker script it writes from the pico target, and the layout of
/// the blink example's .elf (boot2, vector table, stack, .data).
/// </summary>
public sealed class PicoLinkTests : IDisposable
{
    private const uint FlashStart = 0x10000000;
    private const uint RamEnd = 0x20000000 + 264 * 1024;

    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void The_pico_is_built_with_clang_the_rp2040_runtime_and_uf2()
    {
        var pico = Targets.Load("pico");

        Assert.Equal("clang", pico.Toolchain);
        Assert.Equal("rp2040", pico.Runtime);
        Assert.Equal("uf2", pico.Image);
        Assert.Equal("chipsharp_reset", pico.Setting("link.entry"));
        Assert.Equal("flash", pico.Setting("link.code"));
        Assert.Equal("ram", pico.Setting("link.data"));
        var flags = pico.Settings["clang"]!["flags"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        Assert.Contains("-mfloat-abi=soft", flags);
        Assert.Contains("-ffreestanding", flags);
        Assert.Contains("-fno-builtin", flags);
    }

    [Fact]
    public void The_linker_script_puts_boot2_then_the_vectors_at_the_start_of_flash_and_the_stack_at_the_end_of_RAM()
    {
        var pico = Targets.Load("pico");

        var script = LinkerScript.Write(pico, ChipSettings.Read(pico));

        Assert.Contains("ENTRY(chipsharp_reset)", script);
        Assert.Contains("flash (rx) : ORIGIN = 0x10000000, LENGTH = 0x200000", script);
        Assert.Contains("ram (rwx) : ORIGIN = 0x20000000, LENGTH = 0x42000", script);
        Assert.Contains("ASSERT(SIZEOF(.boot2) == 0x100,", script);
        Assert.Contains(".vectors : ALIGN(0x100)", script);
        Assert.Contains("} > ram AT > flash", script);
        Assert.Contains("__chipsharp_stack_top = ORIGIN(ram) + LENGTH(ram);", script);
        Assert.Contains("ASSERT(__chipsharp_bss_end + 0x2000 <= __chipsharp_stack_top,", script);
        Assert.True(script.IndexOf(".boot2 :", StringComparison.Ordinal) < script.IndexOf(".vectors :", StringComparison.Ordinal));
        Assert.True(script.IndexOf(".vectors :", StringComparison.Ordinal) < script.IndexOf(".text :", StringComparison.Ordinal));
    }

    [Fact]
    public void A_chip_target_that_is_incomplete_is_not_linked()
    {
        var rp2040 = Targets.Load("rp2040");
        var code = new GeneratedCode([new GeneratedFile("Blink.ll", "")]);

        var error = Assert.Throws<ChipSharpException>(() => new ClangToolchain().Build(code, rp2040));

        Assert.Equal("Target 'rp2040': it is incomplete; still missing: memory.flash.size.", error.Message);
    }

    [Fact]
    public void An_architecture_alone_has_no_memory_to_link_into()
    {
        var armv6m = Targets.Load("armv6m");
        var code = new GeneratedCode([new GeneratedFile("Blink.ll", "")]);

        var error = Assert.Throws<ChipSharpException>(() => new ClangToolchain().Build(code, armv6m));

        Assert.Equal("Target 'armv6m': it has no memory regions, so there is nowhere to put the program " +
            "('link.code' is \"flash\"). Build for a chip or board that gives them, e.g. 'pico'.", error.Message);
    }

    [Fact]
    public void A_link_region_the_target_does_not_have_is_named_in_the_error()
    {
        var target = WithSettings(Targets.Load("pico"), settings => settings["link"]!["code"] = "rom");

        var error = Assert.Throws<ChipSharpException>(() => ChipSettings.Read(target));

        Assert.Equal("Target 'pico': 'link.code' is \"rom\", but the target has no memory region of that name " +
            "(it has: flash, ram).", error.Message);
    }

    [PicoFact]
    public void The_blink_elf_has_boot2_with_a_valid_checksum_then_the_vector_table_at_0x10000100()
    {
        var program = PicoBuild.Build(PicoBuild.BlinkSource, "PicoBlink", _scratch);
        var elf = ElfFile.Read(program.Elf);
        var flash = PicoBuild.FlashImage(elf);

        Assert.Equal(40, elf.Machine); // ARM
        var boot2 = Assert.Single(elf.Sections, s => s.Name == ".boot2");
        Assert.Equal((FlashStart, 256u), (boot2.Address, boot2.Size));
        Assert.Equal(Rp2040Runtime.Boot2Bytes(PicoBuild.Pico), flash[..256]);
        Assert.True(Boot2Image.HasValidChecksum(flash.AsSpan(0, 256)));

        var vectors = Assert.Single(elf.Sections, s => s.Name == ".vectors");
        Assert.Equal((FlashStart + 0x100, 48u * 4), (vectors.Address, vectors.Size));
        uint Vector(int index) => BinaryPrimitives.ReadUInt32LittleEndian(flash.AsSpan(0x100 + 4 * index));
        Assert.Equal(RamEnd, Vector(0)); // the initial stack pointer: the top of RAM
        Assert.Equal(RamEnd, elf.Symbols["__chipsharp_stack_top"]);
        Assert.Equal(elf.Symbols["chipsharp_reset"], Vector(1)); // a Thumb function: bit 0 set
        Assert.Equal(1u, Vector(1) & 1);
        Assert.Equal(elf.Entry, Vector(1));
        Assert.Equal(elf.Symbols["chipsharp_isr_irq0"], Vector(16)); // TIMER_IRQ_0: the delay's alarm
        Assert.NotEqual(elf.Symbols["chipsharp_unhandled_exception"], Vector(16));
        Assert.Equal(elf.Symbols["chipsharp_unhandled_exception"], Vector(3)); // HardFault: nobody handles it yet

        // code and constants after the vectors, in flash; .data's starting values in flash, .data and .bss in RAM
        var text = Assert.Single(elf.Sections, s => s.Name == ".text");
        Assert.True(text.Address >= vectors.Address + vectors.Size);
        Assert.InRange(elf.Symbols["__chipsharp_data_load"], text.Address, FlashStart + 0x200000);
        Assert.Equal(0x20000000u, elf.Symbols["__chipsharp_data_start"]);
        Assert.InRange(elf.Symbols["__chipsharp_bss_end"], 0x20000000u, RamEnd - 8 * 1024);
        Assert.True(File.Exists(Path.Combine(_scratch, "PicoBlink.ld")));
        Assert.True(File.Exists(Path.Combine(_scratch, "PicoBlink.map")));
    }

    [PicoFact]
    public void A_head_section_of_the_wrong_size_fails_the_link_with_the_reason()
    {
        var target = WithSettings(PicoBuild.Pico, settings => settings["link"]!["head"]![0]!["size"] = 512);

        var error = Assert.Throws<ChipSharpException>(() => PicoBuild.Build(PicoBuild.BlinkSource, "PicoBlink", _scratch, target));

        Assert.Contains("Target pico: section .boot2 must be exactly 512 bytes (link.head), and it is not.", error.Message);
    }

    [PicoFact]
    public void A_program_whose_variables_leave_too_little_stack_fails_the_link_with_the_reason()
    {
        var target = WithSettings(PicoBuild.Pico, settings => settings["link"]!["stack"] = "264K");

        var error = Assert.Throws<ChipSharpException>(() => PicoBuild.Build(PicoBuild.BlinkSource, "PicoBlink", _scratch, target));

        Assert.Contains("the program's variables leave less than 270336 bytes of ram for the stack (link.stack)", error.Message);
    }

    /// <summary>A copy of <paramref name="target"/> with some settings changed.</summary>
    private static TargetDescription WithSettings(TargetDescription target, Action<JsonObject> change)
    {
        var settings = target.Settings.DeepClone().AsObject();
        change(settings);
        return new TargetDescription
        {
            Name = target.Name,
            InheritanceChain = target.InheritanceChain,
            Backend = target.Backend,
            Toolchain = target.Toolchain,
            Runtime = target.Runtime,
            Triple = target.Triple,
            Cpu = target.Cpu,
            PointerSize = target.PointerSize,
            Endian = target.Endian,
            Image = target.Image,
            Flasher = target.Flasher,
            Memory = target.Memory,
            Pins = target.Pins,
            Capabilities = target.Capabilities,
            Settings = settings,
            MissingValues = target.MissingValues,
        };
    }
}
