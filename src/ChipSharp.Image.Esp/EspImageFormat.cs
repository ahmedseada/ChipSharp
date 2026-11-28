using System.Globalization;
using System.Security.Cryptography;
using ChipSharp.Abstractions;
using ChipSharp.Image.Uf2;

namespace ChipSharp.Image.Esp;

/// <summary>
/// The "esp" image format: the ESP application image (<see cref="EspImage"/>) an Espressif chip's ROM loads from flash.
/// The ESP32-C5's ROM reads it at flash offset 0x2000, where ESP-IDF's second-stage bootloader would be; ChipSharp's
/// program is that image itself, so every segment (one per ELF program header with file bytes, at its load address, in address order; segments that touch are one, as esptool's elf2image makes them)
/// must lie in the SRAM the program has (the target's "link.code" region: below the ROM's own stack and data), and
/// the entry is the .elf's. The target's "esp" settings give the chip id ("esp.chipId", 23 for the C5, esptool's
/// IMAGE_CHIP_ID), the revisions it runs on ("esp.minRevFull", "esp.maxRevFull") and the SPI flash's mode
/// ("esp.flashMode": dio), size ("esp.flashSize": 8MB) and frequency ("esp.flashFreq": 40m), which esptool
/// keeps when it writes the image. The file is <c>&lt;program&gt;.bin</c> (<see cref="Extension"/>).
/// </summary>
public sealed class EspImageFormat : IImageFormat
{
    public string Name => "esp";

    public string Extension => "bin";

