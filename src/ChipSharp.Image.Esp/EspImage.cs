using System.Buffers.Binary;
using System.Security.Cryptography;
using ChipSharp.Abstractions;

namespace ChipSharp.Image.Esp;

/// <summary>One segment of an ESP image: its bytes, loaded at <paramref name="Address"/>.</summary>
public sealed record EspSegment(uint Address, byte[] Data);

/// <summary>
/// An ESP application image, the file the ROM bootloader of an Espressif chip loads from flash (esptool's
/// "firmware image format"; esptool/bin_image.py, ESP32FirmwareImage, and ESP-IDF's esp_image_format.h):
/// <code>
/// 0   0xE9 | segment count | SPI flash mode | flash size (high nibble) and frequency | entry point (4 bytes)
/// 8   the extended header, 16 bytes: WP pin (0xEE: none) | drive strengths (3 bytes) | chip id (2) |
///     minimum revision (1, deprecated) | minimum revision (2) | maximum revision (2) | 4 reserved | hash appended (1)
/// 24  each segment: load address (4) | length (4, a multiple of 4) | its bytes
///     zeros up to a position that is 15 mod 16, then the checksum byte (0xEF xor every segment byte)
///     the SHA-256 of everything before it (32 bytes), when the header says it is appended
/// </code>
/// All numbers are little endian. <see cref="Write"/> makes the bytes, <see cref="Read"/> parses and verifies them.
/// </summary>
public sealed class EspImage
{
    public const byte Magic = 0xE9;

    /// <summary>The checksum's start value: esptool's ESP_CHECKSUM_MAGIC.</summary>
    public const byte ChecksumMagic = 0xEF;

    /// <summary>The WP pin field when the SPI flash's pins are not remapped: esptool's WP_PIN_DISABLED.</summary>
    public const byte WpPinDisabled = 0xEE;

    /// <summary>The ROM loads at most this many segments (ESP_IMAGE_MAX_SEGMENTS).</summary>
    public const int MaxSegments = 16;

    public const int HeaderSize = 8, ExtendedHeaderSize = 16, SegmentHeaderSize = 8, DigestSize = 32;

    public byte FlashMode { get; init; }

    /// <summary>Byte 3: the flash size in the high nibble, its frequency in the low one.</summary>
    public byte FlashSizeAndFrequency { get; init; }

    public uint Entry { get; init; }

    public byte WpPin { get; init; } = WpPinDisabled;

    /// <summary>The three drive-strength bytes (CLK and Q, D and CS, HD and WP; a nibble each).</summary>
    public byte[] DriveStrengths { get; init; } = [0, 0, 0];

    public ushort ChipId { get; init; }

    public byte MinRevision { get; init; }

    /// <summary>The chip revisions the image runs on, major * 100 + minor.</summary>
    public ushort MinRevisionFull { get; init; }

    public ushort MaxRevisionFull { get; init; } = ushort.MaxValue;

    public bool DigestAppended { get; init; } = true;

    public required IReadOnlyList<EspSegment> Segments { get; init; }

    /// <summary>The checksum byte of the segments' bytes, as the ROM checks it.</summary>
    public static byte Checksum(IEnumerable<EspSegment> segments)
    {
        var checksum = ChecksumMagic;
        foreach (var segment in segments)
        {
            foreach (var value in segment.Data)
            {
                checksum ^= value;
            }
        }

        return checksum;
    }

