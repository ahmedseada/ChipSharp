using System.Buffers.Binary;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Build;
using ChipSharp.Image.Uf2;
using ChipSharp.Runtime.Ch32v003;
using ChipSharp.Runtime.Riscv32;
using ChipSharp.Targets;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Ch32v003;

/// <summary>
/// The CH32V003 (milestone M5, wave 0) up to a linked .elf: the target files (riscv32e, ch32v003, ch32v003-evt) and what
/// they inherit from riscv32, the flags and the linker script the clang toolchain makes of them, and the .elf of a
/// program: the vector table at 0x00000000 (where the core starts: a jump to the reset handler, then the trap entry's
/// address in every other entry), the code and .data's starting values in the 16 KB of flash, the variables and the
/// stack in the 2 KB of SRAM, RV32EC code (no M, no register above x15) with the ilp32e ABI as llvm-objdump and
/// llvm-readobj read it, and the chip's first steps (INTSYSCR, mtvec, the clock at 48 MHz).
/// </summary>
public sealed partial class Ch32v003LinkTests : IDisposable
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    /// <summary>
    /// A program that only computes: the start-up, a loop over a table, products, quotients and remainders of ints and
    /// longs (the helpers RV32EC needs), a double; from a volatile seed, so that LLVM cannot work it all out first.
    /// </summary>
    private const string Computes = """
        static class Program
        {
            static volatile int seed = 7;
            static long total;
            static int[] table = { 3, 1, 4, 1, 5, 9, 2, 6 };

            static int Main()
            {
                int product = seed;
                for (int i = 0; i < table.Length; i++)
                {
                    total += (long)table[i] * (1000000007L + seed) / (i + seed);
                    product = product * (table[i] + seed) + (int)(total % (i + seed));
                }

                double half = total / (2.0 + seed);
                return (int)(total % 97) + product / seed + product % (seed + 1) + (half > 1 ? 1 : 0);
            }
        }
        """;

    [Fact]
    public void The_board_gets_its_values_from_the_chip_and_the_architectures()
    {
        var board = Targets.Load("ch32v003-evt");

        Assert.Equal(["ch32v003-evt", "ch32v003", "riscv32e", "riscv32"], board.InheritanceChain);
        // from riscv32
        Assert.Equal(("llvm", "clang", "riscv32-unknown-elf", 4), (board.Backend, board.Toolchain, board.Triple, board.PointerSize));
        Assert.Equal(Endianness.Little, board.Endian);
        Assert.True(board.HasCapability("stackCheck"));
        Assert.False(board.HasCapability("fpu"));
        Assert.False(board.HasCapability("unalignedAccess"));
        Assert.Equal("chipsharp_reset", board.Setting("link.entry"));
        // from riscv32e
        Assert.Equal("ilp32e", board.Setting("mabi"));
        Assert.False(board.HasCapability("hwMultiply"));
        Assert.False(board.HasCapability("hwDivide"));
        Assert.Equal("none", board.Capabilities["atomics"]);
        // from ch32v003
        Assert.Equal("ch32v003", board.Runtime);
        Assert.Equal("rv32ec_zicsr", board.Setting("march"));
        Assert.Equal(new MemoryRegion(0x00000000, 16 * 1024), board.Memory["flash"]);
        Assert.Equal(new MemoryRegion(0x20000000, 2 * 1024), board.Memory["sram"]);
        Assert.Equal(("flash", "sram", "512", "4"),
            (board.Setting("link.code"), board.Setting("link.data"), board.Setting("link.stack"), board.Setting("link.vectorAlign")));
        Assert.Equal("39", board.Setting("vectors"));
        Assert.Equal(("12", "20", "32"),
            (board.Setting("interrupts.timer.irq"), board.Setting("interrupts.pin.irq"), board.Setting("interrupts.uart.irq")));
        Assert.False(board.HasHeap);
        Assert.False(board.HasConsole);
        Assert.False(board.HasCapability("clock"));
        Assert.Null(board.Image); // the image and the WCH-LinkE flasher come with M5's wave 0 (B)
        // from the board
        Assert.Equal("48000000", board.Setting("clockHz"));
        Assert.Empty(board.Pins); // the board's LEDs are on a header, wired to no pin
        Assert.True(board.IsComplete);

        // the architecture alone: riscv32's, with E's ABI and runtime
        var riscv32e = Targets.Load("riscv32e");
        Assert.Equal(("riscv32e", "riscv32-unknown-elf", "rv32ec_zicsr"), (riscv32e.Runtime, riscv32e.Triple, riscv32e.Setting("march")));
        Assert.Equal("8K", riscv32e.Setting("link.stack"));
    }

    [Fact]
    public void Clang_gets_rv32ec_and_ilp32e_and_the_linker_script_puts_the_vector_table_first_in_flash()
    {
        var board = Targets.Load("ch32v003-evt");
        var settings = ChipSettings.Read(board);

        Assert.Equal(["-march=rv32ec_zicsr", "-mabi=ilp32e"], settings.ProcessorFlags(ClangDriver.Clang));
        Assert.True(settings.IsRiscV);

        var script = LinkerScript.Write(board, settings);

        // the flash at its alias, where the core starts; the SRAM's last 512 bytes the stack, up to 0x20000800
        Assert.Contains("flash (rx) : ORIGIN = 0x0, LENGTH = 0x4000", script);
        Assert.Contains("sram (rwx) : ORIGIN = 0x20000000, LENGTH = 0x600", script);
        Assert.Contains("stack (rw) : ORIGIN = 0x20000600, LENGTH = 0x200", script);
        // the vector table first in flash, exactly its 39 entries; then the trap entry, 4-byte aligned
        var head = script.IndexOf("  .ch32v003_vectors :", StringComparison.Ordinal);
        Assert.InRange(head, 0, script.IndexOf(".vectors : ALIGN(0x4)", StringComparison.Ordinal));
        Assert.Contains("ASSERT(SIZEOF(.ch32v003_vectors) == 0x9c,", script);
        Assert.Contains("} > sram AT > flash", script);
        Assert.Contains("__global_pointer$ = . + 0x800;", script);
        Assert.Contains("__chipsharp_stack_top = ORIGIN(stack) + LENGTH(stack);", script);
        Assert.Contains("__chipsharp_stack_limit = __chipsharp_stack_bottom + 0x80;", script); // a quarter of 512
    }

    [PicoFact]
    public void A_program_links_with_the_vector_table_at_0_the_code_in_flash_and_the_variables_in_SRAM()
    {
        var program = Ch32v003Build.Build(Computes, "Computes", _scratch);
        var elf = program.File;

        Assert.Equal(243, elf.Machine); // EM_RISCV
        Assert.Equal(elf.Symbols["chipsharp_reset"], elf.Entry);

        // the vector table at 0x00000000, where the core starts: entry 0 jumps to the reset handler (jal x0), entries
        // 1-38 hold the trap entry's address, which comes right after the table
        var table = Assert.Single(elf.Sections, s => s.Name == Ch32v003Runtime.VectorTableSection);
        Assert.Equal((0u, 156u), (table.Address, table.Size));
        Assert.Equal(0u, elf.Symbols["ch32v003_vector_table"]);
        var words = Words(elf, 0, 39);
        Assert.Equal(elf.Symbols["chipsharp_reset"], JalTarget(words[0], 0));
        Assert.Equal(0x6fu, words[0] & 0xfff); // jal x0: opcode 1101111, rd 0
        var trap = elf.Symbols["chipsharp_trap"];
        Assert.All(words.Skip(1), word => Assert.Equal(trap, word));
        Assert.Equal(156u, trap);
        var vectors = Assert.Single(elf.Sections, s => s.Name == ".vectors");
        Assert.Equal(trap, vectors.Address);

        // the code and constants in flash, then .data's starting values; .data and .bss in SRAM, the stack above them
        var text = Assert.Single(elf.Sections, s => s.Name == ".text");
        Assert.True(text.Address >= vectors.Address + vectors.Size);
        Assert.InRange(elf.Symbols["__chipsharp_data_load"], text.Address + text.Size, Ch32v003Build.FlashEnd);
        Assert.Equal(Ch32v003Build.SramStart, elf.Symbols["__chipsharp_data_start"]);
        // gp 0x800 bytes into the small variables (.sdata), which come after the others
        Assert.InRange(elf.Symbols["__global_pointer$"], elf.Symbols["__chipsharp_data_start"] + 0x800,
            elf.Symbols["__chipsharp_data_end"] + 0x800);
        var bss = Assert.Single(elf.Sections, s => s.Name == ".bss");
        Assert.True(bss.NoBits);
        Assert.InRange(bss.Address, elf.Symbols["__chipsharp_data_end"], Ch32v003Build.SramEnd - 512);
        Assert.Equal(Ch32v003Build.SramEnd, elf.Symbols["__chipsharp_stack_top"]);
        Assert.Equal(elf.Symbols["__chipsharp_bss_end"], elf.Symbols["__chipsharp_stack_bottom"]); // no heap: all the rest

        // what a programmer writes: every byte of the file's segments in the 16 KB of flash, at its physical address (the
        // alias: flash 0x08000000 + the same offset)
        var loaded = elf.Segments.Where(s => s.Data.Length > 0).ToList();
        Assert.NotEmpty(loaded);
        Assert.All(loaded, segment =>
        {
            Assert.InRange(segment.PhysicalAddress, Ch32v003Build.FlashStart, Ch32v003Build.FlashEnd);
            Assert.InRange(segment.PhysicalAddress + (uint)segment.Data.Length, Ch32v003Build.FlashStart, Ch32v003Build.FlashEnd);
        });
        Assert.Contains(loaded, segment => segment.PhysicalAddress == 0);

        // the chip's first steps and its trap's dispatch are in, the handlers by the PFIC's 39 numbers
        Assert.Contains("chipsharp_chip_boot", elf.Symbols.Keys);
        Assert.Contains("chipsharp_trap_dispatch", elf.Symbols.Keys);
        Assert.Contains(LlvmCodeGenerator.EntrySymbol, elf.Symbols.Keys);
        Assert.True(File.Exists(Path.Combine(_scratch, "Computes.ld")));

        // what RV32EC has no instruction for: the runtime's helpers, and compiler-rt's double
        Assert.All((string[])["__mulsi3", "__divsi3", "__modsi3", "__muldi3", "__divdi3", "__moddi3", "__divdf3", "__floatdidf"],
            helper => Assert.Contains(helper, elf.Symbols.Keys));
    }

    [PicoFact]
    public void Chipsharp_build_makes_the_elf_with_the_registered_runtimes_and_no_image_yet()
    {
        var dll = Path.Combine(_scratch, "Computes.dll");
        Directory.CreateDirectory(_scratch);
        File.WriteAllBytes(dll, CSharpCompiler.Compile(Computes, "Computes"));

        var result = ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = "ch32v003-evt",
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = Path.Combine(_scratch, "out"),
        });

        Assert.Equal(Path.Combine(_scratch, "out", "Computes.elf"), result.OutputPath);
        Assert.Equal(243, ElfFile.Read(result.OutputPath).Machine);
    }

    [PicoFact]
    public void The_code_is_RV32EC_with_ilp32e_and_the_start_up_sets_gp_sp_mtvec_INTSYSCR_and_the_48_MHz_clock()
    {
        var program = Ch32v003Build.Build(Computes, "Computes", _scratch);

        // the build attributes clang writes for -march and -mabi: E, C and Zicsr (Zca is C's part), the stack 4-byte aligned
        var attributes = Ch32v003Build.Tool("llvm-readobj", "--arch-specific", program.Elf);
        var arch = attributes.Split('\n').Single(line => line.Contains("Value: rv32", StringComparison.Ordinal));
        Assert.Matches(@"Value: rv32e2p0_c2p0_zicsr2p0(_zca1p0)?\s*$", arch);
        Assert.Matches(@"Tag: 4\s+Value: 4\s+TagName: stack_align\s+Description: Stack alignment is 4-bytes", attributes);
        Assert.Contains("EF_RISCV_RVE", Ch32v003Build.Tool("llvm-readobj", "--file-headers", program.Elf));

        var code = Ch32v003Build.Tool("llvm-objdump", "-d", "--no-show-raw-insn", program.Elf);
        var instructions = code.Split('\n').Select(line => Instruction().Match(line)).Where(m => m.Success)
            .Select(m => SymbolName().Replace(m.Groups[1].Value, "")).ToList();
        Assert.True(instructions.Count > 100, code);
        // no M (mul, div, rem and their kin) and no register above x15 (RV32E has x0-x15: a6, a7, s2-s11, t3-t6 are not)
        Assert.DoesNotContain(instructions, MulDiv().IsMatch);
        Assert.DoesNotContain(instructions, HighRegister().IsMatch);

        var reset = Function(code, "chipsharp_reset");
        Assert.Contains("auipc\tgp,", reset);
        Assert.Contains("auipc\tsp,", reset);
        Assert.Contains("csrci\tmstatus, 0x8", reset);
        Assert.Contains("csrw\tmtvec,", reset);
        Assert.Contains("<chipsharp_chip_boot>", reset);
        Assert.Contains("<chipsharp_start>", reset);
        Assert.Contains("ebreak", code);   // the failure stop
        Assert.Contains("mret", code);     // the trap entry's return

        // the multiplication and division call nothing: a * or / of theirs would call themselves
        Assert.All((string[])["__mulsi3", "__divsi3", "__modsi3", "__muldi3"], helper =>
            Assert.DoesNotMatch(@"\t(jal|jalr|call|tail)\t", Function(code, helper)));

        // the trap entry saves the ten registers RV32E's C functions may change: a 40-byte frame
        var trap = Function(code, "chipsharp_trap");
        Assert.Contains("addi\tsp, sp, -0x28", trap);
        Assert.Contains("sw\ta5, 0x24(sp)", trap);
        Assert.Contains("lw\ta5, 0x24(sp)", trap);
        Assert.Contains("addi\tsp, sp, 0x28", trap);

        // the CH32V003's first steps: INTSYSCR (CSR 0x804) cleared, mtvec at the table (0) | 3, the flash's wait state
        // (FLASH_ACTLR, 0x40022000), the clock's registers (RCC, 0x40021000), the two waits: for PLLRDY, then for SWS
        var boot = Function(code, "chipsharp_chip_boot");
        Assert.Contains("csrw\t0x804, zero", boot);
        Assert.Contains("csrw\tmtvec,", boot);
        Assert.Contains(", 0x3", boot);
        Assert.Contains(", 0x40022", boot);
        Assert.Contains(", 0x40021", boot);
        Assert.Equal(2, LoopBack().Count(boot));
    }

    [Fact]
    public void The_trap_dispatch_has_a_weak_handler_for_each_of_the_PFICs_numbers()
    {
        var target = Targets.Load("ch32v003-evt");
        var dispatch = new Ch32v003Runtime().InterruptEntry(target).Files
            .Single(f => f.FileName == Riscv32Runtime.VectorsFileName).Content;

        Assert.Contains("CHIPSHARP_HANDLER(chipsharp_isr_irq38);\n", dispatch);
        Assert.DoesNotContain("chipsharp_isr_irq39", dispatch);
        Assert.Contains("static void (*const chipsharp_isr_handlers[39])(void) =", dispatch);
        Assert.Contains("if (id < 39u)", dispatch);
        Assert.Equal(Frontend.TargetInterrupt.HandlerSymbol(12), Riscv32Runtime.Handler(12)); // SysTick, the timer event's

        // the runtime's files: riscv32e's (with the multiplication and division RV32EC lacks) and the chip's
        var files = new Ch32v003Runtime().Startup(target).Files.Select(f => f.FileName).ToList();
        Assert.Contains(Riscv32Runtime.ArithmeticFileName, files);
        Assert.Contains(Ch32v003Runtime.VectorTableFileName, files);
        Assert.Contains(Ch32v003Runtime.BootFileName, files);
        Assert.Contains(Ch32v003Runtime.RegistersFileName, files);
        Assert.DoesNotContain(Riscv32Runtime.ArithmeticFileName, new Riscv32Runtime().Startup(Targets.Load("esp32c5-devkitc")).Files
            .Select(f => f.FileName));
        Assert.Equal(("riscv32", "riscv32e"), (new Riscv32Runtime().Name, Riscv32Runtime.Embedded().Name));
    }

    [Fact]
    public void A_clock_other_than_the_48_MHz_the_start_up_sets_is_refused()
    {
        var board = Targets.Load("ch32v003-evt");
        var settings = board.Settings.DeepClone().AsObject();
        settings["clockHz"] = 24000000;
        var other = new TargetDescription
        {
            Name = board.Name, InheritanceChain = board.InheritanceChain, Backend = board.Backend, Toolchain = board.Toolchain,
            Runtime = board.Runtime, Triple = board.Triple, Cpu = board.Cpu, PointerSize = board.PointerSize,
            Endian = board.Endian, Memory = board.Memory, Pins = board.Pins, Capabilities = board.Capabilities,
            Settings = settings, MissingValues = board.MissingValues,
        };

        var error = Assert.Throws<ChipSharpException>(() => new Ch32v003Runtime().Startup(other));

        Assert.Equal("Target 'ch32v003-evt': 'clockHz' is \"24000000\"; the ch32v003 runtime runs the CPU at 48000000 " +
            "(the PLL, twice the HSI's 24 MHz).", error.Message);
    }

    [Fact]
    public void A_collection_clears_RV32Es_two_callee_saved_registers_and_riscv32s_twelve()
    {
        var ch32 = LlvmCollectCalls.ForgetRegisters(Targets.Load("ch32v003-evt"))!;
        var c5 = LlvmCollectCalls.ForgetRegisters(Targets.Load("esp32c5-devkitc"))!;

        Assert.Equal("call void asm sideeffect \"li x8, 0\\0Ali x9, 0\", \"~{x8},~{x9}\"()", ch32);
        Assert.Contains("li x27, 0", c5);
    }

    [PicoFact]
    public void A_program_with_objects_links_on_RV32E_with_the_heap_between_the_variables_and_the_stack()
    {
        const string source = """
            using System;

            static class Program
            {
                sealed class Box(int value)
                {
                    public int Value { get; } = value;
                }

                static int Main()
                {
                    var boxes = new Box[4];
                    for (int i = 0; i < boxes.Length; i++)
                    {
                        boxes[i] = new Box(i * 3);
                    }

                    GC.Collect();
                    return boxes[3].Value;
                }
            }
            """;

        // the shipped board has no heap; this one gives it 1 KB, to link riscv32e's heap files (s0 and s1 scanned)
        var program = Ch32v003Build.Build(source, "Objects", _scratch, Ch32v003Build.WithConsole("1K"));

        var elf = program.File;
        Assert.Equal(elf.Symbols["__chipsharp_stack_bottom"], elf.Symbols["__chipsharp_heap_end"]);
        Assert.Equal(1024u, elf.Symbols["__chipsharp_heap_end"] - elf.Symbols["__chipsharp_heap_start"]);
        Assert.Contains("chipsharp_scan_stack", elf.Symbols.Keys);
    }

    /// <summary>The disassembly of one function, from its label to the blank line after it.</summary>
    private static string Function(string code, string name)
    {
        var start = code.IndexOf($"<{name}>:", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} is not in the program");
        var body = code[start..];
        var end = body.IndexOf("\n\n", StringComparison.Ordinal);
        return end < 0 ? body : body[..end];
    }

    /// <summary><paramref name="count"/> words of the program's flash from <paramref name="address"/> on.</summary>
    private static uint[] Words(ElfFile elf, uint address, int count)
    {
        var segment = elf.Segments.Single(s => s.Data.Length > 0 && s.PhysicalAddress <= address
            && address + 4 * (uint)count <= s.PhysicalAddress + (uint)s.Data.Length);
        var offset = (int)(address - segment.PhysicalAddress);
        return [.. Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(segment.Data.AsSpan(offset + 4 * i)))];
    }

    /// <summary>Where a jal at <paramref name="address"/> jumps: its 21-bit offset (imm[20|10:1|11|19:12]) added.</summary>
    private static uint JalTarget(uint jal, uint address)
    {
        var offset = ((jal >> 31) & 1) << 20 | ((jal >> 21) & 0x3ff) << 1 | ((jal >> 20) & 1) << 11 | ((jal >> 12) & 0xff) << 12;
        var signed = (int)(offset << 11) >> 11;
        return (uint)(address + signed);
    }

    /// <summary>A line of llvm-objdump's listing with an instruction: its address, then the instruction and its operands.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[0-9a-f]+:\s+(\S.*)$")]
    private static partial System.Text.RegularExpressions.Regex Instruction();

    /// <summary>The symbol llvm-objdump names after an address: &lt;chipsharp_trap+0x4&gt;.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("<[^>]*>")]
    private static partial System.Text.RegularExpressions.Regex SymbolName();

    /// <summary>A branch back into chipsharp_chip_boot: a loop that waits for a flag.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\tb\w+\t[^\n]*<chipsharp_chip_boot\+0x")]
    private static partial System.Text.RegularExpressions.Regex LoopBack();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(mul|mulh|mulhu|mulhsu|div|divu|rem|remu)\s")]
    private static partial System.Text.RegularExpressions.Regex MulDiv();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(x1[6-9]|x2\d|x3[01]|a6|a7|s[2-9]|s1[01]|t[3-6])\b")]
    private static partial System.Text.RegularExpressions.Regex HighRegister();
}
