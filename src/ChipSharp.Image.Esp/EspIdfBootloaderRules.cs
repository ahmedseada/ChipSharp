using ChipSharp.Abstractions;

namespace ChipSharp.Image.Esp;

/// <summary>
/// What ESP-IDF's second-stage bootloader (release/v5.5) checks of an app image on the ESP32-C5 before it starts it, each
/// rule with the source it comes from, so an image that passes <see cref="Check"/> boots behind it (Wokwi's flow, or a
/// devkit still flashed with its factory bootloader: the app at flash offset 0x10000). The ROM, which loads ChipSharp's
/// image from flash offset 0x2000 itself, checks none of these; they only add to what the image has.
/// <list type="bullet">
/// <item>esp_image_format.c, verify_image_header: magic 0xE9; bootloader_common_loader.c, check_chip_validity: the chip id
/// is the C5's (23, CONFIG_IDF_FIRMWARE_CHIP_ID); check_chip_revision_validity: the chip's revision is at least the
/// image's minimum, and at most its maximum unless that is unset (IS_FIELD_SET: not 0 and not 65535); at most 16
/// segments.</item>
/// <item>verify_segment_header: every length a multiple of 4, below 16 MB; segment 0 at least 256 bytes (the
/// <see cref="EspAppDescription"/>), starting with its magic word 0xABCD5432 ("Failed to fetch app description
/// header!"); a segment at an address mapped from flash (0x42000000-0x43ffffff) has its data at the same offset in an MMU
/// page (64 KB by default, the description's mmu_page_size) as its load address.</item>
/// <item>process_segment_data: bootloader_common_check_efuse_blk_validity on segment 0's description: the chip's eFuse
/// block revision at least min_efuse_blk_rev_full and at most max_efuse_blk_rev_full, each ignored when unset.</item>
/// <item>process_segment, verify_load_addresses: a segment the bootloader copies (not mapped, load address from
/// 0x10000000) lies in SRAM (0x40800000-0x4085ffff) or LP memory (0x50000000-0x50003fff), and not over the bootloader's
/// own SRAM while it runs: its code (0x4084bba0 on), its loader, its data and the stack up to SOC_ROM_STACK_START
/// 0x4085e5a0 (components/bootloader/subproject/main/ld/esp32c5/bootloader.ld: bootloader_iram_seg_start 0x4084bba0,
/// bootloader_iram_loader_seg_start 0x4084e5a0, bootloader_dram_seg_start 0x408555a0, end 0x4085a5a0).</item>
/// <item>process_checksum, process_appended_hash_and_sig, verify_simple_hash: the checksum byte, the appended SHA-256 and
/// the image's length within its partition.</item>
/// <item>bootloader_utility.c, unpack_load_app (the C5 has SOC_MMU_DI_VADDR_SHARED): exactly two segments mapped from
/// flash (<c>assert(rom_index == 2)</c>), each reaching into its MMU page (cache_ll_l1_get_bus asserts the mapped range is
/// not empty), and the entry point (called as a function) in a copied segment.</item>
/// </list>
/// </summary>
public static class EspIdfBootloaderRules
{
    /// <summary>The ESP32-C5's chip id in an image header: esptool's IMAGE_CHIP_ID, CONFIG_IDF_FIRMWARE_CHIP_ID.</summary>
    public const int ChipId = 23;

    /// <summary>The 64 KB MMU page ESP-IDF maps flash in on the C5 (SPI_FLASH_MMU_PAGE_SIZE, mmu_page_size 16).</summary>
    public const uint MmuPageSize = 0x10000;

    /// <summary>soc.h: SOC_IROM_LOW and SOC_IROM_HIGH (SOC_DROM_* the same: the instruction and data addresses are shared).</summary>
    public const uint FlashMapStart = 0x42000000, FlashMapEnd = 0x44000000;

    /// <summary>soc.h: SOC_IRAM_LOW/SOC_DRAM_LOW and _HIGH: the 384 KB of HP SRAM.</summary>
    public const uint SramStart = 0x40800000, SramEnd = 0x40860000;