    private static readonly Dictionary<string, byte> Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qio"] = 0, ["qout"] = 1, ["dio"] = 2, ["dout"] = 3,
    };

    private static readonly Dictionary<string, byte> Sizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1MB"] = 0x00, ["2MB"] = 0x10, ["4MB"] = 0x20, ["8MB"] = 0x30, ["16MB"] = 0x40, ["32MB"] = 0x50,
        ["64MB"] = 0x60, ["128MB"] = 0x70,
    };

    /// <summary>The frequencies esptool's esp32c5 target knows (FLASH_FREQUENCY).</summary>
    private static readonly Dictionary<string, byte> Frequencies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["80m"] = 0xF, ["40m"] = 0x0, ["20m"] = 0x2,
    };

    public void Package(LinkedBinary binary, TargetDescription target, Stream output)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(output);

        var elfSha256 = SHA256.HashData(File.ReadAllBytes(binary.Path));
        var image = Make(ElfFile.Read(binary.Path), target, binary.Path, Path.GetFileNameWithoutExtension(binary.Path), elfSha256);
        output.Write(image.Write());
    }

    /// <summary>
    /// The image of <paramref name="elf"/> for <paramref name="target"/>; <paramref name="path"/> names it in errors.
    /// When the program starts with ESP-IDF's application description (<see cref="EspAppDescription"/>, the first 256 bytes
    /// of its first segment: the target's "link.head" section), the image is also an app ESP-IDF's bootloader accepts
    /// (<see cref="EspIdfBootloaderRules"/>): the description gets <paramref name="projectName"/> and the .elf's
    /// <paramref name="elfSha256"/>, the loaded segments must end below the bootloader's own SRAM, and two zero-length
    /// segments at flash-mapped addresses follow (the C5's bootloader insists on two such segments, DROM and IROM; the
    /// program has nothing mapped, and the target's "esp.mappedSegments" "false" leaves them out).
    /// </summary>
    public static EspImage Make(ElfFile elf, TargetDescription target, string path, string? projectName = null, byte[]? elfSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(elf);
        ArgumentNullException.ThrowIfNull(target);

        var chipId = Number(target, "esp.chipId", required: true);
        var flashMode = Choice(target, "esp.flashMode", Modes);
        var size = Choice(target, "esp.flashSize", Sizes);
        var frequency = Choice(target, "esp.flashFreq", Frequencies);
        var region = target.Setting("link.code") is { } name && target.Memory.TryGetValue(name, out var found)
            ? found
            : throw new ChipSharpException(
                $"Target '{target.Name}': 'link.code' must name the memory region the image's segments load into.");

        var segments = new List<EspSegment>();
        foreach (var segment in elf.Segments.Where(s => s.Data.Length > 0))
        {
            var start = (ulong)segment.PhysicalAddress;
            var end = start + (ulong)segment.Data.Length;
            if (start < region.Origin || end > region.End)
            {
                throw new ChipSharpException(
                    $"'{path}' loads {segment.Data.Length} bytes at 0x{start:x8}-0x{end - 1:x8}, which is not inside the " +
                    $"SRAM the {target.Name}'s ROM loads an image into (0x{region.Origin:x8}-0x{region.End - 1:x8}: above " +
                    "that is the ROM's own stack and data while it boots).");
            }

            segments.Add(new EspSegment(segment.PhysicalAddress, segment.Data));
        }

        // as esptool does: in address order, the segments that touch are one (the code and the .data right after it)
        segments = [.. segments.OrderBy(s => s.Address)];
        for (var i = segments.Count - 1; i > 0; i--)
        {
            if ((ulong)segments[i - 1].Address + (ulong)segments[i - 1].Data.Length == segments[i].Address)
            {
                segments[i - 1] = new EspSegment(segments[i - 1].Address, [.. segments[i - 1].Data, .. segments[i].Data]);
                segments.RemoveAt(i);
            }
        }

        if (segments.Count == 0)
        {
            throw new ChipSharpException($"'{path}' has nothing to load: no segment has any bytes.");
        }

        if (segments.Count > EspImage.MaxSegments)
        {
            throw new ChipSharpException(
                $"'{path}' has {segments.Count} loadable segments; the ROM loads at most {EspImage.MaxSegments}.");
        }

        if (EspAppDescription.IsPresent(segments[0].Data))
        {
            segments = IdfApp(segments, target, path, projectName, elfSha256);
        }

        return new EspImage
        {
            FlashMode = flashMode,
            FlashSizeAndFrequency = (byte)(size | frequency),
            Entry = elf.Entry,
            ChipId = (ushort)chipId,
            MinRevisionFull = (ushort)Number(target, "esp.minRevFull", required: false),
            MaxRevisionFull = target.Setting("esp.maxRevFull") is null ? ushort.MaxValue : (ushort)Number(target, "esp.maxRevFull", required: false),
            Segments = segments,
        };
    }

    /// <summary>
    /// The segments of an app for ESP-IDF's bootloader: the description filled in, the end of the copied segments checked
    /// against the bootloader's SRAM, and the two mapped segments the C5's bootloader asserts on (EspIdfBootloaderRules).
    /// </summary>
    private static List<EspSegment> IdfApp(List<EspSegment> segments, TargetDescription target, string path, string? projectName, byte[]? elfSha256)
    {
        var last = segments[^1];
        var end = (ulong)last.Address + (ulong)((last.Data.Length + 3) / 4 * 4);
        if (end > EspIdfBootloaderRules.BootloaderRamStart)
        {
            throw new ChipSharpException(
                $"'{path}' loads up to 0x{end - 1:x8}, but behind ESP-IDF's bootloader (Wokwi, or a devkit with its factory bootloader) " +
                $"the image may only load below 0x{EspIdfBootloaderRules.BootloaderRamStart:x8}, where the bootloader's own SRAM starts " +
                $"(its bootloader.ld: loading over it would overwrite the code that copies the image): {EspIdfBootloaderRules.BootloaderRamStart - segments[0].Address} " +
                "bytes of code, constants and starting values at most. The program is too big for both ways of booting.");
        }

        var result = segments.Select(s => new EspSegment(s.Address, s.Data)).ToList();
        result[0] = new EspSegment(result[0].Address, EspAppDescription.Fill(result[0].Data, projectName, elfSha256));
        if (!string.Equals(target.Setting("esp.mappedSegments"), "false", StringComparison.OrdinalIgnoreCase))
        {
            // each mapped segment's data sits at an offset in its 64 KB flash page that is its address' offset in the MMU
            // page (esp_image_format.c, verify_segment_header), and reaches into the page (a range of 0 bytes asserts)
            while (true)
            {
                var first = EspImage.HeaderSize + EspImage.ExtendedHeaderSize
                    + result.Sum(s => EspImage.SegmentHeaderSize + (s.Data.Length + 3) / 4 * 4) + EspImage.SegmentHeaderSize;
                if (first % EspIdfBootloaderRules.MmuPageSize != 0 && (first + EspImage.SegmentHeaderSize) % EspIdfBootloaderRules.MmuPageSize != 0)
                {
                    result.Add(new EspSegment(EspIdfBootloaderRules.FlashMapStart + (uint)(first % EspIdfBootloaderRules.MmuPageSize), []));
                    result.Add(new EspSegment(
                        EspIdfBootloaderRules.FlashMapStart + EspIdfBootloaderRules.MmuPageSize
                            + (uint)((first + EspImage.SegmentHeaderSize) % EspIdfBootloaderRules.MmuPageSize), []));
                    break;
                }

                // (the one image in 16384 that would put a mapped segment's data at a page's start: 4 more bytes of zeros)
                var tail = result[^1];
                result[^1] = new EspSegment(tail.Address, [.. tail.Data, 0, 0, 0, 0]);
            }
        }

        return result;
    }

    private static uint Number(TargetDescription target, string key, bool required)
    {
        if (target.Setting(key) is not { } text)
        {
            return required
                ? throw new ChipSharpException(
                    $"Target '{target.Name}': '{key}' is missing; an ESP image needs it (the C5's chip id is 23).")
                : 0U;
        }

        var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        var style = digits.Length == text.Length ? NumberStyles.None : NumberStyles.AllowHexSpecifier;
        return uint.TryParse(digits, style, CultureInfo.InvariantCulture, out var value) && value <= ushort.MaxValue
            ? value
            : throw new ChipSharpException($"Target '{target.Name}': '{key}' is \"{text}\"; write a number up to 65535.");
    }

    private static byte Choice(TargetDescription target, string key, Dictionary<string, byte> choices)
    {
        var text = target.Setting(key) ?? throw new ChipSharpException(
            $"Target '{target.Name}': '{key}' is missing; write one of {string.Join(", ", choices.Keys)}.");
        return choices.TryGetValue(text, out var value)
            ? value
            : throw new ChipSharpException(
                $"Target '{target.Name}': '{key}' is \"{text}\"; write one of {string.Join(", ", choices.Keys)}.");
    }
}
