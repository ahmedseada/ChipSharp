using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Image.Bin;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Build;

/// <summary>
/// The "bin" image format (<see cref="BinImageFormat"/>), the CH32V003's: the bytes of a known .elf from the flash's
/// origin (0x08000000), the sections at their load addresses (.data's in flash), the gaps 0xFF, nothing of .bss or of a
/// section that is not loaded; a program SDCC linked (.ihx); the errors for a program bigger than the flash and for
/// bytes outside it; and the image of a Pico program, which is what llvm-objcopy -O binary --gap-fill 0xff writes.
/// </summary>
public sealed class BinImageFormatTests : IDisposable
{
    /// <summary>A CH32V003 as the tests see it: 16 KB of flash at 0x08000000, 2 KB of SRAM at 0x20000000.</summary>
    private const string Board = """
        {
          "name": "ch32-test-board",
          "inherits": "riscv32",
          "memory": {
            "flash": { "origin": "0x08000000", "size": "16K" },
            "ram":   { "origin": "0x20000000", "size": "2K" }
          },
          "image": "bin",
          "flasher": "wlink",
          "wlink": { "chip": "CH32V003" }
        }
        """;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    private TargetDescription? _target;

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void The_bin_format_is_registered_and_its_file_is_a_bin()
    {
        var target = Target();

        Assert.Equal(("bin", "wlink"), (target.Image, target.Flasher));
        Assert.Equal("bin", DefaultComponents.ImageExtension("bin"));
        IImageFormat format = new BinImageFormat();
        Assert.Equal(("bin", "bin"), (format.Name, format.Extension));
        Assert.Empty(format.Variants);
        Assert.IsType<BinImageFormat>(DefaultComponents.CreateRegistry(
            new BuildContext(new BuildOptions(), target, "Blink", _scratch)).Get<IImageFormat>("bin"));
        Assert.Equal(("flash", new MemoryRegion(0x0800_0000, 16 * 1024)), BinImageFormat.Flash(target));
    }

    [Fact]
    public void The_image_of_a_known_elf_is_its_flash_from_0x08000000_with_the_gaps_0xFF()
    {
        var elf = Elf(
            (".text", 0x0800_0000, 0x0800_0000, [0x6f, 0x00, 0x40, 0x00, 1, 2, 3, 4], Bss: false),
            (".rodata", 0x0800_0010, 0x0800_0010, Encoding.ASCII.GetBytes("Hi!\0"), Bss: false),
            // .data runs in RAM, its starting values are loaded in flash, right after the constants
            (".data", 0x2000_0000, 0x0800_0014, [0xAA, 0xBB, 0xCC, 0xDD], Bss: false),
            (".bss", 0x2000_0004, 0x2000_0004, new byte[16], Bss: true));

        var image = BinImageFormat.Make(elf, Target(), "known.elf");

        Assert.Equal(
            [
                0x6f, 0x00, 0x40, 0x00, 1, 2, 3, 4,               // .text at 0x08000000
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,   // the gap up to 0x08000010: erased flash
                (byte)'H', (byte)'i', (byte)'!', 0,               // .rodata
                0xAA, 0xBB, 0xCC, 0xDD,                           // .data's starting values; nothing of .bss
            ],
            image);
    }

    [Fact]
    public void A_program_that_starts_past_the_origin_is_written_from_the_origin_and_ends_at_its_last_byte()
    {
        var elf = Elf((".text", 0x0800_0100, 0x0800_0100, [1, 2, 3], Bss: false));

        var image = BinImageFormat.Make(elf, Target(), "late.elf");

        Assert.Equal(0x103, image.Length);
        Assert.All(image[..0x100], b => Assert.Equal(BinImageFormat.Erased, b));
        Assert.Equal([1, 2, 3], image[0x100..]);
    }

    [Fact]
    public void A_program_bigger_than_the_16_KB_of_flash_is_refused_saying_by_how_much()
    {
        // 16 KB of code and 528 bytes of .data's starting values after it: 528 bytes too many
        var elf = Elf(
            (".text", 0x0800_0000, 0x0800_0000, new byte[16 * 1024], Bss: false),
            (".data", 0x2000_0000, 0x0800_4000, new byte[528], Bss: false));

        var error = Assert.Throws<ChipSharpException>(() => BinImageFormat.Make(elf, Target(), "big.elf"));

        Assert.Equal(
            "'big.elf' does not fit in the flash of target 'ch32-test-board': its image is 16.5 KB (0x08000000-0x0800420f), " +
            "528 bytes more than the 16 KB of flash there is (0x08000000-0x08003fff). Make the program smaller.",
            error.Message);
        // exactly 16 KB fits
        Assert.Equal(16 * 1024, BinImageFormat.Make(
            Elf((".text", 0x0800_0000, 0x0800_0000, new byte[16 * 1024], Bss: false)), Target(), "full.elf").Length);
    }

