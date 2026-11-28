using System.Globalization;
using ChipSharp.Abstractions;

namespace ChipSharp.Image.Hex;

/// <summary>
/// An Intel HEX file read back, e.g. the .ihx SDCC links a program into: the bytes it loads, as blocks of consecutive
/// addresses in address order, and the start address its start segment (03) or start linear (05) address record gives
/// (0 when it has none, as SDCC's have). Data records (00) are placed by the extended segment (02) and extended linear
/// (04) address records before them; the end-of-file record (01) ends it.
/// </summary>
public sealed record IntelHexFile(IReadOnlyList<(uint Address, byte[] Data)> Blocks, uint Entry)
{
    /// <summary>True when the file starts as Intel HEX does, with a ':' (an .elf starts with 0x7f 'E' 'L' 'F').</summary>
    public static bool IsIntelHex(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            int first;
            while ((first = stream.ReadByte()) is ' ' or '\t' or '\r' or '\n')
            {
            }

            return first == ':';
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <exception cref="ChipSharpException">The file cannot be read, or is not well-formed Intel HEX.</exception>
    public static IntelHexFile Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ChipSharpException($"Cannot read '{path}': {e.Message}", e);
        }

        return Parse(text, path);
    }

    /// <summary>The records of <paramref name="text"/>; <paramref name="source"/> names it in errors.</summary>
    /// <exception cref="ChipSharpException">A record is not well formed, or two load the same address.</exception>
    public static IntelHexFile Parse(string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new SortedDictionary<uint, byte>();
        ulong upper = 0; // the extended segment address (shifted by 4) or extended linear address (by 16) in force
        uint entry = 0;
        var lines = text.Split('\n');
        var ended = false;
        for (var number = 1; number <= lines.Length && !ended; number++)
        {
            var line = lines[number - 1].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            ChipSharpException Error(string message) => new($"'{source}' line {number}: {message}");
            var record = Record(line) ?? throw Error("is not an Intel HEX record (':', then pairs of hex digits).");
            if (record.Length < 5 || record.Length != 5 + record[0])
            {
                throw Error($"has {record.Length} bytes, not the 5 and the {record[0]} of data its length says.");
            }

            if (record.Aggregate(0, (sum, value) => sum + value) % 256 != 0)
            {
                throw Error("has the wrong checksum.");
            }

            var type = record[3];
            var address = (uint)(record[1] << 8 | record[2]);
            var data = record.AsSpan(4, record[0]);
            switch (type)
            {
                case IntelHexImageFormat.DataRecord:
                    for (var i = 0; i < data.Length; i++)
                    {
                        // a record's addresses wrap within its 64 KB, as the format says
                        var at = (uint)(upper + ((address + (uint)i) & 0xffff));
                        if (!bytes.TryAdd(at, data[i]))
                        {
                            throw Error($"loads address 0x{at:x8} again.");
                        }
                    }

                    break;
                case IntelHexImageFormat.EndOfFileRecord:
                    ended = true;
                    break;
                case IntelHexImageFormat.ExtendedSegmentAddressRecord when data.Length == 2:
                    upper = (ulong)(data[0] << 8 | data[1]) << 4;
                    break;
                case IntelHexImageFormat.ExtendedLinearAddressRecord when data.Length == 2:
                    upper = (ulong)(data[0] << 8 | data[1]) << 16;
                    break;
                case IntelHexImageFormat.StartSegmentAddressRecord when data.Length == 4:
                    entry = (uint)((data[0] << 8 | data[1]) << 4) + (uint)(data[2] << 8 | data[3]);
                    break;
                case IntelHexImageFormat.StartLinearAddressRecord when data.Length == 4:
                    entry = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
                    break;
                default:
                    throw Error($"is a record of type {type:X2} with {data.Length} bytes of data, which Intel HEX does not have.");
            }
        }

        // consecutive addresses as one block
        var blocks = new List<(uint Address, byte[] Data)>();
        var run = new List<byte>();
        uint start = 0, next = 0;
        foreach (var (address, value) in bytes)
        {
            if (run.Count > 0 && address != next)
            {
                blocks.Add((start, [.. run]));
                run.Clear();
            }

            if (run.Count == 0)
            {
                start = address;
            }

            run.Add(value);
            next = address + 1;
        }

        if (run.Count > 0)
        {
            blocks.Add((start, [.. run]));
        }

        return new IntelHexFile(blocks, entry);
    }

    /// <summary>The bytes of a record, ":10010000..." without its ':'; null when it is not one.</summary>
    private static byte[]? Record(string line)
    {
        if (line[0] != ':' || line.Length % 2 == 0)
        {
            return null;
        }

        var record = new byte[(line.Length - 1) / 2];
        for (var i = 0; i < record.Length; i++)
        {
            if (!byte.TryParse(line.AsSpan(1 + 2 * i, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out record[i]))
            {
                return null;
            }
        }

        return record;
    }
}
