using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;

namespace ChipSharp.Runtime.Rp2040;

/// <summary>
/// The RP2040's second-stage bootloader ("boot2"): the first 256 bytes of flash. The boot ROM copies them to RAM,
/// checks their CRC and runs them; boot2 sets up the flash chip for fast reads and then starts the program through
/// the vector table at flash + 0x100. The last 4 bytes are the CRC of the first 252, so the code is padded with
/// zeros to 252 bytes and the CRC appended, as pico-sdk's pad_checksum does.
/// </summary>
/// <remarks>
/// The code comes from pico-sdk 2.1.1 (BSD-3-Clause, see Boot2/LICENSE.TXT): Boot2/boot2_w25q080.S and
/// Boot2/boot2_generic_03h.S, assembled with clang 23 for the Cortex-M0+
/// (<c>--target=armv6m-none-eabi -mcpu=cortex-m0plus</c>, include folders pico_platform/include, hardware_regs/include
/// and boot_stage2/asminclude, and an empty pico.h), linked with Boot2/boot_stage2.ld (at 0x20041f00, where the ROM
/// runs it) and turned into raw bytes with <c>llvm-objcopy -O binary</c>. The .bin files are those bytes, unpadded.
/// </remarks>
public static class Boot2Image
{
    /// <summary>The size of boot2 in flash, checksum included.</summary>
    public const int Size = 256;

    /// <summary>The most code boot2 can have: the rest is the checksum.</summary>
    public const int MaxCodeSize = Size - 4;

    /// <summary>The section the boot2 bytes are placed in; the target's link settings put it first in flash.</summary>
    public const string SectionName = ".boot2";

    /// <summary>
    /// The CRC the boot ROM checks: CRC-32/MPEG-2 (polynomial 0x04c11db7, bits not reflected, starting at
    /// 0xffffffff, result not inverted). The CRC of "123456789" is 0x0376e6e7.
    /// </summary>
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var b in bytes)
        {
            crc ^= (uint)b << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04c11db7u : crc << 1;
            }
        }

        return crc;
    }

    /// <summary>The 256 bytes for flash: <paramref name="code"/>, zeros up to 252 bytes, then the CRC (little-endian).</summary>
    public static byte[] Pad(ReadOnlySpan<byte> code, string name = "boot2")
    {
        if (code.Length > MaxCodeSize)
        {
            throw new ChipSharpException(
                $"The second-stage bootloader '{name}' is {code.Length} bytes; it must fit in {MaxCodeSize} " +
                $"(the boot ROM loads {Size} bytes, the last 4 of them the checksum).");
        }

        var image = new byte[Size];
        code.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(MaxCodeSize), Crc32(image.AsSpan(0, MaxCodeSize)));
        return image;
    }

    /// <summary>True when the last 4 bytes of a 256-byte boot2 are the CRC of the first 252, as the boot ROM checks.</summary>
    public static bool HasValidChecksum(ReadOnlySpan<byte> image) =>
        image.Length == Size
        && BinaryPrimitives.ReadUInt32LittleEndian(image[MaxCodeSize..]) == Crc32(image[..MaxCodeSize]);

    /// <summary>An assembly file that puts a padded <paramref name="image"/> in the section <see cref="SectionName"/>.</summary>
    public static string ToAssembly(ReadOnlySpan<byte> image, string sourceName)
    {
        var text = new StringBuilder()
            .Append("// The RP2040's second-stage bootloader, padded and checksummed by ChipSharp, from ")
            .Append(sourceName).Append('\n')
            .Append("// (Raspberry Pi pico-sdk, BSD-3-Clause: Copyright (c) 2019-2021 Raspberry Pi (Trading) Ltd.)\n\n")
            .Append("    .section ").Append(SectionName).Append(", \"ax\", %progbits\n");
        for (var offset = 0; offset < image.Length; offset += 16)
        {
            var line = image.Slice(offset, Math.Min(16, image.Length - offset)).ToArray();
            text.Append("    .byte ")
                .AppendJoin(", ", line.Select(b => "0x" + b.ToString("x2", CultureInfo.InvariantCulture)))
                .Append('\n');
        }

        return text.ToString();
    }
}
