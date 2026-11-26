using System.Buffers.Binary;
using System.Globalization;
using ChipSharp.Abstractions;

namespace ChipSharp.Image.Uf2;

/// <summary>
/// The "uf2" image format (https://github.com/microsoft/uf2): what the RP2040's boot ROM takes when the board is
/// plugged in with BOOTSEL held and the file is copied to the drive that appears. The bytes the .elf loads
/// (<see cref="ElfFile.Segments"/>, at their load addresses) are cut into 256-byte pages; each page becomes one
/// 512-byte block carrying the target's "uf2FamilyId". Bytes of a page no segment covers are zero.
/// </summary>
public sealed class Uf2ImageFormat : IImageFormat
{
    /// <summary>The first word of every block: "UF2\n".</summary>
    public const uint MagicStart0 = 0x0A324655;

    public const uint MagicStart1 = 0x9E5D5157;

    /// <summary>The last word of every block.</summary>
    public const uint MagicEnd = 0x0AB16F30;

    /// <summary>The flag that says word 7 of the block is the family id, not the file size.</summary>
    public const uint FamilyIdPresent = 0x00002000;

    public const int BlockSize = 512;

    /// <summary>The payload of each block, as the RP2040's boot ROM requires: 256 bytes at a 256-byte boundary.</summary>
    public const int PayloadSize = 256;

    public string Name => "uf2";

    public void Package(LinkedBinary binary, TargetDescription target, Stream output)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(output);

        var familyId = FamilyId(target);
        var elf = ElfFile.Read(binary.Path);
        var pages = Pages(elf, target, binary.Path);
        Write(pages, familyId, output);
    }

    /// <summary>The target's "uf2FamilyId", e.g. 0xe48bff56 for the RP2040.</summary>
    public static uint FamilyId(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var text = target.Setting("uf2FamilyId") ?? throw new ChipSharpException(
            $"Target '{target.Name}': 'uf2FamilyId' is missing; a UF2 image needs the chip family's id, " +
            "e.g. \"0xe48bff56\" for the RP2040.");
        var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new ChipSharpException(
                $"Target '{target.Name}': 'uf2FamilyId' is \"{text}\"; write it in hex, e.g. \"0xe48bff56\".");
    }

    /// <summary>Writes one block per page, in address order, numbered from 0.</summary>
    public static void Write(IReadOnlyList<(uint Address, byte[] Data)> pages, uint familyId, Stream output)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(output);
        var block = new byte[BlockSize];
        for (var i = 0; i < pages.Count; i++)
        {
            var (address, data) = pages[i];
            if (data.Length != PayloadSize || address % PayloadSize != 0)
            {
                throw new ArgumentException(
                    $"Page {i} is {data.Length} bytes at 0x{address:x8}; each must be {PayloadSize} bytes at a " +
                    $"multiple of {PayloadSize}.", nameof(pages));
            }

            Array.Clear(block);
            var words = block.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(words[0..], MagicStart0);
            BinaryPrimitives.WriteUInt32LittleEndian(words[4..], MagicStart1);
            BinaryPrimitives.WriteUInt32LittleEndian(words[8..], FamilyIdPresent);
            BinaryPrimitives.WriteUInt32LittleEndian(words[12..], address);
            BinaryPrimitives.WriteUInt32LittleEndian(words[16..], PayloadSize);
            BinaryPrimitives.WriteUInt32LittleEndian(words[20..], (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(words[24..], (uint)pages.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(words[28..], familyId);
            data.CopyTo(words[32..]);
            BinaryPrimitives.WriteUInt32LittleEndian(words[508..], MagicEnd);
            output.Write(block);
        }
    }

    /// <summary>
    /// The 256-byte pages the segments' bytes fall in, in address order. Every byte must lie in one of the
    /// target's memory regions, and no two segments may load the same byte.
    /// </summary>
    private static List<(uint Address, byte[] Data)> Pages(ElfFile elf, TargetDescription target, string path)
    {
        var pages = new SortedDictionary<uint, (byte[] Data, bool[] Written)>();
        foreach (var segment in elf.Segments.Where(s => s.Data.Length > 0))
        {
            var start = (ulong)segment.PhysicalAddress;
            var end = start + (ulong)segment.Data.Length;
            if (!target.Memory.Values.Any(region => start >= region.Origin && end <= region.End))
            {
                throw new ChipSharpException(
                    $"'{path}' loads {segment.Data.Length} bytes at 0x{start:x8}-0x{end - 1:x8}, which is not inside " +
                    $"a memory region of target '{target.Name}' " +
                    $"({string.Join(", ", target.Memory.Select(m => $"{m.Key} 0x{m.Value.Origin:x8}-0x{m.Value.End - 1:x8}"))}).");
            }

            for (var i = 0; i < segment.Data.Length; i++)
            {
                var address = segment.PhysicalAddress + (uint)i;
                var pageAddress = address & ~(uint)(PayloadSize - 1);
                if (!pages.TryGetValue(pageAddress, out var page))
                {
                    page = (new byte[PayloadSize], new bool[PayloadSize]);
                    pages[pageAddress] = page;
                }

                var at = (int)(address - pageAddress);
                if (page.Written[at])
                {
                    throw new ChipSharpException($"'{path}' loads two segments at the same address, 0x{address:x8}.");
                }

                page.Data[at] = segment.Data[i];
                page.Written[at] = true;
            }
        }

        return pages.Count > 0
            ? [.. pages.Select(p => (p.Key, p.Value.Data))]
            : throw new ChipSharpException($"'{path}' has nothing to load: no segment has any bytes.");
    }
}