    [Fact]
    public void Bytes_outside_the_flash_and_sections_that_overlap_are_refused()
    {
        // linked for RAM (no load address in flash), or below the flash (the alias at 0x00000000)
        Assert.Equal(
            "'ram.elf' loads section .data (4 bytes) at 0x20000000-0x20000003, which is not in the flash of target " +
            "'ch32-test-board' (0x08000000-0x08003fff): a .bin holds the bytes of the flash alone.",
            Assert.Throws<ChipSharpException>(() => BinImageFormat.Make(Elf(
                (".text", 0x0800_0000, 0x0800_0000, [1], Bss: false),
                (".data", 0x2000_0000, 0x2000_0000, [1, 2, 3, 4], Bss: false)), Target(), "ram.elf")).Message);
        Assert.StartsWith("'alias.elf' loads section .text (2 bytes) at 0x00000000-0x00000001, which is not in the flash",
            Assert.Throws<ChipSharpException>(() => BinImageFormat.Make(
                Elf((".text", 0, 0, [1, 2], Bss: false)), Target(), "alias.elf")).Message);
        Assert.Equal(
            "'twice.elf' loads two sections at the same address: .rodata starts at 0x08000002, before the section ahead of " +
            "it ends.",
            Assert.Throws<ChipSharpException>(() => BinImageFormat.Make(Elf(
                (".text", 0x0800_0000, 0x0800_0000, [1, 2, 3, 4], Bss: false),
                (".rodata", 0x0800_0002, 0x0800_0002, [5], Bss: false)), Target(), "twice.elf")).Message);
        Assert.Equal("'empty.elf' has nothing to load: no section has any bytes.",
            Assert.Throws<ChipSharpException>(() => BinImageFormat.Make(
                Elf((".bss", 0x2000_0000, 0x2000_0000, new byte[4], Bss: true)), Target(), "empty.elf")).Message);
    }

    [Fact]
    public void A_target_without_a_flash_region_is_told_what_to_give()
    {
        var target = new TargetDescription
        {
            Name = "no-flash", InheritanceChain = ["no-flash"], Backend = "llvm", PointerSize = 4, Endian = Endianness.Little,
            Settings = System.Text.Json.Nodes.JsonNode.Parse("""{ "link": { "code": "rom" } }""")!.AsObject(),
            Memory = new Dictionary<string, MemoryRegion> { ["ram"] = new(0x2000_0000, 2048) },
        };

        var error = Assert.Throws<ChipSharpException>(() => BinImageFormat.Flash(target));

        Assert.Equal("Target 'no-flash' has no flash to make a .bin of: its \"link.code\" must name the memory region the " +
            "program is written into (e.g. \"flash\").", error.Message);
    }

    [Fact]
    public void A_program_linked_as_intel_hex_is_written_from_its_bytes()
    {
        Directory.CreateDirectory(_scratch);
        var ihx = Path.Combine(_scratch, "Count.ihx");
        // 2 bytes at 0x08000000, 1 byte at 0x08000004 (extended linear address 0x0800)
        File.WriteAllText(ihx, ":020000040800F2\r\n:020000001122CB\r\n:0100040033C8\r\n:00000001FF\r\n");
        using var output = new MemoryStream();

        new BinImageFormat().Package(new LinkedBinary(ihx), Target(), output);

        Assert.Equal([0x11, 0x22, 0xFF, 0xFF, 0x33], output.ToArray());
    }

