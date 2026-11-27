using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;

namespace ChipSharp.Build;

/// <summary>How much of one memory region of the target a program takes.</summary>
/// <param name="Region">The region's name in the target file, e.g. "flash".</param>
/// <param name="Used">The bytes the program takes, from the start of the region.</param>
/// <param name="Size">The region's size.</param>
public sealed record RegionUse(string Region, ulong Used, ulong Size);

/// <summary>
/// How big a built program is, as <c>chipsharp build</c> reports it (<see cref="Text"/>). For a chip it is read from the
/// linked .elf: the flash the program takes (its code, constants and the starting values of its variables: the bytes it
/// loads into the region "link.code" names) and the RAM (its variables, in the region "link.data" names, at the .elf's
/// addresses less "link.dataOffset": an AVR's .elf has RAM at 0x800000 and up), the heap the
/// linker script keeps after them (<see cref="Heap"/>, the target's "heap" capability) and the stack "link.stack" keeps
/// at the end of the RAM. For a PC it is the size of the program's file.
/// </summary>
/// <param name="FileName">The linked program's file name, e.g. Blink.elf or Square.exe.</param>
/// <param name="FileSize">The size of that file.</param>
/// <param name="Code">The flash the program takes; null for a PC.</param>
/// <param name="Data">The RAM its variables take; null for a PC.</param>
/// <param name="Stack">The RAM kept for the stack, after the variables.</param>
public sealed record MemoryReport(string FileName, ulong FileSize, RegionUse? Code, RegionUse? Data, ulong Stack)
{
    /// <summary>The RAM the linked program keeps for the heap, between its variables and the stack; 0 for none (and on a PC).</summary>
    public ulong Heap { get; init; }

    /// <summary>
    /// "flash 6.2 KB of 2048 KB (0.3%), RAM 1.1 KB + heap 224 KB of 264 KB (stack 8 KB reserved)" for a chip (without
    /// "+ heap" when it has none); "Square.exe: 5.5 KB" for a PC.
    /// </summary>
    public string Text => Code is null || Data is null
        ? $"{FileName}: {MemorySize.Text(FileSize)}"
        : string.Create(CultureInfo.InvariantCulture,
            $"{Of(Code)} ({100.0 * Code.Used / Code.Size:0.0}%), {MemorySize.RegionName(Data.Region)} " +
            $"{MemorySize.Text(Data.Used)}{(Heap > 0 ? $" + heap {MemorySize.Text(Heap)}" : "")} of " +
            $"{MemorySize.Text(Data.Size)}") +
          (Stack > 0 ? $" (stack {MemorySize.Text(Stack)} reserved)" : "");

    public override string ToString() => Text;

    /// <summary>
    /// The report for <paramref name="binary"/>, linked for <paramref name="target"/>: from its .elf when the target's
    /// "link.code" and "link.data" name memory regions of it (a chip), else from the size of its file.
    /// </summary>
    /// <exception cref="ChipSharpException">The program cannot be read, or the target's "link.stack" is not a size.</exception>
    public static MemoryReport Read(LinkedBinary binary, TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(target);

        var fileName = Path.GetFileName(binary.Path);
        ulong fileSize;
        try
        {
            fileSize = (ulong)new FileInfo(binary.Path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ChipSharpException($"Cannot read the program '{binary.Path}': {e.Message}", e);
        }

        if (Region(target, "link.code") is not { } code || Region(target, "link.data") is not { } data)
        {
            return new MemoryReport(fileName, fileSize, null, null, 0);
        }

        var elf = ElfFile.Read(binary.Path);
        var offset = Size(target, "link.dataOffset");
        var loaded = elf.Segments.Where(segment => segment.Data.Length > 0 && Contains(code.Region, segment.PhysicalAddress))
            .Select(segment => (ulong)segment.PhysicalAddress + (ulong)segment.Data.Length);
        // the variables' segments, at their data addresses: an AVR's .elf has them at 0x800000 and up (link.dataOffset)
        var variables = elf.Segments
            .Where(segment => segment.MemorySize > 0 && segment.VirtualAddress >= offset
                && Contains(data.Region, segment.VirtualAddress - offset))
            .Select(segment => segment.VirtualAddress - offset + segment.MemorySize);
        return new MemoryReport(fileName, fileSize,
            new RegionUse(code.Name, Used(loaded, code.Region), code.Region.Size),
            new RegionUse(data.Name, Used(variables, data.Region), data.Region.Size),
            Size(target, "link.stack"))
        {
            Heap = elf.Symbols.TryGetValue("__chipsharp_heap_start", out var heapStart)
                && elf.Symbols.TryGetValue("__chipsharp_heap_end", out var heapEnd) && heapEnd > heapStart
                    ? heapEnd - heapStart
                    : 0,
        };
    }

    /// <summary>"flash 6.2 KB of 2048 KB", "RAM 8 bytes of 264 KB".</summary>
    private static string Of(RegionUse use) =>
        $"{MemorySize.RegionName(use.Region)} {MemorySize.Text(use.Used)} of {MemorySize.Text(use.Size)}";

    /// <summary>The memory region the target's setting (e.g. "link.code") names, or null.</summary>
    private static (string Name, MemoryRegion Region)? Region(TargetDescription target, string setting) =>
        target.Setting(setting) is { } name && target.Memory.TryGetValue(name, out var region) ? (name, region) : null;

    private static bool Contains(MemoryRegion region, ulong address) => address >= region.Origin && address < region.End;

    /// <summary>How far into <paramref name="region"/> the furthest of the <paramref name="ends"/> reaches; 0 for none.</summary>
    private static ulong Used(IEnumerable<ulong> ends, MemoryRegion region) =>
        ends.Select(end => end - region.Origin).DefaultIfEmpty(0UL).Max();

    /// <summary>A size or address the target's "link" settings give, e.g. "link.stack"; 0 when not given.</summary>
    private static ulong Size(TargetDescription target, string setting)
    {
        var text = target.Setting(setting);
        try
        {
            return text is null ? 0 : SizeParser.Parse(text);
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new ChipSharpException($"Target '{target.Name}': '{setting}' is \"{text}\"; write it like 8K, 256 or 0x800000.", e);
        }
    }
}
