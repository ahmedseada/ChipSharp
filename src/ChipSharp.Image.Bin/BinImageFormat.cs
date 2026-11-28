using ChipSharp.Abstractions;
using ChipSharp.Image.Hex;
using ChipSharp.Image.Uf2;

namespace ChipSharp.Image.Bin;

/// <summary>
/// The "bin" image format: the raw bytes of the chip's flash, as a programmer writes them from the flash's first address
/// (the CH32V003's 0x08000000, which wlink and minichlink write through a WCH-LinkE, as WCH-LinkUtility does). The flash
/// is the memory region the target's "link.code" names (else its region "flash"); the file starts at that region's origin
/// and ends at the last byte the program loads there. It holds every allocated section with bytes in the file (code,
/// constants, the starting values of .data) at its load address (.data's in flash, not where it runs in RAM); the bytes
/// between sections, and before the first when it starts later, are 0xFF, as erased flash reads. For a program that starts
/// at the flash's origin that is what <c>llvm-objcopy -O binary --gap-fill 0xff</c> writes. A program linked as Intel HEX
/// already (SDCC's .ihx, <see cref="IntelHexFile"/>) is written the same way, from its bytes. Every byte must be in the
/// flash: a program that loads anything elsewhere (RAM, another region) is refused, and so is one bigger than the flash,
/// saying by how much.
/// </summary>
public sealed class BinImageFormat : IImageFormat
{
    /// <summary>What erased flash reads, and what the gaps of the image are filled with.</summary>
    public const byte Erased = 0xFF;

    public string Name => "bin";

    public void Package(LinkedBinary binary, TargetDescription target, Stream output)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(output);

        var blocks = IntelHexFile.IsIntelHex(binary.Path)
            ? [.. IntelHexFile.Read(binary.Path).Blocks.Select(block => ((string?)null, block.Address, block.Data))]
            : Sections(ElfFile.Read(binary.Path));
        output.Write(Make(blocks, target, binary.Path));
    }

    /// <summary>The image of <paramref name="elf"/> for <paramref name="target"/>; <paramref name="path"/> names it in errors.</summary>
    public static byte[] Make(ElfFile elf, TargetDescription target, string path)
    {
        ArgumentNullException.ThrowIfNull(elf);

        return Make(Sections(elf), target, path);
    }

    /// <summary>
    /// The flash the image is of: the memory region the target's "link.code" names, else its region "flash"; else an
    /// error that says what to give.
    /// </summary>
    public static (string Name, MemoryRegion Region) Flash(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Setting("link.code") is { } code && target.Memory.TryGetValue(code, out var named))
        {
            return (code, named);
        }

        return target.Memory.TryGetValue("flash", out var flash)
            ? ("flash", flash)
            : throw new ChipSharpException(
                $"Target '{target.Name}' has no flash to make a .bin of: its \"link.code\" must name the memory region the " +
                "program is written into (e.g. \"flash\").");
    }

    /// <summary>
    /// The image of <paramref name="blocks"/> (a section's name, or null for bytes of an Intel HEX file; the address its
    /// bytes load at; the bytes) for <paramref name="target"/>: from the flash's origin to the last byte loaded, the gaps
    /// 0xFF.
    /// </summary>
    internal static byte[] Make(IReadOnlyList<(string? Section, uint Address, byte[] Data)> blocks, TargetDescription target,
        string path)
    {
        var (name, flash) = Flash(target);
        if (blocks.Count == 0)
        {
            throw new ChipSharpException($"'{path}' has nothing to load: no section has any bytes.");
        }

        ulong end = flash.Origin;
        foreach (var (section, address, data) in blocks.OrderBy(block => block.Address))
        {
            var (start, last) = ((ulong)address, (ulong)address + (ulong)data.Length);
            var what = section is null ? $"{data.Length} bytes" : $"section {section} ({data.Length} bytes)";
            // below the flash, or in another region (RAM): not the flash's; past its end, where no region is, too big
            var elsewhere = start >= flash.End && target.Memory.Any(other => other.Key != name
                && start >= other.Value.Origin && start < other.Value.End);
            if (start < flash.Origin || elsewhere)
            {
                throw new ChipSharpException(
                    $"'{path}' loads {what} at 0x{start:x8}-0x{last - 1:x8}, which is not in the {MemorySize.RegionName(name)} " +
                    $"of target '{target.Name}' (0x{flash.Origin:x8}-0x{flash.End - 1:x8}): a .bin holds the bytes of the " +
                    $"{MemorySize.RegionName(name)} alone.");
            }

            if (start < end)
            {
                throw new ChipSharpException(
                    $"'{path}' loads two sections at the same address: {section ?? "a block"} starts at 0x{start:x8}, " +
                    "before the section ahead of it ends.");
            }

            end = last;
        }

        if (end > flash.End)
        {
            throw new ChipSharpException(
                $"'{path}' does not fit in the {MemorySize.RegionName(name)} of target '{target.Name}': its image is " +
                $"{MemorySize.Text(end - flash.Origin)} (0x{flash.Origin:x8}-0x{end - 1:x8}), {MemorySize.Text(end - flash.End)} " +
                $"more than the {MemorySize.Text(flash.Size)} of {MemorySize.RegionName(name)} there is " +
                $"(0x{flash.Origin:x8}-0x{flash.End - 1:x8}). Make the program smaller.");
        }

        var image = new byte[end - flash.Origin];
        Array.Fill(image, Erased);
        foreach (var (_, address, data) in blocks)
        {
            data.CopyTo(image, (long)(address - flash.Origin));
        }

        return image;
    }

    /// <summary>The allocated sections with bytes in the file, at their load addresses.</summary>
    private static List<(string? Section, uint Address, byte[] Data)> Sections(ElfFile elf) =>
    [
        .. elf.Sections.Where(section => section.IsAllocated && !section.NoBits && section.Data.Length > 0)
            .Select(section => ((string?)section.Name, section.LoadAddress, section.Data)),
    ];
}
