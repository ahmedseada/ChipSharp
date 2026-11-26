using System.Buffers.Binary;
using System.Text;
using ChipSharp.Abstractions;

namespace ChipSharp.Image.Uf2;

/// <summary>
/// A 32-bit little-endian ELF file (what lld writes for ARM and 32-bit RISC-V chips), read for what packaging
/// needs: the bytes to load and where (<see cref="Segments"/>), and for inspection its sections and symbols.
/// </summary>
public sealed class ElfFile
{
    private ElfFile(uint entry, ushort machine, IReadOnlyList<ElfSegment> segments,
        IReadOnlyList<ElfSection> sections, IReadOnlyDictionary<string, uint> symbols)
    {
        Entry = entry;
        Machine = machine;
        Segments = segments;
        Sections = sections;
        Symbols = symbols;
    }

    /// <summary>The ELF machine number: 40 for ARM, 243 for RISC-V.</summary>
    public ushort Machine { get; }

    /// <summary>The address execution starts at (the linker script's ENTRY).</summary>
    public uint Entry { get; }

    /// <summary>The loadable segments (PT_LOAD), in file order.</summary>
    public IReadOnlyList<ElfSegment> Segments { get; }

    /// <summary>The sections with their names, e.g. .text, .data; the empty section 0 is left out.</summary>
    public IReadOnlyList<ElfSection> Sections { get; }

    /// <summary>The symbol table's named symbols and their values (addresses); the first of each name wins.</summary>
    public IReadOnlyDictionary<string, uint> Symbols { get; }

    public static ElfFile Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ChipSharpException($"Cannot read the program '{path}': {e.Message}", e);
        }

        return Parse(bytes, path);
    }

    /// <summary>Reads an ELF file from its bytes; <paramref name="name"/> names it in errors.</summary>
    public static ElfFile Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ChipSharpException Error(string message) => new($"'{name}' {message}");

        if (bytes.Length < 52 || bytes[0] != 0x7f || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F')
        {
            throw Error("is not an ELF file.");
        }

        if (bytes[4] != 1 || bytes[5] != 1)
        {
            throw Error("is not a 32-bit little-endian ELF file, the only kind ChipSharp packages so far.");
        }

        ReadOnlySpan<byte> Slice(uint offset, uint length, string what) =>
            (ulong)offset + length <= (ulong)bytes.Length
                ? bytes.AsSpan((int)offset, (int)length)
                : throw Error($"is cut short: its {what} reaches past the end of the file.");

        ushort U16(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);
        uint U32(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);

        var header = bytes.AsSpan(0, 52);
        var machine = U16(header, 18);
        var entry = U32(header, 24);
        var (programHeaders, sectionHeaders) = (U32(header, 28), U32(header, 32));
        var (programHeaderSize, programHeaderCount) = (U16(header, 42), U16(header, 44));
        var (sectionHeaderSize, sectionHeaderCount, namesIndex) = (U16(header, 46), U16(header, 48), U16(header, 50));

        var segments = new List<ElfSegment>();
        for (var i = 0; i < programHeaderCount; i++)
        {
            var ph = Slice(programHeaders + (uint)(i * programHeaderSize), 32, "program header table");
            if (U32(ph, 0) != 1) // PT_LOAD
            {
                continue;
            }

            var (offset, fileSize) = (U32(ph, 4), U32(ph, 16));
            segments.Add(new ElfSegment(
                VirtualAddress: U32(ph, 8),
                PhysicalAddress: U32(ph, 12),
                Data: Slice(offset, fileSize, $"segment {i}").ToArray(),
                MemorySize: U32(ph, 20)));
        }

        var raw = new List<(uint Name, uint Type, uint Address, uint Offset, uint Size, uint Link)>();
        for (var i = 0; i < sectionHeaderCount; i++)
        {
            var sh = Slice(sectionHeaders + (uint)(i * sectionHeaderSize), 40, "section header table");
            raw.Add((U32(sh, 0), U32(sh, 4), U32(sh, 12), U32(sh, 16), U32(sh, 20), U32(sh, 24)));
        }

        string Text(int table, uint at)
        {
            if (table < 0 || table >= raw.Count)
            {
                return "";
            }

            var strings = Slice(raw[table].Offset, raw[table].Size, "string table");
            if (at >= strings.Length)
            {
                return "";
            }

            var end = strings[(int)at..].IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? strings[(int)at..] : strings.Slice((int)at, end));
        }

        var sections = raw.Skip(1)
            .Select(s => new ElfSection(Text(namesIndex, s.Name), s.Address, s.Size, s.Type == 8 /* SHT_NOBITS */))
            .ToList();

        var symbols = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var table in raw.Where(s => s.Type == 2)) // SHT_SYMTAB
        {
            var entries = Slice(table.Offset, table.Size, "symbol table");
            for (var at = 16; at + 16 <= entries.Length; at += 16)
            {
                var symbolName = Text((int)table.Link, U32(entries, at));
                if (symbolName.Length > 0)
                {
                    symbols.TryAdd(symbolName, U32(entries, at + 4));
                }
            }
        }

        return new ElfFile(entry, machine, segments, sections, symbols);
    }
}

/// <summary>
/// A loadable segment: <see cref="Data"/> is loaded at <see cref="PhysicalAddress"/> (e.g. the starting values of
/// .data, in flash) and used at <see cref="VirtualAddress"/> (e.g. in RAM); the rest of <see cref="MemorySize"/>
/// starts at zero.
/// </summary>
public sealed record ElfSegment(uint VirtualAddress, uint PhysicalAddress, byte[] Data, uint MemorySize);

/// <summary>A section: its name, address and size. <see cref="NoBits"/> sections (.bss) take no room in the file.</summary>
public sealed record ElfSection(string Name, uint Address, uint Size, bool NoBits);
