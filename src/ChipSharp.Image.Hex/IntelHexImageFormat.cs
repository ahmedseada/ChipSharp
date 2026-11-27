using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Image.Uf2;

namespace ChipSharp.Image.Hex;

/// <summary>
/// The "hex" image format: Intel HEX, the text file an AVR's programmers load into its flash (avrdude, the Arduino
/// IDE, Wokwi's "Upload Firmware"), and many other chips' too. It holds the bytes the .elf loads: every allocated
/// section with bytes in the file (code, constants, the starting values of .data), at its load address (.data's in
/// flash, not where it runs in RAM), in address order. The file is what <c>llvm-objcopy -O ihex</c> writes, byte for
/// byte (<see cref="Write"/>).
/// </summary>
public sealed class IntelHexImageFormat : IImageFormat
{
    /// <summary>The data bytes of a full data record, as objcopy writes them.</summary>
    public const int RecordSize = 16;

    /// <summary>Record types: data, end of file, extended segment address (bits 4-19), start segment address (CS:IP),
    /// extended linear address (bits 16-31), start linear address.</summary>
    public const byte DataRecord = 0x00, EndOfFileRecord = 0x01, ExtendedSegmentAddressRecord = 0x02,
        StartSegmentAddressRecord = 0x03, ExtendedLinearAddressRecord = 0x04, StartLinearAddressRecord = 0x05;

    public string Name => "hex";

    public void Package(LinkedBinary binary, TargetDescription target, Stream output)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(output);

        var elf = ElfFile.Read(binary.Path);
        Write(Blocks(elf, target, binary.Path), elf.Entry, output);
    }

    /// <summary>
    /// Writes <paramref name="blocks"/> (bytes and the address they load at) as Intel HEX, as llvm-objcopy does: in
    /// address order, each block in data records of <see cref="RecordSize"/> bytes (the last shorter), a record never
    /// crossing a 64 KB boundary. Addresses from 64 KB to 1 MB take an extended segment address record (02) before the
    /// first record past each 64 KB boundary, addresses past 1 MB an extended linear address record (04); below 64 KB
    /// neither is needed. Then the entry point, when it is not 0: a start segment address record (03, CS:IP) below
    /// 1 MB, else a start linear address record (05), which programmers of chips that start at a fixed address (an
    /// AVR at its reset vector) pass over; and the end-of-file record. Upper-case digits, lines ending in CR LF.
    /// </summary>
    public static void Write(IEnumerable<(uint Address, byte[] Data)> blocks, uint entry, Stream output)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(output);

        var text = new StringBuilder();
        ulong segment = 0; // the extended segment address in force, shifted: 0x10000 to 0xf0000
        ulong linear = 0;  // the extended linear address in force, shifted: 0x10000 and up
        foreach (var (address, data) in blocks.OrderBy(block => block.Address))
        {
            ArgumentNullException.ThrowIfNull(data);
            if ((ulong)address + (ulong)data.Length > 1UL << 32)
            {
                throw new ArgumentException(
                    $"{data.Length} bytes at 0x{address:x8} reach past the 4 GB Intel HEX addresses.", nameof(blocks));
            }

            ulong at = address;
            for (var done = 0; done < data.Length;)
            {
                if (at > segment + linear + 0xffff)
                {
                    if (at > 0xfffff)
                    {
                        if (segment != 0)
                        {
                            segment = 0;
                            Record(text, ExtendedSegmentAddressRecord, 0, [0, 0]);
                        }

                        linear = at & 0xffff0000;
                        Record(text, ExtendedLinearAddressRecord, 0, [(byte)(linear >> 24), (byte)(linear >> 16)]);
                    }
                    else
                    {
                        segment = at & 0xf0000;
                        Record(text, ExtendedSegmentAddressRecord, 0, [(byte)(segment >> 12), 0]);
                    }
                }

                var offset = at - linear - segment;
                var size = (int)Math.Min((ulong)Math.Min(RecordSize, data.Length - done), 0x10000 - offset);
                Record(text, DataRecord, (ushort)offset, data.AsSpan(done, size));
                done += size;
                at += (ulong)size;
            }
        }

        if (entry != 0)
        {
            Record(text, entry <= 0xfffff ? StartSegmentAddressRecord : StartLinearAddressRecord, 0, entry <= 0xfffff
                ? [(byte)((entry & 0xf0000) >> 12), 0, (byte)(entry >> 8), (byte)entry]
                : [(byte)(entry >> 24), (byte)(entry >> 16), (byte)(entry >> 8), (byte)entry]);
        }

        Record(text, EndOfFileRecord, 0, []);
        output.Write(Encoding.ASCII.GetBytes(text.ToString()));
    }

    /// <summary>
    /// One record: ':', the number of data bytes, the address, the type, the data, and the checksum, the two's
    /// complement of the sum of all of those bytes.
    /// </summary>
    private static void Record(StringBuilder text, byte type, ushort address, ReadOnlySpan<byte> data)
    {
        var sum = data.Length + (address >> 8) + (address & 0xff) + type;
        text.Append(':').Append(Hex(data.Length)).Append(Hex(address >> 8)).Append(Hex(address & 0xff)).Append(Hex(type));
        foreach (var value in data)
        {
            text.Append(Hex(value));
            sum += value;
        }

        text.Append(Hex(-sum & 0xff)).Append("\r\n");
    }

    private static string Hex(int value) => value.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>
    /// The allocated sections with bytes in the file, at their load addresses. Every byte must lie in one of the target's
    /// memory regions, and no two sections may load the same byte.
    /// </summary>
    private static List<(uint Address, byte[] Data)> Blocks(ElfFile elf, TargetDescription target, string path)
    {
        var blocks = elf.Sections
            .Where(section => section.IsAllocated && !section.NoBits && section.Data.Length > 0)
            .OrderBy(section => section.LoadAddress)
            .ToList();
        if (blocks.Count == 0)
        {
            throw new ChipSharpException($"'{path}' has nothing to load: no section has any bytes.");
        }

        ulong end = 0;
        foreach (var section in blocks)
        {
            var (start, last) = ((ulong)section.LoadAddress, (ulong)section.LoadAddress + (ulong)section.Data.Length);
            if (!target.Memory.Values.Any(region => start >= region.Origin && last <= region.End))
            {
                throw new ChipSharpException(
                    $"'{path}' loads section {section.Name} ({section.Data.Length} bytes) at 0x{start:x8}-0x{last - 1:x8}, " +
                    $"which is not inside a memory region of target '{target.Name}' " +
                    $"({string.Join(", ", target.Memory.Select(m => $"{m.Key} 0x{m.Value.Origin:x8}-0x{m.Value.End - 1:x8}"))}).");
            }

            if (start < end)
            {
                throw new ChipSharpException(
                    $"'{path}' loads two sections at the same address: {section.Name} starts at 0x{start:x8}, before the " +
                    $"section ahead of it ends.");
            }

            end = last;
        }

        return [.. blocks.Select(section => (section.LoadAddress, section.Data))];
    }
}
