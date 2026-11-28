using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using ChipSharp.Abstractions;
using ChipSharp.Image.Uf2;

namespace ChipSharp.Simulators.Esp32c5;

/// <summary>A piece of an image: bytes the loader puts at an address.</summary>
public sealed record Esp32c5Segment(uint Address, byte[] Bytes);

/// <summary>
/// What runs on a simulated ESP32-C5: the segments to load at their addresses (HP SRAM, LP SRAM, flash through the MMU)
/// and where the CPU starts, as the ROM's loader leaves them, from an ELF file (its PT_LOAD segments at their physical
/// addresses, its entry point, its symbols: ChipSharp.Image.Uf2's reader) or an ESP image (<see cref="FromEspImage"/>:
/// what the ROM loads from flash).
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
            ElfFile elf;
            try
            {
                elf = ElfFile.Parse(bytes, path);
            }
            catch (ChipSharpException e)
            {
                throw new ImageException(e.Message);
            }

            if (elf.Machine != 243)
            {
                throw new ImageException($"'{path}' is not a RISC-V ELF file (e_machine {elf.Machine}).");
            }

            return new Esp32c5Image
            {
                // the bytes in the file, at their physical addresses: .bss is the memory's zeros
                Segments = [.. elf.Segments.Where(segment => segment.Data.Length > 0)
                    .Select(segment => new Esp32c5Segment(segment.PhysicalAddress, segment.Data))],
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
    /// 0x4200_0000 on) are loaded alike. The image format's own reader (ChipSharp.Image.Esp's EspImage.Read, M4's wave
    /// 1) takes this one's place once it is in ChipSharp: this is the hook.
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
