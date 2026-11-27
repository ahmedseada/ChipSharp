using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Image.Hex;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The "hex" image format (<see cref="IntelHexImageFormat"/>): Intel HEX records and their checksums, the extended
/// address records past 64 KB and 1 MB, the start address record, and the file of a whole program, which is what
/// llvm-objcopy -O ihex writes, byte for byte.
/// </summary>
public sealed class IntelHexTests : IDisposable
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
    public void A_record_is_its_length_address_type_data_and_checksum_in_upper_case_hex()
    {
        // "from flash\n" and its 0 at 0x68: 0x0c + 0x00 + 0x68 + 0x00 + the bytes is 0x460; 0x100 - 0x60 = 0xa0
        var text = Hex([(0x68, Encoding.ASCII.GetBytes("from flash\n\0"))]);

        Assert.Equal(":0C00680066726F6D20666C6173680A00A0\r\n:00000001FF\r\n", text);
    }

    [Fact]
    public void Data_is_cut_into_records_of_16_bytes_and_blocks_are_written_in_address_order()
    {
        var text = Hex([(0x200, [0xAA]), (0, [.. Enumerable.Range(0, 20).Select(i => (byte)i)])]);

        Assert.Equal(
            ":10000000000102030405060708090A0B0C0D0E0F78\r\n" +
            ":0400100010111213A6\r\n" +
            ":01020000AA53\r\n" +
            ":00000001FF\r\n", text);
    }

    [Fact]
    public void Past_64_KB_a_record_ends_at_the_boundary_and_an_extended_segment_address_follows()
    {
        var text = Hex([(0xfff8, [.. Enumerable.Repeat((byte)0x11, 16)]), (0x2_0000, [0x22])]);

        Assert.Equal(
            ":08FFF800111111111111111179\r\n" +
            ":020000021000EC\r\n" +   // segment 0x1000: addresses 0x10000-0x1ffff
            ":08000000111111111111111170\r\n" +
            ":020000022000DC\r\n" +
            ":0100000022DD\r\n" +
            ":00000001FF\r\n", text);
    }

    [Fact]
    public void Past_1_MB_an_extended_linear_address_takes_over_from_the_segment_address()
    {
        var text = Hex([(0x1_0000, [0x01]), (0x1000_0000, [0x02]), (0x1000_0100, [0x03])]);

        Assert.Equal(
            ":020000021000EC\r\n" +
            ":0100000001FE\r\n" +
            ":020000020000FC\r\n" +   // the segment address back to 0, then the upper 16 bits 0x1000
            ":020000041000EA\r\n" +
            ":0100000002FD\r\n" +
            ":0101000003FB\r\n" +
            ":00000001FF\r\n", text);
    }

    [Fact]
    public void An_entry_point_is_a_start_segment_address_below_1_MB_and_a_start_linear_address_above()
    {
        Assert.Equal(":0100000001FE\r\n:040000030000007089\r\n:00000001FF\r\n", Hex([(0, [0x01])], entry: 0x70));
        Assert.Equal(":0100000001FE\r\n:0400000510000235B0\r\n:00000001FF\r\n", Hex([(0, [0x01])], entry: 0x1000_0235));
    }

    [Fact]
    public void Every_record_sums_to_zero()
    {
        var bytes = new byte[300];
        new Random(7).NextBytes(bytes);

        var text = Hex([(0xff00, bytes), (0x2000_0000, bytes)], entry: 0x1234);

        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.StartsWith(":", line);
            var record = Convert.FromHexString(line[1..]);
            Assert.Equal(record[0] + 5, record.Length);
            Assert.Equal(0, record.Sum(b => b) & 0xff);
        }
    }

    [AvrFact(NeedsLlvmObjcopy = true)]
    public async Task The_hex_of_the_AVR_test_program_is_what_llvm_objcopy_writes()
    {
        var program = AvrTestProgram.Build(_scratch);

        var expected = await ObjcopyAsync(program.Elf);

        var text = await File.ReadAllTextAsync(program.Hex);
        Assert.Equal(expected, text);
        // .data's starting values ("Hello AVR\n" is an ordinary constant, so it is in .data) are in it, in flash
        var elf = ElfFile.Read(program.Elf);
        var data = Assert.Single(elf.Sections, section => section.Name == ".data");
        Assert.Contains($":0B{data.LoadAddress:X4}0048656C6C6F204156520A00", text);
        // the entry point, the reset handler, which an AVR does not need (it starts at the reset vector, address 0)
        Assert.Equal($":04000003{elf.Entry:X8}", text.Split("\r\n")[^3][..17]);
    }

    [PicoFact]
    public async Task The_hex_of_a_Pico_program_is_what_llvm_objcopy_writes_with_its_extended_linear_addresses()
    {
        var program = PicoBuild.Build(PicoBuild.BlinkSource, "PicoBlink", _scratch);
        var hex = Path.Combine(_scratch, "PicoBlink.hex");
        await using (var output = File.Create(hex))
        {
            new IntelHexImageFormat().Package(new LinkedBinary(program.Elf), PicoBuild.Pico, output);
        }

        var text = await File.ReadAllTextAsync(hex);

        Assert.StartsWith(":020000041000EA\r\n", text); // flash at 0x10000000
        Assert.Equal(await ObjcopyAsync(program.Elf), text);
    }

    [AvrFact]
    public void A_section_outside_the_targets_memory_is_refused()
    {
        var targets = TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("uno-small", """{ "inherits": "uno", "memory": { "flash": { "size": "256" } } }"""));
        var program = AvrTestProgram.Build(_scratch);

        var error = Assert.Throws<ChipSharpException>(() => new IntelHexImageFormat().Package(
            new LinkedBinary(program.Elf), new TargetLoader(targets).Load("uno-small"), new MemoryStream()));

        Assert.Contains("which is not inside a memory region of target 'uno-small' (flash 0x00000000-0x000000ff, " +
            "ram 0x00000100-0x000008ff)", error.Message);
    }

    private static string Hex(IEnumerable<(uint Address, byte[] Data)> blocks, uint entry = 0)
    {
        using var output = new MemoryStream();
        IntelHexImageFormat.Write(blocks, entry, output);
        return Encoding.ASCII.GetString(output.ToArray());
    }

    private async Task<string> ObjcopyAsync(string elf)
    {
        var hex = Path.Combine(_scratch, Path.GetFileNameWithoutExtension(elf) + ".objcopy.hex");
        var result = await Llvm.LlvmTools.RunAsync(PicoTools.LlvmTool("llvm-objcopy"), ["-O", "ihex", elf, hex], _scratch);
        Assert.True(result.ExitCode == 0, result.ToString());
        return await File.ReadAllTextAsync(hex);
    }
}
