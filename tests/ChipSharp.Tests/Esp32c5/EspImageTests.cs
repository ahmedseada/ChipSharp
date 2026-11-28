using System.Buffers.Binary;
using System.Security.Cryptography;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Image.Esp;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// The image format "esp", for the ESP32-C5: the bytes of the image of a known .elf (the headers' fields, the chip id 23,
/// the segment table, the checksum and the SHA-256, each worked out again here), the reader that parses it back and
/// checks it, the error for a segment outside the SRAM the ROM loads into, and the image of a program built for the board.
/// </summary>
public sealed class EspImageTests : IDisposable
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    /// <summary>An .elf with these loadable segments (load address = run address, all bytes in the file) and entry point.</summary>
    private static ElfFile Elf(uint entry, params (uint Address, byte[] Data)[] segments)
    {
        var bytes = new List<byte>();
        void U16(int value) => bytes.AddRange(BitConverter.GetBytes((ushort)value));
        void U32(uint value) => bytes.AddRange(BitConverter.GetBytes(value));

        bytes.AddRange([0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        U16(2);                                   // ET_EXEC
        U16(243);                                 // EM_RISCV
        U32(1);
        U32(entry);
        U32(52);                                  // the program headers follow the ELF header
        U32(0);                                   // no section headers
        U32(0);
        U16(52);
        U16(32);
        U16(segments.Length);
        U16(40);
        U16(0);
        U16(0);
        var offset = 52 + 32 * segments.Length;
        foreach (var (address, data) in segments)
        {
            U32(1);                               // PT_LOAD
            U32((uint)offset);
            U32(address);
            U32(address);
            U32((uint)data.Length);
            U32((uint)data.Length);
            U32(7);
            U32(4);
            offset += data.Length;
        }

        foreach (var (_, data) in segments)
        {
            bytes.AddRange(data);
        }

        return ElfFile.Parse([.. bytes], "known.elf");
    }

    [Fact]
    public void The_board_is_built_to_the_esp_image_which_is_a_bin_and_flashed_with_esptool()
    {
        var board = Shipped.Load("esp32c5-devkitc");

        Assert.Equal(("esp", "esptool"), (board.Image, board.Flasher));
        Assert.Equal("23", board.Setting("esp.chipId"));
        Assert.Equal("0x2000", board.Setting("esptool.offset"));
        Assert.Equal("bin", DefaultComponents.ImageExtension("esp"));
        Assert.Equal("uf2", DefaultComponents.ImageExtension("uf2"));
        Assert.Equal("hex", DefaultComponents.ImageExtension("hex"));
        Assert.Equal("esp", new EspImageFormat().Name);
    }

    [Fact]
    public void The_image_of_a_known_elf_has_the_headers_the_segments_the_checksum_and_the_sha256_esptool_writes()
    {
        byte[] first = [1, 2, 3, 4, 5, 6];                 // 6 bytes: padded to 8
        byte[] second = [0xAA, 0xBB, 0xCC, 0xDD];
        var elf = Elf(0x40800000, (0x40800000, first), (0x40800100, second));

        var bytes = EspImageFormat.Make(elf, Shipped.Load("esp32c5-devkitc"), "known.elf").Write();

        // the header: magic, 2 segments, SPI mode dio (2), 8 MB at 40 MHz (0x30 | 0x0), the entry point
        Assert.Equal([0xE9, 2, 0x02, 0x30], bytes[..4]);
        Assert.Equal(0x40800000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        // the extended header: WP pin 0xEE, drive strengths 0, chip id 23, min revision 0 (1 + 2 bytes), max revision
        // 65535, 4 reserved bytes, hash appended
        Assert.Equal([0xEE, 0, 0, 0, 23, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 1], bytes[8..24]);
        // the segments: address, length, bytes
        Assert.Equal([0x00, 0x00, 0x80, 0x40, 8, 0, 0, 0, 1, 2, 3, 4, 5, 6, 0, 0], bytes[24..40]);
        Assert.Equal([0x00, 0x01, 0x80, 0x40, 4, 0, 0, 0, 0xAA, 0xBB, 0xCC, 0xDD], bytes[40..52]);
        // zeros up to byte 63 (15 mod 16), where the checksum is: 0xEF xor every byte of the segments
        Assert.All(bytes[52..63], b => Assert.Equal(0, b));
        var checksum = (byte)(0xEF ^ 1 ^ 2 ^ 3 ^ 4 ^ 5 ^ 6 ^ 0xAA ^ 0xBB ^ 0xCC ^ 0xDD);
        Assert.Equal(checksum, bytes[63]);
        // then the SHA-256 of the 64 bytes before it, and nothing more
        Assert.Equal(64 + 32, bytes.Length);
        Assert.Equal(SHA256.HashData(bytes[..64]), bytes[64..]);
    }

    [Fact]
    public void The_reader_parses_an_image_back_and_checks_its_checksum_and_digest()
    {
        var elf = Elf(0x40800010, (0x40800000, [1, 2, 3, 4, 5, 6, 7, 8]), (0x40800100, [9, 10, 11, 12]));
        var bytes = EspImageFormat.Make(elf, Shipped.Load("esp32c5-devkitc"), "known.elf").Write();

        var image = EspImage.Read(bytes);

        Assert.Equal(23, image.ChipId);
        Assert.Equal(0x40800010u, image.Entry);
        Assert.Equal((byte)2, image.FlashMode);
        Assert.Equal((byte)0x30, image.FlashSizeAndFrequency);
        Assert.Equal(EspImage.WpPinDisabled, image.WpPin);
        Assert.Equal((0, 65535), (image.MinRevisionFull, (int)image.MaxRevisionFull));
        Assert.True(image.DigestAppended);
        Assert.Equal([(0x40800000u, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), (0x40800100u, new byte[] { 9, 10, 11, 12 })],
            image.Segments.Select(s => (s.Address, s.Data)));
        Assert.Equal(bytes, image.Write());

        // the same image without the digest: 32 bytes shorter, which reads back too
        var plain = new EspImage
        {
            ChipId = 23, Entry = 0x40800010, DigestAppended = false, Segments = image.Segments,
        }.Write();
        Assert.Equal(64, plain.Length);
        Assert.False(EspImage.Read(plain).DigestAppended);

        var badChecksum = (byte[])bytes.Clone();
        badChecksum[63] ^= 1;
        Assert.StartsWith("The ESP image's checksum is", Assert.Throws<ChipSharpException>(() => EspImage.Read(badChecksum)).Message);
        var badDigest = (byte[])bytes.Clone();
        badDigest[^1] ^= 1;
        Assert.Equal("The ESP image's SHA-256 does not match its bytes.",
            Assert.Throws<ChipSharpException>(() => EspImage.Read(badDigest)).Message);
        Assert.Equal("Not an ESP image: it must start with 0xE9.",
            Assert.Throws<ChipSharpException>(() => EspImage.Read(new byte[40])).Message);
        Assert.StartsWith("The ESP image ends inside the header of segment 0", Assert.Throws<ChipSharpException>(() => EspImage.Read(bytes[..30])).Message);
    }

    [Fact]
    public void A_segment_outside_the_SRAM_the_ROM_loads_into_is_a_build_error()
    {
        var board = Shipped.Load("esp32c5-devkitc");

        // the last byte the program may use is 0x4085c59f: the ROM's own stack and data start at 0x4085c5a0
        _ = EspImageFormat.Make(Elf(0x40800000, (0x4085c59c, [1, 2, 3, 4])), board, "ok.elf");
        var over = Assert.Throws<ChipSharpException>(() =>
            EspImageFormat.Make(Elf(0x40800000, (0x4085c59d, [1, 2, 3, 4])), board, "big.elf"));
        Assert.Equal(
            "'big.elf' loads 4 bytes at 0x4085c59d-0x4085c5a0, which is not inside the SRAM the esp32c5-devkitc's ROM " +
            "loads an image into (0x40800000-0x4085c59f: above that is the ROM's own stack and data while it boots).",
            over.Message);
        Assert.Contains("0x42000000", Assert.Throws<ChipSharpException>(() =>
            EspImageFormat.Make(Elf(0x42000000, (0x42000000, [1, 2, 3, 4])), board, "flash.elf")).Message);
        Assert.Equal("'none.elf' has nothing to load: no segment has any bytes.",
            Assert.Throws<ChipSharpException>(() => EspImageFormat.Make(Elf(0x40800000), board, "none.elf")).Message);
    }

    [Fact]
    public void The_chip_id_and_the_flash_settings_come_from_the_target()
    {
        var targets = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("fast", """{ "inherits": "esp32c5-devkitc", "esp": { "flashMode": "qio", "flashSize": "16MB", "flashFreq": "80m" } }"""),
            ("no-id", """{ "inherits": "esp32c5-devkitc", "esp": { "chipId": null } }"""),
            ("bad-mode", """{ "inherits": "esp32c5-devkitc", "esp": { "flashMode": "fast" } }""")));
        var elf = Elf(0x40800000, (0x40800000, [1, 2, 3, 4]));

        var fast = EspImageFormat.Make(elf, targets.Load("fast"), "x.elf");

        Assert.Equal(((byte)0, (byte)0x4F), (fast.FlashMode, fast.FlashSizeAndFrequency));
        Assert.StartsWith("Target 'no-id': 'esp.chipId' is missing",
            Assert.Throws<ChipSharpException>(() => EspImageFormat.Make(elf, targets.Load("no-id"), "x.elf")).Message);
        Assert.StartsWith("Target 'bad-mode': 'esp.flashMode' is \"fast\"; write one of qio, qout, dio, dout.",
            Assert.Throws<ChipSharpException>(() => EspImageFormat.Make(elf, targets.Load("bad-mode"), "x.elf")).Message);
    }

    [Fact]
    public void An_image_with_more_segments_than_the_ROM_loads_is_refused()
    {
        var segments = Enumerable.Range(0, 17).Select(i => ((uint)(0x40800000 + i * 16), new byte[] { 1, 2, 3, 4 })).ToArray();

        Assert.Equal("'many.elf' has 17 loadable segments; the ROM loads at most 16.",
            Assert.Throws<ChipSharpException>(() =>
                EspImageFormat.Make(Elf(0x40800000, segments), Shipped.Load("esp32c5-devkitc"), "many.elf")).Message);
    }

    [Esp32c5Theory]
    [InlineData("Count")]
    public void The_image_of_a_program_built_for_the_board_holds_its_elfs_segments_and_entry(string name)
    {
        var board = Esp32c5Build.Board;
        var program = Esp32c5Build.Build("""
            static class Program
            {
                static int total = 7;
                static int Main()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        total += i;
                    }

                    return total;
                }
            }
            """, name, _scratch);
        var path = Path.Combine(_scratch, name + ".bin");

        using (var output = File.Create(path))
        {
            new EspImageFormat().Package(new LinkedBinary(program.Elf), board, output);
        }

        var image = EspImage.Read(File.ReadAllBytes(path));
        Assert.Equal(program.File.Entry, image.Entry);
        Assert.Equal(program.File.Symbols["chipsharp_reset"], image.Entry);
        Assert.Equal(23, image.ChipId);
        // the ELF's segments in address order, those that touch made one: the same bytes at the same addresses
        Assert.Equal(program.File.Segments.Sum(s => s.Data.Length), image.Segments.Sum(s => s.Data.Length));
        Assert.Equal(program.File.Segments.Min(s => s.PhysicalAddress), image.Segments[0].Address);
        Assert.Equal(program.File.Segments.OrderBy(s => s.PhysicalAddress).SelectMany(s => s.Data), image.Segments.SelectMany(s => s.Data));
        Assert.All(image.Segments, s => Assert.InRange(s.Address + (ulong)s.Data.Length, 0x40800001UL, 0x4085c5a0UL));
        Assert.Equal(0L, new FileInfo(path).Length % 16);
    }

    private const string Program = """
        static class Program
        {
            static int total = 7;
            static int[] table = { 3, 1, 4, 1, 5, 9, 2, 6 };
            static int Main()
            {
                for (int i = 0; i < table.Length; i++)
                {
                    total += table[i] * i;
                }

                return total;
            }
        }
        """;

    /// <summary>Runs the real esptool (never a write to a board) and returns what it printed.</summary>
    private static string Esptool(params string[] arguments)
    {
        var tool = EsptoolTheoryAttribute.Tool!;
        var start = new System.Diagnostics.ProcessStartInfo(tool.Path)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in tool.Prefix.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"esptool {string.Join(' ', arguments)}: {output}{error.Result}");
        return output + error.Result;
    }

    [EsptoolTheory]
    [InlineData("EspCheck")]
    public void The_real_esptool_accepts_the_image_and_makes_the_same_bytes_from_the_same_elf(string name)
    {
        var program = Esp32c5Build.Build(Program, name, _scratch);
        var path = Path.Combine(_scratch, name + ".bin");
        using (var output = File.Create(path))
        {
            new EspImageFormat().Package(new LinkedBinary(program.Elf), Esp32c5Build.Board, output);
        }

        // image-info: the header, the chip id, the checksum and the SHA-256 are valid
        var info = Esptool("--chip", "esp32c5", "image-info", path);
        Assert.Contains("ESP32-C5", info, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(valid)", info);
        Assert.DoesNotContain("invalid", info, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Validation hash", info, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Entry point: 0x{program.File.Entry:x8}", info, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Flash size: 8MB", info);
        Assert.Contains("Flash freq: 40m", info);
        Assert.Contains("Flash mode: DIO", info);

        // esptool's own elf2image of the same ELF (from its segments, which is what the ROM loads): the same bytes
        var reference = Path.Combine(_scratch, "reference.bin");
        Esptool("--chip", "esp32c5", "elf2image", "--use-segments", "--flash-mode", "dio", "--flash-freq", "40m",
            "--flash-size", "8MB", "-o", reference, program.Elf);
        var mine = File.ReadAllBytes(path);
        var theirs = File.ReadAllBytes(reference);
        string Segs(byte[] b) => string.Join(" ", EspImage.Read(b).Segments.Select(x => $"{x.Address:x8}+{x.Data.Length}"));
        Assert.Equal(Segs(theirs), Segs(mine));
        Assert.Equal(theirs.Length, mine.Length);
        Assert.Equal(Convert.ToHexString(theirs), Convert.ToHexString(mine));
    }
}
