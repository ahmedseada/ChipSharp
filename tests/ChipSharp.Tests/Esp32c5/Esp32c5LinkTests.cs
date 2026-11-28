using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Build;
using ChipSharp.Image.Uf2;
using ChipSharp.Runtime.Esp32c5;
using ChipSharp.Runtime.Riscv32;
using ChipSharp.Targets;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// The ESP32-C5 (milestone M4, wave 0) up to a linked .elf: the target files (riscv32, esp32c5, esp32c5-devkitc) and what
/// they inherit, the flags and the linker script the clang toolchain makes of them, and the .elf of a program: every
/// segment the ROM loads in SRAM below its own stack and data, the trap entry 64-byte aligned at SRAM's start, the stack
/// at the end of the program's SRAM, the entry point chipsharp_reset, RV32IMAC code (no float, no Zcmp) as llvm-objdump
/// reads it.
/// </summary>
public sealed class Esp32c5LinkTests : IDisposable
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    /// <summary>A program that only computes and blinks nothing: the start-up, a loop, a division of longs, a double.</summary>
    private const string Computes = """
        static class Program
        {
            static long total;
            static int[] table = { 3, 1, 4, 1, 5, 9, 2, 6 };

            static int Main()
            {
                for (int i = 0; i < table.Length; i++)
                {
                    total += (long)table[i] * 1000000007L / (i + 1);
                }

                double half = total / 2.0;
                return (int)(total % 97) + (half > 1 ? 1 : 0);
            }
        }
        """;

    [Fact]
    public void The_board_gets_its_values_from_the_chip_and_the_architecture()
    {
        var board = Targets.Load("esp32c5-devkitc");

        Assert.Equal(["esp32c5-devkitc", "esp32c5", "riscv32"], board.InheritanceChain);
        // from riscv32
        Assert.Equal(("llvm", "clang", "riscv32-unknown-elf", 4), (board.Backend, board.Toolchain, board.Triple, board.PointerSize));
        Assert.Equal(Endianness.Little, board.Endian);
        Assert.Equal("ilp32", board.Setting("mabi"));
        Assert.True(board.HasCapability("hwDivide"));
        Assert.True(board.HasCapability("stackCheck"));
        Assert.False(board.HasCapability("fpu"));
        Assert.Equal("chipsharp_reset", board.Setting("link.entry"));
        Assert.Equal("64", board.Setting("link.vectorAlign"));
        // from esp32c5
        Assert.Equal("esp32c5", board.Runtime);
        Assert.Equal("rv32imac_zicsr_zifencei", board.Setting("march"));
        Assert.Equal(new MemoryRegion(0x40800000, 0x5c5a0), board.Memory["sram"]);
        Assert.Equal(("sram", "sram", "16K"), (board.Setting("link.code"), board.Setting("link.data"), board.Setting("link.stack")));
        Assert.Equal("48", board.Setting("vectors"));
        Assert.Equal("17", board.Setting("interrupts.timer.irq"));
        Assert.True(board.HasHeap);
        Assert.True(board.HasConsole);
        Assert.Equal(("esp", "esptool"), (board.Image, board.Flasher)); // the ESP image, flashed with esptool
        // from the board
        Assert.Equal("27", board.Pins["RGB_LED"]);
        Assert.True(board.IsComplete);
    }

    [Fact]
    public void Clang_gets_the_chips_march_and_mabi_and_the_linker_script_puts_everything_in_SRAM_below_the_ROMs_data()
    {
        var board = Targets.Load("esp32c5-devkitc").WithoutHeap(); // the heap takes its place in the region (a test below)
        var settings = ChipSettings.Read(board);

        Assert.Equal(["-march=rv32imac_zicsr_zifencei", "-mabi=ilp32"], settings.ProcessorFlags(ClangDriver.Clang));
        Assert.True(settings.IsRiscV);
        Assert.False(ChipSettings.Read(Targets.Load("pico")).IsRiscV);

        var script = LinkerScript.Write(board, settings);

        // code and variables in one region, the stack its last 16 KB: everything ends at 0x4085c5a0, where the ROM's are
        Assert.Contains("sram (rwx) : ORIGIN = 0x40800000, LENGTH = 0x585a0", script);
        Assert.Contains("stack (rw) : ORIGIN = 0x408585a0, LENGTH = 0x4000", script);
        Assert.DoesNotContain("flash", script);
        Assert.Contains(".vectors : ALIGN(0x40)", script);
        Assert.Contains("*(.rodata .rodata.* .srodata .srodata.*)", script);
        Assert.Contains("__global_pointer$ = . + 0x800;", script);
        Assert.Contains("} > sram AT > sram", script);
        Assert.DoesNotContain(".ARM.exidx", script);
        Assert.Contains("__chipsharp_stack_top = ORIGIN(stack) + LENGTH(stack);", script);
    }

    [PicoFact]
    public void A_program_links_into_SRAM_with_the_trap_entry_first_and_its_entry_point_the_reset_handler()
    {
        var program = Esp32c5Build.Build(Computes, "Computes", _scratch);
        var elf = program.File;

        Assert.Equal(243, elf.Machine); // EM_RISCV
        Assert.Equal(elf.Symbols["chipsharp_reset"], elf.Entry);

        // ESP-IDF's application description first, at SRAM's start (its 256 bytes are the image's first segment's first
        // bytes, where the bootloader reads them); then the trap entry, then the code and constants, then .data, loaded
        // where it runs
        var description = Assert.Single(elf.Sections, s => s.Name == ".esp_app_desc");
        Assert.Equal((Esp32c5Build.SramStart, 256u), (description.Address, description.Size));
        Assert.Equal(elf.Symbols["chipsharp_esp_app_desc"], description.Address);
        var vectors = Assert.Single(elf.Sections, s => s.Name == ".vectors");
        Assert.Equal(description.Address + 256, vectors.Address);
        Assert.Equal(elf.Symbols["chipsharp_trap"], vectors.Address);
        var text = Assert.Single(elf.Sections, s => s.Name == ".text");
        Assert.True(text.Address >= vectors.Address + vectors.Size);
        Assert.Equal(elf.Symbols["__chipsharp_data_start"], elf.Symbols["__chipsharp_data_load"]);
        Assert.Equal(elf.Symbols["__chipsharp_data_start"] + 0x800, elf.Symbols["__global_pointer$"]);
        var bss = Assert.Single(elf.Sections, s => s.Name == ".bss");
        Assert.True(bss.NoBits);
        Assert.True(bss.Address >= elf.Symbols["__chipsharp_data_end"]);

        // what the ROM loads: every byte of the file's segments in SRAM, below the ROM's stack and data, at the address
        // it runs at; the stack's top is where they start
        var loaded = elf.Segments.Where(s => s.Data.Length > 0).ToList();
        Assert.NotEmpty(loaded);
        Assert.All(loaded, segment =>
        {
            Assert.Equal(segment.VirtualAddress, segment.PhysicalAddress);
            Assert.InRange(segment.PhysicalAddress, Esp32c5Build.SramStart, Esp32c5Build.LoadEnd);
            Assert.InRange(segment.PhysicalAddress + (uint)segment.Data.Length, Esp32c5Build.SramStart, Esp32c5Build.LoadEnd);
            Assert.Equal(0u, segment.PhysicalAddress % 4);
        });
        Assert.Equal(Esp32c5Build.LoadEnd, elf.Symbols["__chipsharp_stack_top"]);
        Assert.Equal(elf.Symbols["__chipsharp_bss_end"], elf.Symbols["__chipsharp_stack_bottom"]); // no heap: all the rest

        // the chip's first steps and its trap's dispatch are in, the handlers by the CLIC's 48 ids
        Assert.Contains("chipsharp_chip_boot", elf.Symbols.Keys);
        Assert.Contains("chipsharp_trap_dispatch", elf.Symbols.Keys);
        Assert.Contains(LlvmCodeGenerator.EntrySymbol, elf.Symbols.Keys);
        Assert.True(File.Exists(Path.Combine(_scratch, "Computes.ld")));
    }

    [PicoFact]
    public void Chipsharp_build_makes_the_elf_with_the_registered_runtimes_and_the_esp_image()
    {
        var dll = Path.Combine(_scratch, "Computes.dll");
        Directory.CreateDirectory(_scratch);
        File.WriteAllBytes(dll, CSharpCompiler.Compile(Computes, "Computes"));

        var result = ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = "esp32c5-devkitc",
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = Path.Combine(_scratch, "out"),
            KeepIntermediates = true,
        });

        Assert.Equal(Path.Combine(_scratch, "out", "Computes.bin"), result.OutputPath);
        var elf = ElfFile.Read(Path.Combine(_scratch, "out", "Computes.elf"));
        Assert.Equal(243, elf.Machine);
        Assert.Equal(elf.Entry, ChipSharp.Image.Esp.EspImage.Read(File.ReadAllBytes(result.OutputPath)).Entry);
    }

    [PicoFact]
    public void The_code_is_RV32IMAC_with_Zicsr_and_Zifencei_and_the_start_up_sets_gp_sp_and_mtvec()
    {
        var program = Esp32c5Build.Build(Computes, "Computes", _scratch);

        // the build attributes clang writes for -march: I, M, A, C, Zicsr, Zifencei and nothing else (no F, D, Zcmp)
        var attributes = Esp32c5Build.Tool("llvm-readobj", "--arch-specific", program.Elf);
        var arch = attributes.Split('\n').Single(line => line.Contains("Value: rv32", StringComparison.Ordinal));
        const string Rv32imac = "rv32i2p1_m2p0_a2p1_c2p0_zicsr2p0_zifencei2p0";
        Assert.Matches($@"Value: {Rv32imac}(_zmmul1p0)?(_zaamo1p0_zalrsc1p0)?(_zca1p0)?\s*$", arch);

        var code = Esp32c5Build.Tool("llvm-objdump", "-d", "--no-show-raw-insn", program.Elf);
        var reset = code[code.IndexOf("<chipsharp_reset>:", StringComparison.Ordinal)..];
        reset = reset[..reset.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.Contains("auipc\tgp,", reset);
        Assert.Contains("auipc\tsp,", reset);
        Assert.Contains("csrci\tmstatus, 0x8", reset);
        Assert.Contains("csrw\tmtvec,", reset);
        Assert.Contains("<chipsharp_chip_boot>", reset);
        Assert.Contains("<chipsharp_start>", reset);
        Assert.Contains("ebreak", code);   // the failure stop
        Assert.Contains("mret", code);     // the trap entry's return
        Assert.DoesNotContain("cm.push", code); // Zcmp, faulty on the ESP32-C5
        Assert.DoesNotContain("fadd", code);    // no F or D

        // the ESP32-C5's first steps: its watchdogs' write keys, and mtvec in CLIC mode (| 3)
        var boot = code[code.IndexOf("<chipsharp_chip_boot>:", StringComparison.Ordinal)..];
        boot = boot[..boot.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.Contains("0x50d84", boot);   // the key 0x50d83aa1's upper bits (lui 0x50d84, then -0x55f)
        Assert.Contains("csrw\tmtvec,", boot);
        Assert.Contains("ori\t", boot);
    }

    [Fact]
    public void The_trap_dispatch_has_a_weak_handler_for_each_interrupt_id_and_needs_the_targets_count()
    {
        var dispatch = new Esp32c5Runtime().InterruptEntry(Targets.Load("esp32c5-devkitc")).Files
            .Single(f => f.FileName == Riscv32Runtime.VectorsFileName).Content;

        Assert.Contains("CHIPSHARP_HANDLER(chipsharp_isr_irq47);\n", dispatch);
        Assert.DoesNotContain("chipsharp_isr_irq48", dispatch);
        Assert.Contains("static void (*const chipsharp_isr_handlers[48])(void) =", dispatch);
        Assert.Contains("if (id < 48u)", dispatch);
        Assert.Equal("chipsharp_isr_irq17", Frontend.TargetInterrupt.HandlerSymbol(17)); // the timer event's, the C# side's name
        Assert.Equal(Frontend.TargetInterrupt.HandlerSymbol(17), Riscv32Runtime.Handler(17));

        var error = Assert.Throws<ChipSharpException>(() => Riscv32Runtime.VectorCount(Targets.Load("riscv32")));
        Assert.StartsWith("Target 'riscv32': 'vectors' is missing", error.Message);
    }

    [PicoFact]
    public void A_program_with_objects_links_with_the_heap_between_the_variables_and_the_stack()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            static class Program
            {
                static void Main()
                {
                    var list = new List<double>();
                    for (int i = 0; i < 10; i++)
                    {
                        list.Add(i * 1.5);
                    }

                    Console.WriteLine(list.Count);
                    Console.WriteLine(list[9]);
                    GC.Collect();
                }
            }
            """;

        var program = Esp32c5Build.Build(source, "Objects", _scratch, Esp32c5Build.WithHeap("128K"));

        var elf = program.File;
        Assert.Equal(elf.Symbols["__chipsharp_stack_bottom"], elf.Symbols["__chipsharp_heap_end"]);
        Assert.Equal(128u * 1024, elf.Symbols["__chipsharp_heap_end"] - elf.Symbols["__chipsharp_heap_start"]);
        Assert.Contains("chipsharp_scan_stack", elf.Symbols.Keys);
        Assert.Contains("chipsharp_heap_check_thread", elf.Symbols.Keys);
    }
}
