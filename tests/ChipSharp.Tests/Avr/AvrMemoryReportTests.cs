using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Image.Uf2;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// How much of the Uno's 32 KB of flash and 2 KB of RAM a program takes (<c>MemoryReport</c>, from the .elf, whose RAM
/// addresses carry the AVR's 0x800000), and what a build says when the program does not fit.
/// </summary>
public sealed class AvrMemoryReportTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [AvrFact]
    public void The_report_is_the_flash_up_to_the_starting_values_of_data_and_the_RAM_of_data_and_bss()
    {
        var program = AvrTestProgram.Build(_scratch);
        var elf = ElfFile.Read(program.Elf);
        ElfSection Section(string name) => Assert.Single(elf.Sections, section => section.Name == name);

        var report = program.Memory;

        // flash: the vectors, the constants in flash, the code, then .data's starting values, one after another
        var flash = new[] { ".vectors", ".progmem", ".text", ".data" }.Sum(name => (long)Section(name).Size);
        Assert.Equal(flash, Section(".data").LoadAddress + Section(".data").Size);
        Assert.Equal(("flash", (ulong)flash, (32UL << 10) - 512), (report.Code!.Region, report.Code.Used, report.Code.Size));
        // RAM, from its first data address (0x100): .data (the greeting) and .bss (the count)
        Assert.Equal(("ram", 12UL, 2UL << 10), (report.Data!.Region, report.Data.Used, report.Data.Size));
        Assert.Equal(Section(".data").Size + Section(".bss").Size, report.Data.Used);
        Assert.Equal((512UL, 0UL), (report.Stack, report.Heap));
        Assert.Equal(string.Create(CultureInfo.InvariantCulture,
            $"flash {flash} bytes of 31.5 KB ({100.0 * flash / ((32 << 10) - 512):0.0}%), RAM 12 bytes of 2 KB (stack 512 bytes reserved)"),
            report.Text);
    }

    [AvrFact]
    public void A_program_too_big_for_the_flash_fails_with_what_it_needs_and_what_the_Uno_has()
    {
        var error = Assert.Throws<ChipSharpException>(() => AvrTestProgram.Build(_scratch, extra: """
            const __flash uint8_t big[33000] = { 1, 2, 3 };
            void test_extra(void) { *(volatile uint8_t *)0xC6 = big[*(volatile uint8_t *)0xC6]; }
            """));

        Assert.Matches("^Blink does not fit in target 'uno': its code, constants and the starting values of its variables " +
            @"need 32\.\d KB of flash, (\d+ bytes|\d+(\.\d)? KB) more than the target's 31\.5 KB\. Make the program smaller, or build it for a " +
            @"chip with more memory\.$", error.Message);
    }

    [AvrFact]
    public void A_program_too_big_for_the_RAM_fails_counting_the_stack_the_Uno_keeps()
    {
        var error = Assert.Throws<ChipSharpException>(() => AvrTestProgram.Build(_scratch, extra: """
            static volatile uint8_t buffer[1600];
            void test_extra(void) { buffer[*(volatile uint8_t *)0xC6] = 1; }
            """));

        // the variables have the 1.5 KB the stack leaves: 1600 bytes, the greeting and the count need 76 bytes more
        Assert.Equal("Blink does not fit in target 'uno': its variables (1.6 KB) and the stack (512 bytes, link.stack) " +
            "need 2.1 KB of RAM, 76 bytes more than the target's 2 KB. Make the program smaller, or build it for a chip " +
            "with more memory.", error.Message);
    }
}
