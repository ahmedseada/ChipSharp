using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace ChipSharp.Simulators.Esp32c5;

/// <summary>A piece of an image: bytes the loader puts at an address.</summary>
public sealed record Esp32c5Segment(uint Address, byte[] Bytes);

/// <summary>
/// What runs on a simulated ESP32-C5: the segments to load at their addresses (HP SRAM, LP SRAM, flash through the MMU)
/// and where the CPU starts, as the ROM's loader leaves them, from an ELF file (its PT_LOAD segments at their physical
/// addresses, its entry point, its symbols) or an ESP image (<see cref="FromEspImage"/>: what the ROM loads from flash).
/// </summary>
public sealed class Esp32c5Image
{
    /// <summary>The ESP image's chip ID for the ESP32-C5 (esptool's IMAGE_CHIP_ID).</summary>
    public const int EspChipId = 23;

    public required IReadOnlyList<Esp32c5Segment> Segments { get; init; }

    /// <summary>The address the CPU starts at.</summary>
    public required uint Entry { get; init; }

    /// <summary>The ELF's symbols (functions, variables) by name; none for an ESP image.</summary>
    public IReadOnlyDictionary<string, uint> Symbols { get; init; } = new Dictionary<string, uint>();

    /// <summary>"ELF" or "ESP image".</summary>
    public required string Format { get; init; }

    /// <summary>Reads <paramref name="path"/>: an ELF file, or an ESP image (it starts with 0xE9).</summary>
    public static Esp32c5Image Load(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ImageException($"cannot read '{path}': {e.Message}");
        }

        if (bytes.Length >= 4 && bytes[0] == 0x7F && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F')
        {
            var elf = ElfFile.Read(bytes, path, machine: 243, "a RISC-V");
            return new Esp32c5Image
            {
                Segments = [.. elf.Segments.Select(segment => new Esp32c5Segment(segment.Address, segment.Bytes))],
                Entry = elf.Entry,
                Symbols = elf.Symbols,
                Format = "ELF",
            };
        }

        if (bytes.Length > 0 && bytes[0] == 0xE9)
        {
            return FromEspImage(bytes, path);
        }

        throw new ImageException($"'{path}' is neither an ELF file nor an ESP image (0xE9).");
    }

    /// <summary>
    /// An ESP image, as esptool documents the format and the ROM loads it from flash: the header (0xE9, the number of
    /// segments, the flash's mode, size and speed, the entry point), the extended header (the chip ID, the ESP32-C5's
    /// 23; whether a SHA-256 is appended), the segments (each its load address, length and bytes), the checksum (0xEF
    /// XORed with every byte of the segments, the last byte of the padding to 16 bytes) and the SHA-256 when appended;
    /// both are checked, as the ROM does. The segments the ROM copies into SRAM and those it maps through the MMU (at
    /// 0x4200_0000 on) are loaded alike.
    /// </summary>
    public static Esp32c5Image FromEspImage(byte[] bytes, string path)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 24)
        {
            throw new ImageException($"'{path}' is too short for an ESP image's header (24 bytes).");
        }

        var chip = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12));
        if (chip != EspChipId)
        {
            throw new ImageException(string.Create(CultureInfo.InvariantCulture,
                $"'{path}' is an ESP image for chip ID {chip}, not the ESP32-C5's ({EspChipId})."));
        }

        var count = bytes[1];
        var entry = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var at = 24;
        byte checksum = 0xEF;
        var segments = new List<Esp32c5Segment>();
        for (var s = 0; s < count; s++)
        {
            if (at + 8 > bytes.Length)
            {
                throw new ImageException($"'{path}': segment {s} of {count} is cut short.");
            }

            var address = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            if (length < 0 || at + 8 + length > bytes.Length)
            {
                throw new ImageException($"'{path}': segment {s} of {count} is cut short.");
            }

            var data = bytes.AsSpan(at + 8, length).ToArray();
            foreach (var b in data)
            {
                checksum ^= b;
            }

            segments.Add(new Esp32c5Segment(address, data));
            at += 8 + length;
        }

        var padded = (at + 1 + 15) & ~15;
        if (padded > bytes.Length || bytes[padded - 1] != checksum)
        {
            throw new ImageException(string.Create(CultureInfo.InvariantCulture,
                $"'{path}': the ESP image's checksum is wrong (0x{checksum:x2} expected), as the ROM would find."));
        }

        if (bytes[23] == 1)
        {
            if (padded + 32 > bytes.Length || !SHA256.HashData(bytes.AsSpan(0, padded)).AsSpan().SequenceEqual(bytes.AsSpan(padded, 32)))
            {
                throw new ImageException($"'{path}': the ESP image's SHA-256 is wrong, as the ROM would find.");
            }
        }

        return new Esp32c5Image { Segments = segments, Entry = entry, Format = "ESP image" };
    }
}