    /// <summary>The image's bytes. Each segment is padded with zeros to a multiple of 4 bytes.</summary>
    public byte[] Write()
    {
        if (Segments.Count is 0 or > MaxSegments)
        {
            throw new ChipSharpException(Segments.Count == 0
                ? "An ESP image needs at least one segment."
                : $"An ESP image has at most {MaxSegments} segments (the ROM loads no more), not {Segments.Count}.");
        }

        using var stream = new MemoryStream();
        var padded = Segments.Select(s => new EspSegment(s.Address, Padded(s.Data))).ToList();
        var word = new byte[4];

        stream.WriteByte(Magic);
        stream.WriteByte((byte)padded.Count);
        stream.WriteByte(FlashMode);
        stream.WriteByte(FlashSizeAndFrequency);
        BinaryPrimitives.WriteUInt32LittleEndian(word, Entry);
        stream.Write(word);

        stream.WriteByte(WpPin);
        stream.Write(DriveStrengths.Length == 3 ? DriveStrengths : [0, 0, 0]);
        var half = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(half, ChipId);
        stream.Write(half);
        stream.WriteByte(MinRevision);
        BinaryPrimitives.WriteUInt16LittleEndian(half, MinRevisionFull);
        stream.Write(half);
        BinaryPrimitives.WriteUInt16LittleEndian(half, MaxRevisionFull);
        stream.Write(half);
        stream.Write(new byte[4]);
        stream.WriteByte(DigestAppended ? (byte)1 : (byte)0);

        foreach (var segment in padded)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, segment.Address);
            stream.Write(word);
            BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)segment.Data.Length);
            stream.Write(word);
            stream.Write(segment.Data);
        }

        // zeros up to a position that is 15 mod 16, then the checksum: the image's length is a multiple of 16
        while (stream.Length % 16 != 15)
        {
            stream.WriteByte(0);
        }

        stream.WriteByte(Checksum(padded));
        if (DigestAppended)
        {
            stream.Write(SHA256.HashData(stream.ToArray()));
        }

        return stream.ToArray();
    }

    /// <summary>Parses an image and verifies its checksum and, when appended, its SHA-256.</summary>
    /// <exception cref="ChipSharpException">The bytes are not a well-formed ESP image, or a check fails.</exception>
    public static EspImage Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 1 || bytes[0] != Magic)
        {
            throw new ChipSharpException($"Not an ESP image: it must start with 0x{Magic:X2}.");
        }

        if (bytes.Length < HeaderSize + ExtendedHeaderSize)
        {
            throw new ChipSharpException("Not an ESP image: it is shorter than its headers.");
        }

        var count = bytes[1];
        var extended = bytes[HeaderSize..];
        var digestByte = extended[15];
        if (digestByte > 1)
        {
            throw new ChipSharpException($"Not an ESP image: its hash-appended byte is 0x{digestByte:X2}, not 0 or 1.");
        }

        var position = HeaderSize + ExtendedHeaderSize;
        var segments = new List<EspSegment>();
        for (var i = 0; i < count; i++)
        {
            if (position + SegmentHeaderSize > bytes.Length)
            {
                throw new ChipSharpException($"The ESP image ends inside the header of segment {i}.");
            }

            var address = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 4)..]);
            position += SegmentHeaderSize;
            if (length > (uint)(bytes.Length - position))
            {
                throw new ChipSharpException($"The ESP image ends inside segment {i} (0x{address:x8}, {length} bytes).");
            }

            segments.Add(new EspSegment(address, bytes.Slice(position, (int)length).ToArray()));
            position += (int)length;
        }

        while (position % 16 != 15)
        {
            position++;
        }

        var digestAppended = digestByte == 1;
        if (position >= bytes.Length || (digestAppended && position + 1 + DigestSize > bytes.Length))
        {
            throw new ChipSharpException("The ESP image ends before its checksum" + (digestAppended ? " and SHA-256." : "."));
        }

        var expected = Checksum(segments);
        if (bytes[position] != expected)
        {
            throw new ChipSharpException(
                $"The ESP image's checksum is 0x{bytes[position]:X2}, but its segments' is 0x{expected:X2}.");
        }

        if (digestAppended && !SHA256.HashData(bytes[..(position + 1)]).AsSpan().SequenceEqual(bytes.Slice(position + 1, DigestSize)))
        {
            throw new ChipSharpException("The ESP image's SHA-256 does not match its bytes.");
        }

        return new EspImage
        {
            FlashMode = bytes[2],
            FlashSizeAndFrequency = bytes[3],
            Entry = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            WpPin = extended[0],
            DriveStrengths = extended.Slice(1, 3).ToArray(),
            ChipId = BinaryPrimitives.ReadUInt16LittleEndian(extended[4..]),
            MinRevision = extended[6],
            MinRevisionFull = BinaryPrimitives.ReadUInt16LittleEndian(extended[7..]),
            MaxRevisionFull = BinaryPrimitives.ReadUInt16LittleEndian(extended[9..]),
            DigestAppended = digestAppended,
            Segments = segments,
        };
    }

    private static byte[] Padded(byte[] data)
    {
        if (data.Length % 4 == 0)
        {
            return data;
        }

        var padded = new byte[(data.Length + 3) / 4 * 4];
        data.CopyTo(padded, 0);
        return padded;
    }
}
