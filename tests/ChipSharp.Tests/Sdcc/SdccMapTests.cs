using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.Sdcc;

/// <summary>
/// SDCC's map (<see cref="SdccMap"/>): its areas, which of them are RAM, how far the program reaches in each region; the
/// memory report in words for a Padauk chip; and the message of a program that does not fit.
/// </summary>
public sealed class SdccMapTests
{
    /// <summary>The area lines of the map sdldpdk wrote for a small PFS154 program (each area has a page of its own).</summary>
    private const string PdkMap = """
        ASxxxx Linker V05.50.4-SDLD,  page 1.
        Hexadecimal  [32-Bits]

        Area                                    Addr        Size        Decimal Bytes (Attributes)
        --------------------------------        ----        ----        ------- ----- ------------
        .  .ABS.                            00000000    00000000 =           0. bytes (ABS,CON)

               Value  Global                             Global Defined In Module
               -----  --------------------------------   ------------------------
             00000002  s_DATA
        DATA                                00000002    0000000B =          11. bytes (REL,CON)
        OSEG                                0000000D    00000001 =           1. bytes (REL,OVR)
        SSEG                                0000000E    00000001 =           1. bytes (REL,CON)
        HOME                                00000022    00000002 =           2. bytes (REL,CON)
        HEADER1                             00000000    00000002 =           2. bytes (ABS,CON)
        GSINIT                              00000024    0000002C =          44. bytes (REL,CON)
        GSFINAL                             00000050    00000002 =           2. bytes (REL,CON)
        PREG2                               00000000    00000002 =           2. bytes (ABS,CON)
        HEADER3                             00000000    00000014 =          20. bytes (ABS,CON)
        CODE                                00000052    0000005A =          90. bytes (REL,CON)
        CONST                               000000AC    00000006 =           6. bytes (REL,CON)
        CABS                                00000000    00000000 =           0. bytes (ABS,CON)
        """;

    [Fact]
    public void A_map_lists_every_area_with_its_address_size_and_whether_it_is_at_fixed_addresses()
    {
        var map = SdccMap.Parse(PdkMap);

        Assert.Equal(["DATA", "OSEG", "SSEG", "HOME", "HEADER1", "GSINIT", "GSFINAL", "PREG2", "HEADER3", "CODE", "CONST", "CABS"],
            map.Areas.Select(area => area.Name));
        Assert.Equal(new SdccArea("CODE", 0x52, 0x5a, false), map.Areas.Single(area => area.Name == "CODE"));
        Assert.True(map.Areas.Single(area => area.Name == "HEADER3").Absolute);
        Assert.True(SdccMap.IsData("OSEG") && SdccMap.IsData("INITIALIZED") && SdccMap.IsData("PREG2"));
        Assert.False(SdccMap.IsData("CONST") || SdccMap.IsData("INITIALIZER") || SdccMap.IsData("HOME"));
    }

    [Fact]
    public void The_program_reaches_to_the_end_of_its_last_area_of_code_and_its_variables_to_the_stacks_marker()
    {
        var use = SdccMap.Parse(PdkMap).Use(SdccSettings.Read(SdccPrograms.Pfs154));

        // CONST ends at byte 0xb2: 89 words; the variables end where SSEG (the stack) starts, at 0x0e
        Assert.Equal((0xb2UL, 2UL, 0x0eUL, 16UL), (use.CodeUsed, use.CodeWordSize, use.DataUsed, use.Stack));
        Assert.Equal(("program", "ram"), (use.Code.Name, use.Data.Name));
        Assert.Null(SdccMemoryOverflow.Message(use, "Blink", SdccPrograms.Pfs154));
    }

    [Fact]
    public void A_Padauk_chips_report_counts_words_of_program_memory()
    {
        var report = new MemoryReport("Blink.ihx", 512, new RegionUse("program", 1624, 4096) { WordSize = 2 },
            new RegionUse("ram", 9, 128), 16);

        Assert.Equal("program 812 words of 2048 words (39.6%), RAM 9 bytes of 128 bytes (stack 16 bytes reserved)",
            report.Text);
        Assert.Equal(("1 word", "812 words", "1.6 KB"), (MemorySize.Text(2, 2), MemorySize.Text(1624, 2), MemorySize.Text(1624, 1)));
    }

    [Fact]
    public void A_program_too_big_for_both_regions_says_what_each_needs()
    {
        var target = SdccPrograms.Pfs154;
        var settings = SdccSettings.Read(target);
        var use = new SdccMemoryUse(settings.Code, 2 * 3057, 2, settings.Data, 204, 16);

        Assert.Equal("Big does not fit in target 'pfs154-bare': its code, constants and the starting values of its " +
            "variables need 3057 words of program memory, 1009 words more than the target's 2048 words; its variables " +
            "(204 bytes) and the stack (16 bytes, link.stack) need 220 bytes of RAM, 92 bytes more than the target's " +
            "128 bytes. Make the program smaller, or build it for a chip with more memory.",
            SdccMemoryOverflow.Message(use, "Big", target));
    }
}
