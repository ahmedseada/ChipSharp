using System.Buffers.Binary;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend;
using ChipSharp.Image.Uf2;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Runtime.Windows;
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
    public void The_runtime_is_the_armv6m_files_plus_boot2_the_chips_own_C_and_the_shared_float_text()
    {
        var pico = Targets.Load("pico");
        var runtime = new Rp2040Runtime();

        var files = runtime.Startup(pico).Files;

        var softFloat = CompilerRt.Sources().Select(f => f.FileName).ToList();
        Assert.Equal(
            ["armv6m_startup.c", "armv6m_helpers.c", "armv6m_divmod.S", "armv6m_float_compare.c",
                ArmV6MRuntime.FloatMathFileName, .. softFloat, ArmV6MRuntime.FailuresFileName, "chipsharp_string.c",
                "chipsharp_exceptions.c",
                "chipsharp_heap.c", "armv6m_heap.c", "chipsharp_string_heap.c", "chipsharp_float_text.c", "rp2040_boot2.S",
                "rp2040_runtime.c", Rp2040Runtime.ConsoleFileName, "rp2040_failures.c", "rp2040_i2c.c", "rp2040_spi.c",
                Rp2040Runtime.UartFileName, Rp2040Runtime.AdcFileName, Rp2040Runtime.PwmFileName],
            files.Select(f => f.FileName));

        // the very files the PC's runtime has
        Assert.Equal(WindowsX64Runtime.Console,
            files.Single(f => f.FileName == Rp2040Runtime.ConsoleFileName).Content);
        Assert.Equal(WindowsX64Runtime.FloatText,
            files.Single(f => f.FileName == Rp2040Runtime.FloatTextFileName).Content);
        Assert.Equal(WindowsX64Runtime.FloatMath,
            files.Single(f => f.FileName == ArmV6MRuntime.FloatMathFileName).Content);
        Assert.Equal(["armv6m_vectors.c", Rp2040Runtime.InterruptsFileName],
            runtime.InterruptEntry(pico).Files.Select(f => f.FileName));
        Assert.Empty(runtime.Atomics(pico).Files);

        // ChipSharp's own C includes nothing (compiler-rt's includes its own headers and, on these targets, only the
        // C standard's freestanding ones, which come with clang)
        Assert.All(runtime.Startup(pico).Files.Concat(runtime.InterruptEntry(pico).Files)
                .Where(file => !softFloat.Contains(file.FileName)),
            file => Assert.DoesNotContain("#include", file.Content));
    }

    /// <summary>
    /// compiler-rt's files ship unchanged with their license; a build compiles every .c file once, in one of six
    /// units, and gets every header.
    /// </summary>
    [Fact]
    public void The_armv6m_runtime_ships_compiler_rts_soft_float_with_its_license()
    {
        var names = CompilerRt.FileNames;
        string[] sources = [.. names.Where(name => name.EndsWith(".c", StringComparison.Ordinal))];

        Assert.Contains("adddf3.c", names);
        Assert.Contains("fp_lib.h", names);
        Assert.Contains("fp_add_impl.inc", names);
        Assert.Equal(31, sources.Length);
        Assert.Equal(sources, CompilerRt.Units.SelectMany(unit => unit.Files).Order(StringComparer.Ordinal));
        Assert.All(names, name => Assert.Contains("SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception",
            CompilerRt.File(name).Content));
        var built = CompilerRt.Sources();
        Assert.Equal([.. names.Except(sources), .. CompilerRt.Units.Select(unit => unit.Name)], built.Select(f => f.FileName));
        Assert.All(CompilerRt.Units, unit => Assert.All(unit.Files, file => Assert.Contains(
            $"#line 1 \"{file}\"\n{CompilerRt.File(file).Content}", built.Single(f => f.FileName == unit.Name).Content)));
        var license = Path.Combine(Repository.Root, "src", "ChipSharp.Runtime.ArmV6M", "CompilerRt", "LICENSE.TXT");
        Assert.Contains("Apache License", File.ReadAllText(license));
        Assert.Contains("LLVM Exceptions to the Apache 2.0 License", File.ReadAllText(license));
    }

    [Fact]
    public void The_runtime_defines_every_function_a_library_call_becomes()
    {
        var pico = Targets.Load("pico");
        var runtime = new Rp2040Runtime();
        var files = runtime.Startup(pico).Files;
        var source = files.Single(f => f.FileName == Rp2040Runtime.SourceFileName).Content;
        var console = files.Single(f => f.FileName == Rp2040Runtime.ConsoleFileName).Content;
        var floatMath = files.Single(f => f.FileName == ArmV6MRuntime.FloatMathFileName).Content;
        var heap = files.Single(f => f.FileName == ArmV6MRuntime.HeapFileName).Content;
        var interrupts = runtime.InterruptEntry(pico).Files
            .Single(f => f.FileName == Rp2040Runtime.InterruptsFileName).Content;

        // the Math methods are in the float math every runtime shares, Console's in the console output every runtime
        // shares, which writes through the chip's own chipsharp_console_write, and GC's in the heap every runtime with
        // one shares; the rest is the chip's own
        Assert.All(KnownExternals.All, function =>
        {
            var symbol = LlvmCodeGenerator.SymbolName(function.DisplayName);
            var file = function.LibraryMethod.StartsWith("System.Math", StringComparison.Ordinal) ? floatMath
                : function.LibraryMethod.StartsWith("System.Console", StringComparison.Ordinal) ? console
                : function.LibraryMethod.StartsWith("System.GC", StringComparison.Ordinal) ? heap
                : source + interrupts;
            Assert.Contains($" {symbol}(", file);
        });
        Assert.Contains("void chipsharp_console_write(const char *bytes, unsigned int count)", source);
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