    [PicoFact]
    public async Task The_bin_of_a_Pico_program_is_what_llvm_objcopy_writes_with_the_gaps_0xFF()
    {
        var program = PicoBuild.Build(PicoBuild.BlinkSource, "PicoBlink", _scratch);
        var bin = Path.Combine(_scratch, "PicoBlink.bin");
        await using (var output = File.Create(bin))
        {
            new BinImageFormat().Package(new LinkedBinary(program.Elf), PicoBuild.Pico, output);
        }

        var objcopy = Path.Combine(_scratch, "PicoBlink.objcopy.bin");
        var result = await Llvm.LlvmTools.RunAsync(PicoTools.LlvmTool("llvm-objcopy"),
            ["-O", "binary", "--gap-fill", "0xff", program.Elf, objcopy], _scratch);
        Assert.True(result.ExitCode == 0, result.ToString());

        var bytes = await File.ReadAllBytesAsync(bin);
        Assert.Equal(await File.ReadAllBytesAsync(objcopy), bytes);
        // the Pico's flash starts with boot2 (256 bytes), at 0x10000000
        Assert.Equal(ElfFile.Read(program.Elf).Sections.Single(s => s.Name == ".boot2").Data, bytes[..256]);
    }

    /// <summary>The test's CH32V003 board, <see cref="Board"/>, on the shipped riscv32 target.</summary>
    private TargetDescription Target() => _target ??=
        new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"), ("ch32-test-board", Board)))
            .Load("ch32-test-board");

    /// <summary>
    /// A RISC-V .elf with these sections (their run address, load address and bytes; <c>Bss</c>: NOBITS, nothing in the
    /// file), each loaded by a segment of its own, a section that is not loaded (.comment) and the section names.
    /// </summary>
    internal static ElfFile Elf(params (string Name, uint Address, uint LoadAddress, byte[] Data, bool Bss)[] sections)
    {
        var names = new StringBuilder("\0");
        var nameOffsets = new List<int>();
        foreach (var section in sections.Select(s => s.Name).Append(".comment").Append(".shstrtab"))
        {
            nameOffsets.Add(names.Length);
            names.Append(section).Append('\0');
        }

        var loaded = sections.Where(s => !s.Bss).ToList();
        var comment = Encoding.ASCII.GetBytes("ChipSharp\0");
        var dataStart = 52 + 32 * loaded.Count;
        var offsets = new List<int>();
        var at = dataStart;
        foreach (var section in sections)
        {
            offsets.Add(at);
            at += section.Bss ? 0 : section.Data.Length;
        }

        var commentOffset = at;
        var namesOffset = commentOffset + comment.Length;
        var sectionHeaders = namesOffset + names.Length;

        var bytes = new List<byte>();
        void U16(int value) => bytes.AddRange(BitConverter.GetBytes((ushort)value));
        void U32(long value) => bytes.AddRange(BitConverter.GetBytes((uint)value));

        bytes.AddRange([0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        U16(2);                                   // ET_EXEC
        U16(243);                                 // EM_RISCV
        U32(1);
        U32(0x0800_0000);                         // the entry point
        U32(52);                                  // the program headers follow the ELF header
        U32(sectionHeaders);
        U32(0);
        U16(52);
        U16(32);
        U16(loaded.Count);
        U16(40);
        U16(sections.Length + 3);                 // the empty section 0, the sections, .comment, .shstrtab
        U16(sections.Length + 2);
        for (var i = 0; i < sections.Length; i++)
        {
            if (!sections[i].Bss)
            {
                U32(1);                           // PT_LOAD
                U32(offsets[i]);
                U32(sections[i].Address);
                U32(sections[i].LoadAddress);
                U32(sections[i].Data.Length);
                U32(sections[i].Data.Length);
                U32(5);
                U32(4);
            }
        }

        foreach (var section in loaded)
        {
            bytes.AddRange(section.Data);
        }

        bytes.AddRange(comment);
        bytes.AddRange(Encoding.ASCII.GetBytes(names.ToString()));

        void Header(int name, int type, int flags, long address, int offset, int size)
        {
            U32(name);
            U32(type);
            U32(flags);
            U32(address);
            U32(offset);
            U32(size);
            U32(0);
            U32(0);
            U32(1);
            U32(0);
        }

        Header(0, 0, 0, 0, 0, 0);
        for (var i = 0; i < sections.Length; i++)
        {
            Header(nameOffsets[i], sections[i].Bss ? 8 : 1, 0x2 | 0x4, sections[i].Address, offsets[i], sections[i].Data.Length);
        }

        Header(nameOffsets[^2], 1, 0, 0, commentOffset, comment.Length);   // .comment: PROGBITS, not allocated
        Header(nameOffsets[^1], 3, 0, 0, namesOffset, names.Length);       // .shstrtab: STRTAB
        return ElfFile.Parse([.. bytes], "known.elf");
    }
}
