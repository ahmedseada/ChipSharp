using ChipSharp.Abstractions;
using ChipSharp.Image.Hex;

namespace ChipSharp.Tests.Sdcc;

/// <summary>
/// Intel HEX read back (<see cref="IntelHexFile"/>), as the "hex" image reads SDCC's .ihx: records in any order and of
/// any length, the extended address records, the start address, and the errors of a file that is not well formed.
/// </summary>
public sealed class IntelHexFileTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void Records_in_any_order_are_blocks_of_consecutive_addresses_in_address_order()
    {
        // as SDCC writes them for a PDK14 program: by area, not by address, up to 32 bytes a record, LF only
        var hex = IntelHexFile.Parse("""
            :020020007B0063
            :14000000000001130D2F0228FE2C82014938002B1230113096
            :02002200293083
            :00000001FF

            """.Replace("\r\n", "\n", StringComparison.Ordinal), "Blink.ihx");

        Assert.Equal(0U, hex.Entry);
        Assert.Equal([(0U, 20), (0x20U, 4)], hex.Blocks.Select(block => (block.Address, block.Data.Length)));
        Assert.Equal([0x7B, 0x00, 0x29, 0x30], hex.Blocks[1].Data);
    }

    [Fact]
    public void What_the_hex_image_writes_reads_back_as_the_same_bytes_past_64_KB_and_1_MB_with_its_start_address()
    {
        (uint, byte[])[] blocks = [(0x10, [1, 2, 3]), (0xfff8, [.. Enumerable.Range(0, 20).Select(i => (byte)i)]),
            (0x12_0000, [0xAA, 0xBB])];
        using var stream = new MemoryStream();
        IntelHexImageFormat.Write(blocks, 0x10, stream);

        var hex = IntelHexFile.Parse(System.Text.Encoding.ASCII.GetString(stream.ToArray()), "image");

        Assert.Equal(0x10U, hex.Entry);
        Assert.Equal(blocks.Select(block => $"{block.Item1:x}:{Convert.ToHexString(block.Item2)}"),
            hex.Blocks.Select(block => $"{block.Address:x}:{Convert.ToHexString(block.Data)}"));
    }

    [Theory]
    [InlineData(":0400100010111213A7", "line 1: has the wrong checksum.")]
    [InlineData("0400100010111213A6", "line 1: is not an Intel HEX record (':', then pairs of hex digits).")]
    [InlineData(":0400100010111213", "line 1: has 8 bytes, not the 5 and the 4 of data its length says.")]
    [InlineData(":01001000559A\n:01001000559A", "line 2: loads address 0x00000010 again.")]
    [InlineData(":0100000655A4", "line 1: is a record of type 06, which Intel HEX does not have.")]
    [InlineData(":0100000455A6", "line 1: is a record of type 04 with 1 bytes of data, not 2.")]
    public void A_record_that_is_not_well_formed_is_an_error_with_its_line(string text, string message)
    {
        var error = Assert.Throws<ChipSharpException>(() => IntelHexFile.Parse(text, "x.ihx"));

        Assert.Equal($"'x.ihx' {message}", error.Message);
    }

    [Fact]
    public void An_ihx_is_told_from_an_elf_by_its_first_character()
    {
        Directory.CreateDirectory(_scratch);
        var ihx = Path.Combine(_scratch, "Blink.ihx");
        var elf = Path.Combine(_scratch, "Blink.elf");
        File.WriteAllText(ihx, "\r\n:00000001FF\r\n");
        File.WriteAllBytes(elf, [0x7f, (byte)'E', (byte)'L', (byte)'F']);

        Assert.True(IntelHexFile.IsIntelHex(ihx));
        Assert.False(IntelHexFile.IsIntelHex(elf));
        Assert.False(IntelHexFile.IsIntelHex(Path.Combine(_scratch, "missing.ihx")));
    }

    [Fact]
    public void An_ihx_that_loads_bytes_outside_the_targets_memory_is_not_packaged()
    {
        Directory.CreateDirectory(_scratch);
        var ihx = Path.Combine(_scratch, "Blink.ihx");
        // 4 bytes at 0x7ffe: two below the STM8S103's flash
        File.WriteAllText(ihx, ":047FFE00820080007D\n:00000001FF\n");

        var error = Assert.Throws<ChipSharpException>(() =>
            new IntelHexImageFormat().Package(new LinkedBinary(ihx), SdccPrograms.Stm8s103, new MemoryStream()));

        Assert.StartsWith($"'{ihx}' loads 4 bytes at 0x00007ffe-0x00008001, which is not inside a memory region of target " +
            "'stm8s103-board' (flash 0x00008000-0x00009fff, ram 0x00000000-0x000003ff, ", error.Message);
    }
}
