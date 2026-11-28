using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Image.Hex;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.Sdcc;

/// <summary>
/// The toolchain "sdcc" building hand-written C (and assembly) for the PFS154 and the STM8S103: SDCC's .ihx, normalized
/// into the "hex" image; the memory report, which matches SDCC's own map; the file with main() linked first; the
/// build cache; and the messages of a program that does not fit or does not compile.
/// </summary>
public sealed class SdccBuildTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [SdccFact]
    public void A_PFS154_program_is_an_ihx_of_14_bit_words_two_bytes_each_with_the_vectors_at_words_0_and_0x10()
    {
        var target = SdccPrograms.Pfs154;

        var binary = SdccPrograms.Build(target, _scratch);

        Assert.Equal(Path.Combine(_scratch, "Blink.ihx"), binary.Path);
        var hex = IntelHexFile.Read(binary.Path);
        var bytes = hex.Blocks.SelectMany(block => block.Data.Select((value, i) => (Address: block.Address + (uint)i, value)))
            .ToDictionary(b => b.Address, b => b.value);
        // whole words, low byte first, each of 14 bits
        Assert.All(hex.Blocks, block => Assert.True(block.Address % 2 == 0 && block.Data.Length % 2 == 0));
        Assert.All(bytes.Where(b => b.Key % 2 == 1), b => Assert.True(b.Value <= 0x3f, $"byte 0x{b.Key:x}: 0x{b.Value:x2}"));
        ushort Word(uint word) => (ushort)(bytes[2 * word] | bytes[2 * word + 1] << 8);
        // the interrupt vector (word 0x10, bytes 0x20 and 0x21): goto the handler, whose byte address the map gives
        var symbols = SdccPrograms.MapSymbols(Path.ChangeExtension(binary.Path, ".map"));
        Assert.Equal(0x3000 | (ushort)(symbols["_interrupt"] / 2), Word(0x10));
        Assert.Equal(0UL, hex.Entry);
    }

    [SdccFact]
    public void The_PFS154_memory_report_counts_words_of_program_memory_and_bytes_of_RAM_as_SDCCs_map_has_them()
    {
        var target = SdccPrograms.Pfs154;
        var binary = SdccPrograms.Build(target, _scratch);
        var symbols = SdccPrograms.MapSymbols(Path.ChangeExtension(binary.Path, ".map"));

        var report = MemoryReport.Read(binary, target);

        // the code ends with the constants (CONST, the last area), the variables where the stack starts (SSEG)
        var code = symbols["s_CONST"] + symbols["l_CONST"];
        Assert.Equal(code, IntelHexFile.Read(binary.Path).Blocks.Max(block => block.Address + (ulong)block.Data.Length));
        Assert.Equal(("program", code, 4096UL, 2UL), (report.Code!.Region, report.Code.Used, report.Code.Size, report.Code.WordSize));
        Assert.Equal(("ram", symbols["s_SSEG"], 128UL), (report.Data!.Region, report.Data.Used, report.Data.Size));
        Assert.Equal(16UL, report.Stack);
        Assert.Equal($"program {code / 2} words of 2048 words ({Percent(code, 4096)}), RAM {symbols["s_SSEG"]} bytes " +
            "of 128 bytes (stack 16 bytes reserved)", report.Text);
    }

    [SdccFact]
    public void An_STM8S103_program_starts_with_its_vector_table_at_0x8000_and_its_report_is_SDCCs_map()
    {
        var target = SdccPrograms.Stm8s103;
        var binary = SdccPrograms.Build(target, _scratch);
        var symbols = SdccPrograms.MapSymbols(Path.ChangeExtension(binary.Path, ".map"));
        var hex = IntelHexFile.Read(binary.Path);
        var flash = hex.Blocks.SelectMany(block => block.Data.Select((value, i) => (Address: block.Address + (uint)i, value)))
            .ToDictionary(b => b.Address, b => b.value);

        var report = MemoryReport.Read(binary, target);

        // reset: int (0x82) and a 24-bit address; IRQ 23 (TIM4's update) at 0x8000 + 4 * (2 + 23), to the handler
        Assert.Equal(0x82, flash[0x8000]);
        Assert.Equal(0x82, flash[0x8064]);
        Assert.Equal(symbols["_tim4_update"], (ulong)(flash[0x8065] << 16 | flash[0x8066] << 8 | flash[0x8067]));
        // the code ends with CODE, the last area; the variables where the stack's marker is (SSEG)
        var code = symbols["s_CODE"] + symbols["l_CODE"] - 0x8000;
        Assert.Equal(0x8000 + code, hex.Blocks.Max(block => block.Address + (ulong)block.Data.Length));
        Assert.Equal(("flash", code, 8192UL, 1UL), (report.Code!.Region, report.Code.Used, report.Code.Size, report.Code.WordSize));
        Assert.Equal(("ram", symbols["s_SSEG"], 1024UL), (report.Data!.Region, report.Data.Used, report.Data.Size));
        Assert.Equal($"flash {MemorySize.Text(code)} of 8 KB ({Percent(code, 8192)}), RAM {symbols["s_SSEG"]} bytes of " +
            "1 KB (stack 256 bytes reserved)", report.Text);
    }

    [SdccFact]
    public void The_file_with_main_is_linked_first_so_the_other_files_starting_values_are_set()
    {
        foreach (var target in new[] { SdccPrograms.Pfs154, SdccPrograms.Stm8s103 })
        {
            var folder = Path.Combine(_scratch, target.Name);

            SdccPrograms.Build(target, folder);

            // blink.c is the last file given, and the first linked
            var link = File.ReadAllLines(Path.Combine(folder, "Blink.lk"));
            Assert.Equal(["blink.c.rel", "steps.c.rel", "answer.s.rel"], link.Where(line => line.EndsWith(".rel", StringComparison.Ordinal)));
        }
    }

    [SdccFact]
    public void A_library_file_is_linked_for_a_symbol_nothing_else_defines_and_a_file_linked_whole_takes_its_place()
    {
        foreach (var target in new[] { SdccPrograms.Pfs154, SdccPrograms.Stm8s103 })
        {
            var folder = Path.Combine(_scratch, target.Name);
            // steps.c in the library, which blink.c calls; a default answer() there too, which answer.s (linked whole)
            // takes the place of; and a file nothing calls
            List<GeneratedFile> files =
            [
                .. SdccPrograms.Code(target).Files.Select(file => file.FileName == "steps.c" ? file with { Library = true } : file),
                new GeneratedFile("default_answer.c", "unsigned char answer(void)\n{\n    return 0;\n}\n") { Library = true },
                new GeneratedFile("unused.c", "unsigned char unused_table[40];\n\nvoid unused(void)\n{\n    unused_table[0] = 1;\n}\n")
                {
                    Library = true,
                },
            ];

            var binary = SdccPrograms.Build(target, folder, new GeneratedCode(files));

            var link = File.ReadAllLines(Path.Combine(folder, "Blink.lk"));
            Assert.Equal(["blink.c.rel", "answer.s.rel"], link.Where(line => line.EndsWith(".rel", StringComparison.Ordinal)));
            Assert.Contains("-l Blink.lib", link);
            var map = File.ReadAllText(Path.ChangeExtension(binary.Path, ".map"));
            Assert.Contains("[ steps.c.rel ]", map);
            Assert.DoesNotContain("default_answer.c.rel", map);
            Assert.DoesNotContain("unused.c.rel", map);
            // steps.c's starting value (offset = 3) is set before main() runs, from the library too
            Assert.Contains("_offset", map);
        }
    }

    [SdccFact]
    public void The_hex_image_of_an_SDCC_program_is_its_ihx_in_address_order_in_records_of_16_bytes()
    {
        var target = SdccPrograms.Stm8s103;
        var binary = SdccPrograms.Build(target, _scratch);
        using var image = new MemoryStream();

        new IntelHexImageFormat().Package(binary, target, image);

        var text = Encoding.ASCII.GetString(image.ToArray());
        var hex = IntelHexFile.Read(binary.Path);
        using var expected = new MemoryStream();
        IntelHexImageFormat.Write(hex.Blocks, 0, expected);
        Assert.Equal(Encoding.ASCII.GetString(expected.ToArray()), text);
        // SDCC's own records are of up to 32 bytes, in the order of its areas; the image's of 16, by address
        Assert.Contains(File.ReadAllLines(binary.Path), line => line.StartsWith(":20", StringComparison.Ordinal));
        Assert.StartsWith(":108000008200", text, StringComparison.Ordinal);
        Assert.All(text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), line => Assert.True(
            int.Parse(line.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) <= 16, line));
        Assert.EndsWith(":00000001FF\r\n", text, StringComparison.Ordinal);
        Assert.Equal(Blocks(hex), Blocks(IntelHexFile.Parse(text, "image")));
    }

    [SdccFact]
    public void The_build_cache_keeps_each_files_rel_and_a_later_build_links_the_same_program_from_it()
    {
        var cache = Path.Combine(_scratch, "cache");
        var target = SdccPrograms.Stm8s103;
        SdccToolchainOptions Cached(params string[] flags) => new() { UseCache = true, CacheFolder = cache, ExtraFlags = flags };

        var compiled = File.ReadAllBytes(SdccPrograms.Build(target, Path.Combine(_scratch, "compiled")).Path);
        Assert.False(Directory.Exists(cache));
        var first = File.ReadAllBytes(SdccPrograms.Build(target, Path.Combine(_scratch, "first"), options: Cached()).Path);
        var kept = Directory.GetFiles(cache).ToHashSet();
        var second = File.ReadAllBytes(SdccPrograms.Build(target, Path.Combine(_scratch, "second"), options: Cached()).Path);

        // one .rel for each .c and .s file, made by the first build and taken by the second
        Assert.Equal(3, kept.Count(path => path.EndsWith(".rel", StringComparison.Ordinal)));
        Assert.Equal(kept, Directory.GetFiles(cache).ToHashSet());
        Assert.Equal(compiled, first);
        Assert.Equal(compiled, second);

        // a flag is another .rel for each C file (the assembler takes none of them); another text only its file's
        SdccPrograms.Build(target, Path.Combine(_scratch, "flag"), options: Cached("-DSTEP=2"));
        var withFlag = Directory.GetFiles(cache).Except(kept).ToList();
        SdccPrograms.Build(target, Path.Combine(_scratch, "text"), SdccPrograms.Code(target, "/* another text */"), Cached());
        var withText = Directory.GetFiles(cache).Except(kept).Except(withFlag).ToList();
        Assert.Equal(2, withFlag.Count);
        Assert.Single(withText);
    }

    [SdccFact]
    public void A_program_too_big_for_the_STM8s_flash_fails_with_what_it_needs_and_what_the_chip_has()
    {
        var target = SdccPrograms.Stm8s103;

        var error = Assert.Throws<ChipSharpException>(() => SdccPrograms.Build(target, _scratch, SdccPrograms.Code(target, """
            const uint8_t big[9000] = { 1, 2, 3 };
            uint8_t read_big(uint16_t i) { return big[i]; }
            """)));

        Assert.Matches(@"^Blink does not fit in target 'stm8s103-board': its code, constants and the starting values of " +
            @"its variables need 9\.\d KB of flash, 1\.\d KB more than the target's 8 KB\. Make the program smaller, or " +
            @"build it for a chip with more memory\.$", error.Message);
    }

    [SdccFact]
    public void A_program_too_big_for_the_STM8s_RAM_fails_counting_the_stack_the_target_keeps()
    {
        var target = SdccPrograms.Stm8s103;

        var error = Assert.Throws<ChipSharpException>(() => SdccPrograms.Build(target, _scratch, SdccPrograms.Code(target, """
            uint8_t buffer[800];
            void fill(uint8_t i) { buffer[i] = i; }
            """)));

        // the program's 12 bytes, the buffer's 800 and the stack's 256: 44 bytes more than 1 KB
        Assert.Equal("Blink does not fit in target 'stm8s103-board': its variables (812 bytes) and the stack (256 bytes, " +
            "link.stack) need 1.0 KB of RAM, 44 bytes more than the target's 1 KB. Make the program smaller, or build " +
            "it for a chip with more memory.", error.Message);
    }

    [SdccFact]
    public void A_program_too_big_for_the_PFS154_says_so_in_words_of_program_memory()
    {
        var target = SdccPrograms.Pfs154;

        var error = Assert.Throws<ChipSharpException>(() => SdccPrograms.Build(target, _scratch, SdccPrograms.Code(target, """
            const uint8_t big[2000] = { 1, 2, 3 };
            uint8_t read_big(uint16_t i) { return big[i]; }
            """)));

        // each constant byte is a word (ret k)
        Assert.Matches(@"^Blink does not fit in target 'pfs154-bare': its code, constants and the starting values of " +
            @"its variables need 2\d{3} words of program memory, \d+ words more than the target's 2048 words\. Make the " +
            @"program smaller, or build it for a chip with more memory\.$", error.Message);
    }

    [SdccFact]
    public void A_program_too_big_for_the_PFS154s_RAM_counts_its_bytes_and_the_stack()
    {
        var target = SdccPrograms.Pfs154;

        var error = Assert.Throws<ChipSharpException>(() => SdccPrograms.Build(target, _scratch, SdccPrograms.Code(target, """
            uint8_t buffer[110];
            void fill(uint8_t i) { buffer[i] = i; }
            """)));

        // past the 128 bytes the PDK14 has at most, SDCC's linker fails first ("RAM value 137 too large (128B max)")
        Assert.Matches(@"^Blink does not fit in target 'pfs154-bare': its variables \(1\d\d bytes\) and the stack " +
            @"\(16 bytes, link\.stack\) need 1\d\d bytes of RAM, \d+ bytes more than the target's 128 bytes\. ", error.Message);
        Assert.Contains("too large (128B max)", error.InnerException!.Message);
    }

    [SdccFact]
    public void A_C_error_is_sdccs_message_with_the_command_and_the_folder()
    {
        var target = SdccPrograms.Stm8s103;

        var error = Assert.Throws<ChipSharpException>(() => SdccPrograms.Build(target, _scratch, SdccPrograms.Code(target,
            "int broken(void) { return 1 +; }")));

        Assert.StartsWith($"sdcc failed (exit code 1) in '{_scratch}':\n", error.Message);
        Assert.Contains(" -mstm8 --opt-code-size --peep-file chipsharp_peephole.def -c blink.c -o blink.c.rel\n", error.Message);
        Assert.Contains("blink.c:", error.Message);
        Assert.Contains("syntax error", error.Message);
    }

    [SdccFact]
    public void A_program_without_main_is_an_error_that_says_why_SDCC_needs_it()
    {
        var target = SdccPrograms.Stm8s103;

        var error = Assert.Throws<ChipSharpException>(() => SdccPrograms.Build(target, _scratch,
            new GeneratedCode([new GeneratedFile("steps.c", SdccPrograms.Steps)])));

        Assert.StartsWith("None of the C files defines main(), where an SDCC program starts", error.Message);
    }

    /// <summary>"39.6%".</summary>
    private static string Percent(ulong used, ulong size) =>
        (100.0 * used / size).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The blocks of an Intel HEX file as text, to compare: address and bytes.</summary>
    private static List<string> Blocks(IntelHexFile hex) =>
        [.. hex.Blocks.Select(block => $"{block.Address:x8}:{Convert.ToHexString(block.Data)}")];
}
