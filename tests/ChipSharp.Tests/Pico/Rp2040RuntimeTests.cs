using System.Buffers.Binary;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend;
using ChipSharp.Image.Uf2;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Targets;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Pico;

/// <summary>The rp2040 and armv6m runtimes: their files, boot2 and its checksum, and the functions programs call.</summary>
public sealed class Rp2040RuntimeTests
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    [Fact]
    public void The_parts_the_pico_names_are_found_in_the_registry()
    {
        var pico = Targets.Load("pico");
        var registry = new ComponentRegistry()
            .Register(new ClangToolchain())
            .Register(new Rp2040Runtime())
            .Register(new ArmV6MRuntime())
            .Register(new Uf2ImageFormat());

        Assert.IsType<ClangToolchain>(registry.Get<IToolchain>(pico.Toolchain!));
        Assert.IsType<Rp2040Runtime>(registry.Get<IArchRuntime>(pico.Runtime!));
        Assert.IsType<Uf2ImageFormat>(registry.Get<IImageFormat>(pico.Image!));
        Assert.IsType<ArmV6MRuntime>(registry.Get<IArchRuntime>(Targets.Load("armv6m").Runtime!));
    }

    [Fact]
    public void The_runtime_is_the_armv6m_files_plus_boot2_and_the_chips_own_C()
    {
        var pico = Targets.Load("pico");
        var runtime = new Rp2040Runtime();

        Assert.Equal(
            ["armv6m_startup.c", "armv6m_helpers.c", "armv6m_divmod.S", "rp2040_boot2.S", "rp2040_runtime.c",
                "rp2040_failures.c"],
            runtime.Startup(pico).Files.Select(f => f.FileName));
        Assert.Equal(["armv6m_vectors.c"], runtime.InterruptEntry(pico).Files.Select(f => f.FileName));
        Assert.Empty(runtime.Atomics(pico).Files);
        Assert.All(runtime.Startup(pico).Files.Concat(runtime.InterruptEntry(pico).Files),
            file => Assert.DoesNotContain("#include", file.Content));
    }

    [Fact]
    public void The_runtime_defines_every_function_a_library_call_becomes()
    {
        var pico = Targets.Load("pico");
        var source = new Rp2040Runtime().Startup(pico).Files.Single(f => f.FileName == Rp2040Runtime.SourceFileName).Content;

        Assert.All(KnownExternals.All, function =>
        {
            var symbol = LlvmCodeGenerator.SymbolName(function.DisplayName);
            Assert.Contains($" {symbol}(", source);
        });
        Assert.Contains("void chipsharp_chip_init(void)", source);
        Assert.Contains("void chipsharp_isr_irq0(void)", source);
    }

    [Fact]
    public void The_boot_ROMs_checksum_is_CRC_32_MPEG_2()
    {
        Assert.Equal(0x0376e6e7u, Boot2Image.Crc32("123456789"u8));
        Assert.Equal(0xffffffffu, Boot2Image.Crc32([]));
    }

    [Fact]
    public void Boot2_is_padded_to_252_bytes_and_followed_by_its_checksum()
    {
        byte[] code = [0x00, 0xb5, 0x01, 0x02];

        var image = Boot2Image.Pad(code);

        Assert.Equal(256, image.Length);
        Assert.Equal(code, image[..4]);
        Assert.All(image[4..252], b => Assert.Equal(0, b));
        Assert.Equal(Boot2Image.Crc32(image.AsSpan(0, 252)), BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(252)));
        Assert.True(Boot2Image.HasValidChecksum(image));
        image[7] ^= 1;
        Assert.False(Boot2Image.HasValidChecksum(image));
    }

    [Fact]
    public void Boot2_code_longer_than_252_bytes_is_refused()
    {
        var error = Assert.Throws<ChipSharpException>(() => Boot2Image.Pad(new byte[253], "big.bin"));

        Assert.StartsWith("The second-stage bootloader 'big.bin' is 253 bytes; it must fit in 252", error.Message);
    }

    [Theory]
    [InlineData("boot2_w25q080.bin", 240)]
    [InlineData("boot2_generic_03h.bin", 76)]
    public void The_shipped_boot2s_are_pico_sdks_code_ready_for_flash(string name, int size)
    {
        var target = WithBoot2(Targets.Load("pico"), name);

        var image = Rp2040Runtime.Boot2Bytes(target);

        Assert.True(Boot2Image.HasValidChecksum(image));
        Assert.Equal([0x00, 0xb5], image[..2]); // push {lr}: lr tells boot2 whether the boot ROM called it
        Assert.All(image[size..252], b => Assert.Equal(0, b));
        Assert.NotEqual(0, image[size - 1] | image[size - 2] | image[size - 3] | image[size - 4]);
        // boot2 ends by jumping through the vector table at the start of the program, flash + 0x100
        Assert.Contains("00010010", Convert.ToHexString(image.AsSpan(0, size)));
    }

    [Fact]
    public void The_pico_uses_the_W25Q080_boot2_in_section_boot2()
    {
        var pico = Targets.Load("pico");

        var file = new Rp2040Runtime().Startup(pico).Files.Single(f => f.FileName == Rp2040Runtime.Boot2FileName);

        Assert.Equal("boot2_w25q080.bin", pico.Setting("boot.stage2"));
        Assert.Contains("from boot2_w25q080.bin", file.Content);
        Assert.Contains(".section .boot2, \"ax\", %progbits", file.Content);
        var bytes = file.Content.Split('\n').Where(line => line.TrimStart().StartsWith(".byte", StringComparison.Ordinal))
            .SelectMany(line => line.Trim()[".byte".Length..].Split(',', StringSplitOptions.TrimEntries))
            .Select(hex => Convert.ToByte(hex, 16))
            .ToArray();
        Assert.Equal(Rp2040Runtime.Boot2Bytes(pico), bytes);
    }

    [Fact]
    public void A_boot2_the_runtime_does_not_have_is_an_error_that_lists_the_ones_it_has()
    {
        var target = WithBoot2(Targets.Load("pico"), "boot2_is25lp080.bin");

        var error = Assert.Throws<ChipSharpException>(() => new Rp2040Runtime().Startup(target));

        Assert.Equal("Target 'pico': 'boot.stage2' is \"boot2_is25lp080.bin\", which the rp2040 runtime does not have. " +
            "It has: boot2_w25q080.bin, boot2_generic_03h.bin.", error.Message);
    }

    [Fact]
    public void The_boot2_sources_keep_pico_sdks_license_notice()
    {
        var folder = Path.Combine(Repository.Root, "src", "ChipSharp.Runtime.Rp2040", "Boot2");

        Assert.Contains("Raspberry Pi (Trading) Ltd.", File.ReadAllText(Path.Combine(folder, "LICENSE.TXT")));
        Assert.Contains("SPDX-License-Identifier: BSD-3-Clause", File.ReadAllText(Path.Combine(folder, "boot2_w25q080.S")));
        Assert.Contains("SPDX-License-Identifier: BSD-3-Clause", File.ReadAllText(Path.Combine(folder, "boot2_generic_03h.S")));
    }

    private static TargetDescription WithBoot2(TargetDescription target, string name)
    {
        var settings = target.Settings.DeepClone().AsObject();
        settings["boot"] = new JsonObject { ["stage2"] = name };
        return new TargetDescription
        {
            Name = target.Name,
            InheritanceChain = target.InheritanceChain,
            Backend = target.Backend,
            PointerSize = target.PointerSize,
            Endian = target.Endian,
            Memory = target.Memory,
            Settings = settings,
        };
    }
}
