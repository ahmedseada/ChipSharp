using System.Buffers.Binary;
using System.Text;
using ChipSharp.Abstractions;

namespace ChipSharp.Image.Esp;

/// <summary>
/// ESP-IDF's <c>esp_app_desc_t</c> (components/esp_app_format/include/esp_app_desc.h, release/v5.5), the 256 bytes at the
/// start of an app image's first segment, which ESP-IDF's second-stage bootloader reads before it starts the app:
/// <code>
/// 0   magic word 0xABCD5432 (4)   4 secure version (4)   8 reserved (8)
/// 16  version (32)   48 project name (32)   80 time (16)   96 date (16)   112 IDF version (32)
/// 144 SHA-256 of the .elf (32)   176 minimum eFuse block revision (2)   178 maximum (2)   180 MMU page size, log2 (1)
/// 181 reserved (3)   184 reserved (72)
/// </code>
/// The strings are NUL-padded. ChipSharp's program has it as its first 256 bytes (esp32c5_app_desc.c); the image writer
/// fills in the project name and the .elf's hash.
/// </summary>
public sealed class EspAppDescription
{
    public const uint MagicWord = 0xABCD5432;

    public const int Size = 256, VersionOffset = 16, ProjectNameOffset = 48, TimeOffset = 80, DateOffset = 96,
        IdfVersionOffset = 112, ElfSha256Offset = 144, MinEfuseOffset = 176, MaxEfuseOffset = 178, MmuPageSizeOffset = 180;

    public uint Magic { get; init; }

    public uint SecureVersion { get; init; }

    public required string Version { get; init; }

    public required string ProjectName { get; init; }

    public required string Time { get; init; }

    public required string Date { get; init; }

    public required string IdfVersion { get; init; }

    public required byte[] ElfSha256 { get; init; }

    public ushort MinEfuseBlockRevision { get; init; }

    public ushort MaxEfuseBlockRevision { get; init; }

    /// <summary>The MMU page size as log2 (16: 64 KB).</summary>
    public byte MmuPageSizeLog2 { get; init; }

    /// <summary>Whether <paramref name="segment"/> starts with the description's magic word and is long enough to hold it.</summary>
    public static bool IsPresent(ReadOnlySpan<byte> segment) =>
        segment.Length >= Size && BinaryPrimitives.ReadUInt32LittleEndian(segment) == MagicWord;

    /// <summary>The description at the start of <paramref name="segment"/> (a first segment's bytes), as the bootloader reads it.</summary>
    public static EspAppDescription Read(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < Size)
        {
            throw new ChipSharpException(
                $"The first segment has {segment.Length} bytes, too few for the application description ({Size}).");
        }

        return new EspAppDescription
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(segment),
            SecureVersion = BinaryPrimitives.ReadUInt32LittleEndian(segment[4..]),
            Version = Text(segment.Slice(VersionOffset, 32)),
            ProjectName = Text(segment.Slice(ProjectNameOffset, 32)),
            Time = Text(segment.Slice(TimeOffset, 16)),
            Date = Text(segment.Slice(DateOffset, 16)),
            IdfVersion = Text(segment.Slice(IdfVersionOffset, 32)),
            ElfSha256 = segment.Slice(ElfSha256Offset, 32).ToArray(),
            MinEfuseBlockRevision = BinaryPrimitives.ReadUInt16LittleEndian(segment[MinEfuseOffset..]),
            MaxEfuseBlockRevision = BinaryPrimitives.ReadUInt16LittleEndian(segment[MaxEfuseOffset..]),
            MmuPageSizeLog2 = segment[MmuPageSizeOffset],
        };
    }

    /// <summary>A copy of <paramref name="segment"/> with the project name and the .elf's hash written into its description.</summary>
    public static byte[] Fill(byte[] segment, string? projectName, byte[]? elfSha256)
    {
        var copy = (byte[])segment.Clone();
        if (projectName is not null)
        {
            var field = copy.AsSpan(ProjectNameOffset, 32);
            field.Clear();
            Encoding.ASCII.GetBytes(projectName.AsSpan(0, Math.Min(projectName.Length, 31)), field);
        }

        if (elfSha256 is { Length: 32 })
        {
            elfSha256.CopyTo(copy.AsSpan(ElfSha256Offset));
        }

        return copy;
    }

    private static string Text(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }
}
