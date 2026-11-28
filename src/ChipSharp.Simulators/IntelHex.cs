using System.Globalization;

namespace ChipSharp.Simulators;

/// <summary>An image that cannot be loaded: not Intel HEX, or with bytes where the chip has no program memory.</summary>
public sealed class ImageException(string message) : Exception(message);

/// <summary>
/// The bytes an Intel HEX file loads into one memory of a chip, from <see cref="Start"/>: which of them it fills, and how
/// many. Addresses in the file are byte addresses, as SDCC's linkers write them (sdldstm8, sdldpdk) and as tools/avr-sim
/// reads them. For a Padauk chip, whose program memory holds 14-bit words, a word at word address w is the two bytes at
/// 2w and 2w + 1, low byte first (what <c>sdcc -mpdk14</c> writes, and the Free-PDK programmer reads).
/// </summary>
public sealed class IntelHexImage
{
    private IntelHexImage(int start, byte[] bytes, bool[] loaded, int count)
    {
        Start = start;
        Bytes = bytes;
        Loaded = loaded;
        Count = count;
    }

    /// <summary>An image of <paramref name="bytes"/> at <paramref name="start"/>, <paramref name="count"/> of them <paramref name="loaded"/>.</summary>
    public static IntelHexImage FromBytes(int start, byte[] bytes, bool[] loaded, int count)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(loaded);
        return new IntelHexImage(start, bytes, loaded, count);
    }

    /// <summary>The address of <see cref="Bytes"/>[0].</summary>
    public int Start { get; }

    /// <summary>The memory, as the file fills it; what it does not fill is <c>fill</c>.</summary>
    public byte[] Bytes { get; }

    /// <summary>Which bytes of <see cref="Bytes"/> the file fills.</summary>
    public bool[] Loaded { get; }

    /// <summary>How many bytes the file loads.</summary>
    public int Count { get; }

    /// <summary>
    /// Reads <paramref name="path"/> into a memory of <paramref name="size"/> bytes at <paramref name="start"/> (e.g. the
    /// STM8S103's 8 KB of flash at 0x8000), every other byte <paramref name="fill"/> (what erased memory reads). Data
    /// records (00) load at their address, with the extended segment (02) and linear (04) address records; start address
    /// records (03, 05) are allowed and not needed. Every record's checksum is checked; a byte outside the memory, a
    /// record that is not Intel HEX or a file without its end record throws <see cref="ImageException"/>, saying where.
    /// <paramref name="memory"/> names the memory in that message ("the STM8S103's 8 KB of flash").
    /// </summary>
    public static IntelHexImage Load(string path, int start, int size, byte fill, string memory)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, System.Text.Encoding.Latin1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ImageException($"cannot read '{path}': {e.Message}");
        }

        var bytes = new byte[size];
        Array.Fill(bytes, fill);
        var loaded = new bool[size];
        var count = 0;
        var baseAddress = 0;
        var ended = false;
        var lines = text.Split('\n');
        for (var number = 1; number <= lines.Length; number++)
        {
            var line = lines[number - 1].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var where = $"line {number} of '{path}'";
            if (ended)
            {
                throw new ImageException($"{where} comes after the end-of-file record.");
            }

            if (line.Length < 11 || line[0] != ':' || line.Length % 2 == 0 || !line.Skip(1).All(Uri.IsHexDigit))
            {
                throw new ImageException($"{where} is not an Intel HEX record (':' and pairs of hex digits): '{line}'.");
            }

            var record = Convert.FromHexString(line.AsSpan(1));
            int length = record[0];
            var type = record[3];
            if (record.Length != length + 5)
            {
                throw new ImageException($"{where} says it has {length} data bytes but has {record.Length - 5}.");
            }

            if ((record.Sum(b => b) & 0xFF) != 0)
            {
                throw new ImageException($"{where} has a wrong checksum.");
            }

            var data = record.AsSpan(4, length);
            switch (type)
            {
                case 0x00:
                    var address = baseAddress + ((record[1] << 8) | record[2]);
                    if (address < start || address + length > start + size)
                    {
                        throw new ImageException(
                            $"{where} writes {length} bytes at 0x{address.ToString("x", CultureInfo.InvariantCulture)}, " +
                            $"outside {memory} (0x{start:x4}-0x{start + size - 1:x4}).");
                    }

                    data.CopyTo(bytes.AsSpan(address - start));
                    for (var i = 0; i < length; i++)
                    {
                        count += loaded[address - start + i] ? 0 : 1;
                        loaded[address - start + i] = true;
                    }

                    break;
                case 0x01:
                    ended = true;
                    break;
                case 0x02 when length == 2:
                    baseAddress = ((data[0] << 8) | data[1]) << 4;
                    break;
                case 0x04 when length == 2:
                    baseAddress = ((data[0] << 8) | data[1]) << 16;
                    break;
                case 0x03 or 0x05 when length == 4:
                    break;
                default:
                    throw new ImageException(
                        $"{where} is a record of type {type} with {length} bytes, which is not one Intel HEX has.");
            }
        }

        if (!ended)
        {
            throw new ImageException($"'{path}' has no end-of-file record (:00000001FF); is it cut short?");
        }

        if (count == 0)
        {
            throw new ImageException($"'{path}' has no data to load.");
        }

        return new IntelHexImage(start, bytes, loaded, count);
    }
}
