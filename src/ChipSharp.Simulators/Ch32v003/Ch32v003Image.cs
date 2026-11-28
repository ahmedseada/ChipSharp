using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Image.Uf2;

namespace ChipSharp.Simulators.Ch32v003;

/// <summary>
/// What runs on a simulated CH32V003: the 16 KB of flash as a WCH-LinkE (or WCH's bootloader) writes them, from an
/// Intel HEX file (addresses at 0x0800_0000 on, or at the alias 0 on, as WCH's linker script links), a raw .bin (at
/// 0x0800_0000) or an ELF file (its PT_LOAD segments at their physical addresses, in flash or its alias: .data's load
/// address in flash; the symbols, for the tests). Bytes for anywhere else (SRAM among them: the chip loads only its
/// flash) are refused. What the image does not fill reads 0xFF.
/// </summary>
public sealed class Ch32v003Image
{
    private Ch32v003Image(byte[] flash, bool[] loaded, int count, string format, IReadOnlyDictionary<string, uint> symbols)
    {
        Flash = flash;
        Loaded = loaded;
        Count = count;
        Format = format;
        Symbols = symbols;
    }

    /// <summary>The flash, 16 KB, from 0x0800_0000.</summary>
    public byte[] Flash { get; }

    /// <summary>Which bytes of <see cref="Flash"/> the image fills.</summary>
    public bool[] Loaded { get; }

    /// <summary>How many bytes it fills.</summary>
    public int Count { get; }

    /// <summary>"Intel HEX", "binary" or "ELF".</summary>
    public string Format { get; }

    /// <summary>The ELF's symbols by name (at the addresses it links them, the alias 0 or 0x0800_0000); none otherwise.</summary>
    public IReadOnlyDictionary<string, uint> Symbols { get; }

    /// <summary>Reads <paramref name="path"/>: an ELF file (it starts with 0x7F 'E' 'L' 'F'), Intel HEX (.hex, .ihx, or ':'), else a .bin.</summary>
    public static Ch32v003Image Load(string path)
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
            return FromElf(bytes, path);
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".hex" or ".ihx" || (extension != ".bin" && bytes.Length > 0 && bytes[0] == (byte)':'))
        {
            return FromHex(bytes, path);
        }

        if (bytes.Length == 0 || bytes.Length > Ch32v003Map.FlashSize)
        {
            throw new ImageException(string.Create(CultureInfo.InvariantCulture,
                $"'{path}' has {bytes.Length} bytes: a .bin for the CH32V003 has 1 to {Ch32v003Map.FlashSize} (its flash)."));
        }

        var flash = Erased();
        bytes.CopyTo(flash, 0);
        var loaded = new bool[Ch32v003Map.FlashSize];
        Array.Fill(loaded, true, 0, bytes.Length);
        return new Ch32v003Image(flash, loaded, bytes.Length, "binary", new Dictionary<string, uint>());
    }

    private static Ch32v003Image FromHex(byte[] bytes, string path)
    {
        // the base the first extended linear address record (type 04) gives: 0x0800 for the flash's own addresses
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var first = text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length >= 13 && line[7..9] == "04");
        var start = first is not null && first[9..13] == "0800" ? (int)Ch32v003Map.FlashStart : 0;
        var hex = IntelHexImage.Load(path, start, (int)Ch32v003Map.FlashSize, 0xFF, "the CH32V003's 16 KB of flash");
        return new Ch32v003Image(hex.Bytes, hex.Loaded, hex.Count, "Intel HEX", new Dictionary<string, uint>());
    }

    private static Ch32v003Image FromElf(byte[] bytes, string path)
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

        var flash = Erased();
        var loaded = new bool[Ch32v003Map.FlashSize];
        var count = 0;
        foreach (var segment in elf.Segments.Where(segment => segment.Data.Length > 0))
        {
            var address = segment.PhysicalAddress;
            var offset = address >= Ch32v003Map.FlashStart ? address - Ch32v003Map.FlashStart : address;
            if ((address < Ch32v003Map.FlashSize || address >= Ch32v003Map.FlashStart) &&
                (long)offset + segment.Data.Length <= Ch32v003Map.FlashSize)
            {
                segment.Data.CopyTo(flash, offset);
                for (var i = 0; i < segment.Data.Length; i++)
                {
                    count += loaded[offset + i] ? 0 : 1;
                    loaded[offset + i] = true;
                }

                continue;
            }

            throw new ImageException(string.Create(CultureInfo.InvariantCulture,
                $"'{path}' has {segment.Data.Length} bytes at 0x{address:x8}, which is not the CH32V003's flash " +
                $"(0x08000000-0x08003fff, or its alias 0x00000000-0x00003fff): the chip loads only its flash, so .data's " +
                $"load address must be there"));
        }

        if (count == 0)
        {
            throw new ImageException($"'{path}' has nothing to load into the flash.");
        }

        return new Ch32v003Image(flash, loaded, count, "ELF", elf.Symbols);
    }

    private static byte[] Erased()
    {
        var flash = new byte[Ch32v003Map.FlashSize];
        Array.Fill(flash, (byte)0xFF);
        return flash;
    }
}