    /// <summary>soc.h: SOC_RTC_IRAM_LOW and _HIGH (16 KB of LP memory).</summary>
    public const uint LpStart = 0x50000000, LpEnd = 0x50004000;

    /// <summary>
    /// Where the bootloader's own SRAM begins while it loads an app (bootloader.ld: bootloader_iram_seg_start, the lowest
    /// of its regions) and ends (soc.h SOC_ROM_STACK_START, where the stack the check protects ends): an app segment
    /// that reaches into it would overwrite the bootloader as it copies.
    /// </summary>
    public const uint BootloaderRamStart = 0x4084bba0, BootloaderRamEnd = 0x4085e5a0;

    /// <summary>The default partition table's app partition: factory at 0x10000, 1 MB.</summary>
    public const uint DefaultAppOffset = 0x10000, DefaultAppSize = 0x100000;

    public const int MaxSegments = 16;

    /// <summary>esp_image_format.h: ESP_IMAGE_MAX_FLASH_ADDR_SIZE, a segment is shorter than 16 MB.</summary>
    public const uint MaxSegmentSize = 0x1000000;

    /// <summary>Whether a segment's load address is one the bootloader maps from flash (should_map) and does not copy.</summary>
    public static bool IsMapped(uint address) => address is >= FlashMapStart and < FlashMapEnd;

    /// <summary>ESP-IDF's IS_FIELD_SET: a revision field that is neither 0 nor 65535 says something.</summary>
    public static bool IsFieldSet(uint revision) => revision is not (0 or 65535);

    /// <summary>
    /// Whether the bootloader accepts the image on a chip whose revision is <paramref name="revision"/> (major * 100 +
    /// minor) and whose eFuse block revision is <paramref name="blockRevision"/>; the violations otherwise.
    /// </summary>
    public static IReadOnlyList<string> CheckRevisions(EspImage image, EspAppDescription description, uint revision, uint blockRevision)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(description);
        var found = new List<string>();
        if (revision < image.MinRevisionFull)
        {
            found.Add($"chip revision check failed: the image needs chip revision >= v{image.MinRevisionFull / 100}.{image.MinRevisionFull % 100}.");
        }

        if (IsFieldSet(image.MaxRevisionFull) && revision > image.MaxRevisionFull)
        {
            found.Add($"chip revision check failed: the image needs chip revision <= v{image.MaxRevisionFull / 100}.{image.MaxRevisionFull % 100}.");
        }

        var min = description.MinEfuseBlockRevision;
        var max = description.MaxEfuseBlockRevision;
        if (IsFieldSet(min) && blockRevision < min)
        {
            found.Add($"Image requires efuse blk rev >= v{min / 100}.{min % 100}, but chip is v{blockRevision / 100}.{blockRevision % 100}");
        }

        if (IsFieldSet(max) && blockRevision > max)
        {
            found.Add($"Image requires efuse blk rev <= v{max / 100}.{max % 100}, but chip is v{blockRevision / 100}.{blockRevision % 100}");
        }

