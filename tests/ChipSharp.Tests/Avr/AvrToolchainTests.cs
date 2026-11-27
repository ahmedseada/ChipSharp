using System.Buffers.Binary;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The Arduino Uno's target files and the toolchain "zig": the clang toolchain plug-in running the clang and ld.lld
/// inside Zig (the installed LLVM has no AVR), the flags it gives them, the AVR linker script, and where the sections
/// of a program built with it land.
/// </summary>
public sealed class AvrToolchainTests : IDisposable
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void The_Uno_is_an_ATmega328P_built_with_zig_into_a_hex_file()
    {
        var uno = Shipped.Load("uno");

        Assert.Equal(["uno", "atmega328p", "avr"], uno.InheritanceChain);
        Assert.True(uno.IsComplete);
        Assert.Equal(("llvm", "zig", "avr", "avr", "hex"), (uno.Backend, uno.Toolchain, uno.Runtime, uno.Triple, uno.Image));
        Assert.Equal(2, uno.PointerSize);
        Assert.Equal(Endianness.Little, uno.Endian);
        Assert.Equal(new MemoryRegion(0, 32 << 10), uno.Memory["flash"]);
        Assert.Equal(new MemoryRegion(0x100, 2 << 10), uno.Memory["ram"]);
        Assert.False(uno.HasHeap);
        Assert.False(uno.HasConsole); // until the runtime "avr" has USART0
        Assert.Equal("16000000", uno.Setting("clockHz"));
        Assert.Equal("13", uno.Pins["LED"]);

        var settings = ChipSettings.Read(uno);
        Assert.True(settings.IsAvr);
        Assert.Equal(("atmega328p", "avr-freestanding-none", 0x800000UL), (settings.Mmcu, settings.ZigTarget, settings.DataOffset));
        Assert.Equal(("chipsharp_reset", "flash", "ram", 512UL), (settings.Entry, settings.Code.Name, settings.Data.Name,
            settings.StackSize));
        // zig cc takes the processor from -mcpu (with -mmcu alone it builds for a generic avr2: no mul, no jmp/call)
        Assert.Equal(["-mcpu=atmega328p", "-mmcu=atmega328p"], settings.ProcessorFlags(ClangDriver.Zig));
        Assert.Equal(["-mmcu=atmega328p"], settings.ProcessorFlags(ClangDriver.Clang));
        Assert.Contains("-Os", settings.ClangFlags);
        Assert.Contains("-ffunction-sections", settings.ClangFlags);
        Assert.Contains("-fdata-sections", settings.ClangFlags);
        Assert.Contains("-nostdlib", settings.ClangFlags);
        Assert.Equal(["--gc-sections"], settings.LinkFlags);
    }

    [Fact]
    public void The_other_chips_keep_their_flags_and_the_installed_clang()
    {
        var pico = ChipSettings.Read(Shipped.Load("pico"));

        Assert.False(pico.IsAvr);
        Assert.Equal((null, null, 0UL), (pico.Mmcu, pico.ZigTarget, pico.DataOffset));
        Assert.Equal(["-mcpu=cortex-m0plus"], pico.ProcessorFlags(ClangDriver.Clang));
        Assert.Equal("clang", new ClangToolchain().Name);
        Assert.Equal("zig", new ClangToolchain(new ClangToolchainOptions { Driver = ClangDriver.Zig }).Name);
    }

    [Fact]
    public void The_build_pipeline_has_the_toolchain_zig_and_the_image_hex()
    {
        var uno = Shipped.Load("uno");
        var registry = DefaultComponents.CreateRegistry(
            new BuildContext(new BuildOptions { Target = "uno" }, uno, "Blink", _scratch));

        var zig = Assert.IsType<ClangToolchain>(registry.Get<IToolchain>("zig"));
        Assert.Equal(ClangDriver.Zig, zig.Options.Driver);
        Assert.Equal(ClangDriver.Clang, Assert.IsType<ClangToolchain>(registry.Get<IToolchain>("clang")).Options.Driver);
        Assert.Equal("hex", registry.Get<IImageFormat>("hex").Name);
    }

    [Fact]
    public void The_AVR_linker_script_has_flash_and_RAM_in_address_spaces_of_their_own()
    {
        var uno = Shipped.Load("uno");

        var script = LinkerScript.Write(uno, ChipSettings.Read(uno));

        Assert.Contains("ENTRY(chipsharp_reset)", script);
        // RAM's data addresses 0x100-0x8ff at 0x800000 in the .elf; its last 512 bytes the stack's
        Assert.Contains("  flash (rx) : ORIGIN = 0x0, LENGTH = 0x8000\n", script);
        Assert.Contains("  ram (rwx) : ORIGIN = 0x800100, LENGTH = 0x600\n", script);
        Assert.Contains("  stack (rw) : ORIGIN = 0x800700, LENGTH = 0x200\n", script);
        // the vectors first at address 0, then the constants in flash, then the code, then .data's starting values
        var order = new[] { ".vectors :", ".progmem :", ".text :", ".data :", ".bss (NOLOAD) :" }
            .Select(section => script.IndexOf(section, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Contains("    *(.progmem .progmem.* .progmem?.*)\n  } > flash", script);
        // ordinary constants are read from RAM, so they go there with .data
        Assert.Contains("    *(.data .data.*)\n    *(.rodata .rodata.*)\n    __chipsharp_data_end = .;\n  } > ram AT > flash",
            script);
        Assert.DoesNotContain(".ARM.exidx", script);
        Assert.DoesNotContain("ALIGN(4)", script);
        Assert.Contains("  __chipsharp_data_load = LOADADDR(.data);\n", script);
        Assert.Contains("  __chipsharp_stack_top = ORIGIN(stack) + LENGTH(stack);\n", script);
        Assert.Contains("  __chipsharp_stack_bottom = __chipsharp_bss_end;\n", script);
        Assert.Contains("  __chipsharp_stack_limit = __chipsharp_stack_bottom + 0x80;\n", script);
    }

    [Fact]
    public void Zig_not_where_CHIPSHARP_ZIG_says_is_an_error_that_says_how_to_get_it()
    {
        var missing = Path.Combine(_scratch, "no-zig", "zig.exe");
        var toolchain = new ClangToolchain(new ClangToolchainOptions { Driver = ClangDriver.Zig },
            name => name == ClangToolchain.ZigVariable ? missing : null);

        var error = Assert.Throws<ChipSharpException>(toolchain.FindZig);

        Assert.StartsWith($"Zig was not found at '{missing}' (set by the environment variable CHIPSHARP_ZIG). ",
            error.Message);
        Assert.Contains("Install Zig 0.17 or later (https://ziglang.org/download/", error.Message);
        Assert.Contains("set CHIPSHARP_ZIG to its zig", error.Message);
        var option = new ClangToolchain(new ClangToolchainOptions { Driver = ClangDriver.Zig, ZigPath = missing },
            _ => null);
        Assert.Contains("(set by the ZigPath option)", Assert.Throws<ChipSharpException>(option.FindZig).Message);
    }

    [Fact]
    public void CHIPSHARP_ZIG_may_name_zig_or_its_folder()
    {
        var folder = Path.Combine(_scratch, "zig-0.17");
        Directory.CreateDirectory(folder);
        var zig = Path.Combine(folder, OperatingSystem.IsWindows() ? "zig.exe" : "zig");
        File.WriteAllBytes(zig, []);

        Assert.Equal(zig, new ClangToolchain(new ClangToolchainOptions(), name =>
            name == ClangToolchain.ZigVariable ? zig : null).FindZig());
        Assert.Equal(zig, new ClangToolchain(new ClangToolchainOptions(), name =>
            name == ClangToolchain.ZigVariable ? $"\"{folder}\"" : null).FindZig());
    }

    [Fact]
    public void The_toolchain_zig_builds_no_PC_program_and_needs_zigs_name_of_the_target()
    {
        var zig = new ClangToolchain(new ClangToolchainOptions { Driver = ClangDriver.Zig, OutputFolder = _scratch });
        var code = new GeneratedCode([new GeneratedFile("main.c", "int main(void) { return 0; }\n")]);

        var pc = Assert.Throws<ChipSharpException>(() => zig.Build(code, Shipped.Load("host")));
        Assert.Equal("Target 'host': the toolchain \"zig\" builds chip programs (a target whose 'link.code' names a " +
            "memory region), not PC programs; build those with the toolchain \"clang\".", pc.Message);

        var targets = TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("no-zig-target", """{ "inherits": "uno", "zig": null }"""));
        var error = Assert.Throws<ChipSharpException>(() => zig.Build(code, new TargetLoader(targets).Load("no-zig-target")));
        Assert.StartsWith("Target 'no-zig-target': 'zig.target' is missing;", error.Message);
    }

    [AvrFact]
    public void The_test_program_is_built_for_the_ATmega328P_with_its_sections_where_the_script_puts_them()
    {
        var program = AvrTestProgram.Build(_scratch);
        var elf = ElfFile.Read(program.Elf);
        ElfSection Section(string name) => Assert.Single(elf.Sections, section => section.Name == name);

        Assert.Equal(83, elf.Machine); // EM_AVR
        // e_flags: avr5, the ATmega328P's instruction set (mul, jmp and call, movw), not avr2's
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(program.Elf).AsSpan(36));
        Assert.Equal(5U, flags & 0x7f);
        Assert.Equal(elf.Symbols["chipsharp_reset"], elf.Entry);
        Assert.Equal(0U, Section(".vectors").Address);
        Assert.Equal(26U * 4, Section(".vectors").Size); // a jmp for the reset and each of the 25 interrupts
        // __flash text in flash, after the vectors; the ordinary constant in RAM, at its first data address
        Assert.Equal(Section(".progmem").Address, elf.Symbols["flashText"]);
        Assert.Equal(Section(".vectors").Size, Section(".progmem").Address);
        Assert.True(Section(".progmem").Address < Section(".text").Address);
        Assert.Equal(0x800100U, Section(".data").Address);
        Assert.Equal(0x800100U, elf.Symbols["greeting"]);
        Assert.Equal(Section(".text").Address + Section(".text").Size, Section(".data").LoadAddress);
        Assert.Equal(Section(".data").LoadAddress, elf.Symbols["__chipsharp_data_load"]);
        Assert.Equal(Section(".data").Address + Section(".data").Size, elf.Symbols["__chipsharp_bss_start"]);
        Assert.Equal(elf.Symbols["toggles"], elf.Symbols["__chipsharp_bss_start"]);
        Assert.Equal(0x800900U, elf.Symbols["__chipsharp_stack_top"]);
        // the linker script and the map of where everything went, next to the program
        Assert.True(File.Exists(Path.Combine(program.Folder, "Blink.ld")));
        Assert.True(File.Exists(Path.Combine(program.Folder, "Blink.map")));
    }

    [AvrFact]
    public void The_build_cache_keeps_zigs_objects_under_keys_made_with_zigs_identity()
    {
        var cache = Path.Combine(_scratch, "cache");
        var options = new ClangToolchainOptions { CacheFolder = cache };

        var first = AvrTestProgram.Build(Path.Combine(_scratch, "first"), options: options);
        var objects = Directory.GetFiles(cache, "*.o");
        var second = AvrTestProgram.Build(Path.Combine(_scratch, "second"), options: options);

        Assert.Equal(3, objects.Length); // startup.S, main.c and extra.c
        Assert.Equal(objects.Order(), Directory.GetFiles(cache, "*.o").Order()); // the second build compiled nothing
        Assert.Equal(File.ReadAllBytes(first.Hex), File.ReadAllBytes(second.Hex));
        var zig = AvrTools.Zig.FindZig();
        List<string> parts = ["cc", "--target=avr-freestanding-none", "main.c"];
        Assert.NotEqual(ObjectCache.Key(zig, ".o", parts), ObjectCache.Key(Path.Combine(_scratch, "clang.exe"), ".o", parts));
    }
}
