using System.Buffers.Binary;
using ChipSharp.Abstractions;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The uf2 image format against the UF2 specification (https://github.com/microsoft/uf2): 512-byte blocks with the
/// three magic numbers, 256-byte payloads at 256-byte boundaries, blocks numbered from 0 with the total in each,
/// and the family id flag (0x2000) with the RP2040's family id.
/// </summary>
public sealed class Uf2ImageFormatTests : IDisposable
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public Uf2ImageFormatTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void Every_block_follows_the_specification()
    {
        var pages = new List<(uint, byte[])>
        {
            (0x10000000, Filled(0x11)),
            (0x10000100, Filled(0x22)),
            (0x10000400, Filled(0x33)),
        };
        using var output = new MemoryStream();

        Uf2ImageFormat.Write(pages, 0xe48bff56, output);

        var file = output.ToArray();
        Assert.Equal(3 * 512, file.Length);
        for (var i = 0; i < 3; i++)
        {
            var block = file.AsSpan(i * 512, 512).ToArray();
            uint Word(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset));
            Assert.Equal(0x0A324655u, Word(0));   // magicStart0, "UF2\n"
            Assert.Equal(0x9E5D5157u, Word(4));   // magicStart1
            Assert.Equal(0x00002000u, Word(8));   // flags: familyID present
            Assert.Equal(pages[i].Item1, Word(12)); // targetAddr
            Assert.Equal(256u, Word(16));         // payloadSize
            Assert.Equal((uint)i, Word(20));      // blockNo
            Assert.Equal(3u, Word(24));           // numBlocks
            Assert.Equal(0xe48bff56u, Word(28));  // familyID
            Assert.Equal(pages[i].Item2, block[32..288]);
            Assert.All(block[288..508], b => Assert.Equal(0, b)); // the rest of the data area
            Assert.Equal(0x0AB16F30u, Word(508)); // magicEnd
        }
    }

    [Fact]
    public void Segments_are_cut_into_pages_by_load_address_and_gaps_in_a_page_are_zero()
    {
        var elf = WriteElf(
            (0x10000000, 0x10000000, Filled(0xaa, 300)),     // a page and a bit
            (0x10000140, 0x20000000, Filled(0xbb, 8)),       // .data: used in RAM, loaded after it in flash
            (0x10000400, 0x10000400, Filled(0xcc, 4)));      // a page further on
        using var output = new MemoryStream();

        new Uf2ImageFormat().Package(new LinkedBinary(elf), Targets.Load("pico"), output);

        var blocks = Blocks(output.ToArray());
        Assert.Equal([0x10000000u, 0x10000100u, 0x10000400u], blocks.Select(b => b.Address));
        Assert.Equal(Filled(0xaa), blocks[0].Payload);
        Assert.Equal([.. Filled(0xaa, 44), .. new byte[20], .. Filled(0xbb, 8), .. new byte[184]], blocks[1].Payload);
        Assert.Equal([.. Filled(0xcc, 4), .. new byte[252]], blocks[2].Payload);
    }

    [Fact]
    public void Bytes_outside_the_targets_memory_are_an_error()
    {
        var elf = WriteElf((0x30000000, 0x30000000, Filled(1, 4)));

        var error = Assert.Throws<ChipSharpException>(
            () => new Uf2ImageFormat().Package(new LinkedBinary(elf), Targets.Load("pico"), Stream.Null));

        Assert.Contains("loads 4 bytes at 0x30000000-0x30000003, which is not inside a memory region of target 'pico'", error.Message);
    }

    [Fact]
    public void Two_segments_that_load_the_same_byte_are_an_error()
    {
        var elf = WriteElf((0x10000000, 0x10000000, Filled(1, 8)), (0x10000004, 0x20000000, Filled(2, 8)));

        var error = Assert.Throws<ChipSharpException>(
            () => new Uf2ImageFormat().Package(new LinkedBinary(elf), Targets.Load("pico"), Stream.Null));

        Assert.Contains("loads two segments at the same address, 0x10000004", error.Message);
    }

    [Fact]
    public void A_target_without_a_family_id_cannot_be_packaged_as_uf2()
    {
        var host = Targets.Load("host");

        var error = Assert.Throws<ChipSharpException>(() => Uf2ImageFormat.FamilyId(host));

        Assert.StartsWith("Target 'host': 'uf2FamilyId' is missing", error.Message);
        Assert.Equal(0xe48bff56u, Uf2ImageFormat.FamilyId(Targets.Load("pico")));
    }

    [Fact]
    public void A_file_that_is_not_ELF_is_reported()
    {
        var path = Path.Combine(_scratch, "notes.txt");
        File.WriteAllText(path, "not a program, just some text that is long enough to be an ELF header if it were one");

        var error = Assert.Throws<ChipSharpException>(
            () => new Uf2ImageFormat().Package(new LinkedBinary(path), Targets.Load("pico"), Stream.Null));

        Assert.Equal($"'{path}' is not an ELF file.", error.Message);
    }

    [PicoFact]
    public void The_blink_uf2_holds_exactly_the_flash_bytes_of_the_elf()
    {
        var program = PicoBuild.Build(PicoBuild.BlinkSource, "PicoBlink", _scratch);

        var blocks = Blocks(File.ReadAllBytes(program.Uf2));

        var flash = PicoBuild.FlashImage(ElfFile.Read(program.Elf));
        Assert.Equal(0x10000000u, blocks[0].Address);
        Assert.Equal(Enumerable.Range(0, blocks.Count).Select(i => 0x10000000u + (uint)i * 256), blocks.Select(b => b.Address));
        var payload = blocks.SelectMany(b => b.Payload).ToArray();
        Assert.Equal(flash, payload[..flash.Length]);
        Assert.All(payload[flash.Length..], b => Assert.Equal(0, b));
    }

    private static byte[] Filled(byte value, int count = 256) => Enumerable.Repeat(value, count).ToArray();

    private static List<(uint Address, byte[] Payload)> Blocks(byte[] file)
    {
        var blocks = new List<(uint, byte[])>();
        for (var at = 0; at < file.Length; at += 512)
        {
            var block = file.AsSpan(at, 512);
            Assert.Equal(0x0A324655u, BinaryPrimitives.ReadUInt32LittleEndian(block));
            Assert.Equal((uint)(at / 512), BinaryPrimitives.ReadUInt32LittleEndian(block[20..]));
            Assert.Equal((uint)(file.Length / 512), BinaryPrimitives.ReadUInt32LittleEndian(block[24..]));
            blocks.Add((BinaryPrimitives.ReadUInt32LittleEndian(block[12..]), block.Slice(32, 256).ToArray()));
        }

        return blocks;
    }

    /// <summary>A minimal 32-bit ARM ELF file with one PT_LOAD segment per (physical, virtual, bytes); returns its path.</summary>
    private string WriteElf(params (uint Physical, uint Virtual, byte[] Data)[] segments)
    {
        var headers = 52 + 32 * segments.Length;
        var file = new byte[headers + segments.Sum(s => s.Data.Length)];
        file[0] = 0x7f;
        "ELF"u8.CopyTo(file.AsSpan(1));
        file[4] = 1; // 32-bit
        file[5] = 1; // little-endian
        file[6] = 1;
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], 2);  // ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], 40); // ARM
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], 52); // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(span[40..], 52); // e_ehsize
        BinaryPrimitives.WriteUInt16LittleEndian(span[42..], 32); // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(span[44..], (ushort)segments.Length);
        var offset = headers;
        for (var i = 0; i < segments.Length; i++)
        {
            var ph = span[(52 + 32 * i)..];
            BinaryPrimitives.WriteUInt32LittleEndian(ph[0..], 1); // PT_LOAD
            BinaryPrimitives.WriteUInt32LittleEndian(ph[4..], (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(ph[8..], segments[i].Virtual);
            BinaryPrimitives.WriteUInt32LittleEndian(ph[12..], segments[i].Physical);
            BinaryPrimitives.WriteUInt32LittleEndian(ph[16..], (uint)segments[i].Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(ph[20..], (uint)segments[i].Data.Length);
            segments[i].Data.CopyTo(span[offset..]);
            offset += segments[i].Data.Length;
        }

        var path = Path.Combine(_scratch, $"test{Guid.NewGuid():N}.elf");
        File.WriteAllBytes(path, file);
        return path;
    }
}