/// <summary>
/// An ELF32 executable (little-endian), as ld.lld writes one: its PT_LOAD segments (the bytes in the file, at their
/// physical addresses), its entry point and its symbols (.symtab).
/// </summary>
public sealed class ElfFile
{
    private ElfFile(IReadOnlyList<Esp32c5Segment> segments, uint entry, IReadOnlyDictionary<string, uint> symbols)
    {
        Segments = segments;
        Entry = entry;
        Symbols = symbols;
    }

    public IReadOnlyList<Esp32c5Segment> Segments { get; }

    public uint Entry { get; }

    public IReadOnlyDictionary<string, uint> Symbols { get; }

    /// <summary>Reads <paramref name="bytes"/>, which must be for <paramref name="machine"/> (e_machine; 243 RISC-V).</summary>
    public static ElfFile Read(byte[] bytes, string path, int machine, string what)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 52 || bytes[4] != 1 || bytes[5] != 1)
        {
            throw new ImageException($"'{path}' is not a 32-bit little-endian ELF file.");
        }

        var span = bytes.AsSpan();
        if (BinaryPrimitives.ReadUInt16LittleEndian(span[18..]) != machine)
        {
            throw new ImageException($"'{path}' is not {what} ELF file (e_machine {BinaryPrimitives.ReadUInt16LittleEndian(span[18..])}).");
        }

        try
        {
            var entry = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
            var phoff = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[28..]);
            var shoff = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[32..]);
            var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(span[42..]);
            var phnum = BinaryPrimitives.ReadUInt16LittleEndian(span[44..]);
            var shentsize = BinaryPrimitives.ReadUInt16LittleEndian(span[46..]);
            var shnum = BinaryPrimitives.ReadUInt16LittleEndian(span[48..]);
            var segments = new List<Esp32c5Segment>();
            for (var p = 0; p < phnum; p++)
            {
                var header = span[(phoff + p * phentsize)..];
                var fileSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 1 || fileSize == 0)
                {
                    continue; // PT_LOAD with bytes in the file only: .bss is the memory's zeros
                }

                var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
                var physical = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
                segments.Add(new Esp32c5Segment(physical, span.Slice(offset, fileSize).ToArray()));
            }

            var symbols = new Dictionary<string, uint>(StringComparer.Ordinal);
            for (var s = 0; s < shnum && shoff > 0; s++)
            {
                var section = span[(shoff + s * shentsize)..];
                if (BinaryPrimitives.ReadUInt32LittleEndian(section[4..]) != 2)
                {
                    continue; // SHT_SYMTAB only
                }

                var table = (int)BinaryPrimitives.ReadUInt32LittleEndian(section[16..]);
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(section[20..]);
                var strings = span[(shoff + (int)BinaryPrimitives.ReadUInt32LittleEndian(section[24..]) * shentsize)..];
                var stringsOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(strings[16..]);
                for (var at = table + 16; at + 16 <= table + size; at += 16)
                {
                    var nameAt = stringsOffset + (int)BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
                    var end = span[nameAt..].IndexOf((byte)0);
                    var name = System.Text.Encoding.ASCII.GetString(span.Slice(nameAt, end));
                    if (name.Length > 0 && span[at + 14] != 0)
                    {
                        symbols.TryAdd(name, BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 4)..]));
                    }
                }
            }

            return new ElfFile(segments, entry, symbols);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ImageException($"'{path}' is cut short: an ELF header points past its end.");
        }
    }
}