        return found;
    }

    /// <summary>
    /// Every rule above, for the image in <paramref name="bytes"/> placed at flash offset <paramref name="offset"/> in a
    /// partition of <paramref name="partitionSize"/> bytes. The list is empty when the bootloader accepts the image
    /// (on every chip revision and eFuse block revision), else it says what it would refuse and why.
    /// </summary>
    public static IReadOnlyList<string> Check(ReadOnlySpan<byte> bytes, uint offset = DefaultAppOffset, uint partitionSize = DefaultAppSize)
    {
        var found = new List<string>();
        EspImage image;
        try
        {
            image = EspImage.Read(bytes); // magic 0xE9, the checksum and the appended SHA-256
        }
        catch (ChipSharpException e)
        {
            return [e.Message];
        }

        if (!image.DigestAppended)
        {
            found.Add("the image has no appended SHA-256 (hash_appended 0): the bootloader would not verify it.");
        }

        if (bytes.Length > partitionSize)
        {
            found.Add($"Image length {bytes.Length} doesn't fit in partition length {partitionSize}");
        }

        if (image.Segments.Count > MaxSegments)
        {
            found.Add($"segment count {image.Segments.Count} exceeds max {MaxSegments}");
        }

        if (image.ChipId != ChipId)
        {
            found.Add($"mismatch chip ID, expected {ChipId}, found {image.ChipId}");
        }

        var segments = image.Segments;
        if (segments.Count == 0 || !EspAppDescription.IsPresent(segments[0].Data))
        {
            found.Add(segments.Count > 0 && segments[0].Data.Length < EspAppDescription.Size
                ? $"Segment 0: length 0x{segments[0].Data.Length:x} is too short for app description"
                : "Failed to fetch app description header! (segment 0 must start with the magic word 0xABCD5432)");
        }
        else
        {
            var description = EspAppDescription.Read(segments[0].Data);
            // every revision a chip can have: the image must run on all of them (the bootloader checks the chip's own)
            foreach (var revision in new uint[] { 0, 1, 100, 101, 102, 200, 65534 })
            {
                foreach (var message in CheckRevisions(image, description, revision, revision))
                {
                    if (!found.Contains(message))
                    {
                        found.Add(message);
                    }
                }
            }

            if (description.MmuPageSizeLog2 != 16)
            {
                found.Add($"MMU page size mismatch: the description says 2^{description.MmuPageSizeLog2}, the bootloader is built for 0x{MmuPageSize:x}");
            }
        }

        // the segments' data in flash: after the 24 bytes of headers, each after its 8-byte header
        var dataOffset = offset + (uint)(EspImage.HeaderSize + EspImage.ExtendedHeaderSize);
        var mapped = 0;
        var entryCopied = false;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            dataOffset += EspImage.SegmentHeaderSize;
            var start = segment.Address;
            var length = (uint)segment.Data.Length;
            var end = (ulong)start + length;
            if (length % 4 != 0 || length >= MaxSegmentSize)
            {
                found.Add($"invalid segment length 0x{length:x}");
            }

            if (IsMapped(start))
            {
                mapped++;
                if (dataOffset % MmuPageSize != start % MmuPageSize)
                {
                    found.Add($"Segment {i} load address 0x{start:x8}, doesn't match data 0x{dataOffset:x8}");
                }

                if (start % MmuPageSize + length == 0)
                {
                    found.Add($"Segment {i}: a mapped range of 0 bytes at the start of its MMU page (cache_ll_l1_get_bus asserts)");
                }

                if (end > FlashMapEnd)
                {
                    found.Add($"Segment {i} 0x{start:x8}-0x{end:x8} invalid: bad load address range");
                }
            }
            else if (start >= 0x10000000 && length > 0)
            {
                var inSram = start >= SramStart && end <= SramEnd;
                var inLp = start >= LpStart && end <= LpEnd;
                if (!inSram && !inLp)
                {
                    found.Add($"Segment {i} 0x{start:x8}-0x{end:x8} invalid: bad load address range");
                }
                else if (inSram && start < BootloaderRamEnd && end > BootloaderRamStart)
                {
                    found.Add($"Segment {i} 0x{start:x8}-0x{end:x8} invalid: overlaps the bootloader's SRAM " +
                        $"(0x{BootloaderRamStart:x8}-0x{BootloaderRamEnd:x8}: its code, loader, data and stack)");
                }

                entryCopied |= inSram && image.Entry >= start && image.Entry < end;
            }

            dataOffset += length;
        }

        if (mapped != 2)
        {
            found.Add($"assert failed: unpack_load_app (rom_index == 2): the image has {mapped} segments mapped from flash " +
                $"(0x{FlashMapStart:x8}-0x{FlashMapEnd - 1:x8}), the C5's bootloader needs exactly 2");
        }

        if (!entryCopied)
        {
            found.Add($"the entry point 0x{image.Entry:x8} is not in a segment the bootloader copies into SRAM");
        }

        return found;
    }
}
