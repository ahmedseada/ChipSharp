using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ChipSharp.Abstractions;

namespace ChipSharp.Chips;

/// <summary>
/// Reads a CMSIS-SVD file (Arm's System View Description, which chip vendors publish: the RP2040's comes with pico-sdk,
/// Espressif's chips' with esp-pacs) into a <see cref="ChipDescription"/>: the device's peripherals (a peripheral
/// <c>derivedFrom</c> another has its registers at its own base), their registers and clusters (<c>dim</c> arrays
/// expanded: <c>CH%s</c> as CH0, CH1, ...), the fields (bitOffset and bitWidth, lsb and msb, or bitRange), their
/// enumerated values, the defaults a device, peripheral or cluster gives its registers (size, access, resetValue), and
/// the interrupts. A register's C name is its peripheral's name, an underscore and its own (a cluster's name between):
/// <c>TIMER_TIMERAWL</c>, as the RP2040's datasheet and pico-sdk name them. SVD says nothing of pins or memory.
/// </summary>
public static partial class Svd
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
    };

    /// <summary>Reads the SVD file at <paramref name="path"/>.</summary>
    public static ChipDescription Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = File.OpenRead(path);
        return Read(stream, path);
    }

    /// <summary>Reads an SVD document from <paramref name="stream"/>; <paramref name="source"/> names it in messages.</summary>
    public static ChipDescription Read(Stream stream, string source = "SVD file")
    {
        ArgumentNullException.ThrowIfNull(stream);
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(stream, Settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new ChipSharpException($"{source}: not valid XML (line {e.LineNumber}): {e.Message}", e);
        }

        var device = document.Root is { Name.LocalName: "device" } root
            ? root
            : throw new ChipSharpException($"{source}: an SVD file's root is <device>.");
        var defaults = new Defaults(
            Number(device, "size", source) is { } size ? (int)size : 32,
            ParseAccess(Value(device, "access")) ?? RegisterAccess.ReadWrite,
            Number(device, "resetValue", source));

        var peripherals = new List<ChipPeripheral>();
        var interrupts = new Dictionary<int, ChipInterrupt>();
        var elements = device.Element("peripherals")?.Elements("peripheral").ToList() ?? [];
        var byName = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            var name = Value(element, "name") ?? throw new ChipSharpException($"{source}: a peripheral has no name.");
            byName[name] = element;
        }

        foreach (var element in elements)
        {
            var name = Value(element, "name")!;
            var baseAddress = Number(element, "baseAddress", source)
                ?? throw new ChipSharpException($"{source}: peripheral {name} has no baseAddress.");

            // a peripheral derived from another has its registers (unless it has its own), at its own base
            var original = element.Attribute("derivedFrom")?.Value is { } from
                ? byName.GetValueOrDefault(from) ?? throw new ChipSharpException(
                    $"{source}: peripheral {name} is derived from {from}, which the file does not have.")
                : null;
            var registersElement = element.Element("registers") ?? original?.Element("registers");
            var peripheralDefaults = defaults.Under(original, source).Under(element, source);
            var registers = new List<ChipRegister>();
            Registers(registersElement, name, baseAddress, peripheralDefaults, registers, source);
            peripherals.Add(new ChipPeripheral(name, baseAddress, AddressSpace.Memory,
                Clean(Value(element, "description") ?? (original is null ? null : Value(original, "description"))),
                registers));

            foreach (var interrupt in element.Elements("interrupt"))
            {
                var number = (int)(Number(interrupt, "value", source)
                    ?? throw new ChipSharpException($"{source}: an interrupt of {name} has no value."));
                interrupts.TryAdd(number, new ChipInterrupt(Value(interrupt, "name") ?? $"IRQ{number}", number, name,
                    Clean(Value(interrupt, "description"))));
            }
        }

        var cpu = device.Element("cpu");
        return new ChipDescription
        {
            Name = Value(device, "name") ?? throw new ChipSharpException($"{source}: the device has no name."),
            Vendor = Value(device, "vendor"),
            Description = Clean(Value(device, "description")),
            Cpu = cpu is null ? null : Value(cpu, "name"),
            Endian = cpu is null ? "little" : Value(cpu, "endian") ?? "little",
            Width = Number(device, "width", source) is { } width ? (int)width : 32,
            Peripherals = peripherals,
            Interrupts = [.. interrupts.Values.OrderBy(i => i.Number)],
        };
    }

    /// <summary>SVD's access, and ChipSharp's description's: null for anything else.</summary>
    public static RegisterAccess? ParseAccess(string? text) => text switch
    {
        "read-write" => RegisterAccess.ReadWrite,
        "read-only" => RegisterAccess.ReadOnly,
        "write-only" => RegisterAccess.WriteOnly,
        "writeOnce" => RegisterAccess.WriteOnce,
        "read-writeOnce" => RegisterAccess.ReadWriteOnce,
        _ => null,
    };

    /// <summary>
    /// The registers of <paramref name="parent"/> (a peripheral's registers, or a cluster), and of its clusters, each
    /// array expanded, added to <paramref name="registers"/> with their C names from <paramref name="prefix"/> on.
    /// </summary>
    private static void Registers(XElement? parent, string prefix, ulong baseAddress, Defaults defaults,
        List<ChipRegister> registers, string source)
    {
        if (parent is null)
        {
            return;
        }

        var byName = parent.Elements("register").Where(r => Value(r, "name") is not null)
            .GroupBy(r => Value(r, "name")!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var element in parent.Elements())
        {
            if (element.Name.LocalName is not ("register" or "cluster"))
            {
                continue;
            }

            var offset = Number(element, "addressOffset", source) ?? 0;
            foreach (var (name, step) in Instances(element, source))
            {
                var address = baseAddress + offset + step;
                if (element.Name.LocalName == "cluster")
                {
                    Registers(element, $"{prefix}_{name}", address, defaults.Under(element, source), registers, source);
                    continue;
                }

                // a register derived from another of the same parent has its fields and settings, unless it gives its own
                var original = element.Attribute("derivedFrom")?.Value is { } from ? byName.GetValueOrDefault(from) : null;
                var own = defaults.Under(original, source).Under(element, source);
                var fields = element.Element("fields") ?? original?.Element("fields");
                registers.Add(new ChipRegister(name, $"{prefix}_{name}", address, own.Size, own.Access, own.Reset,
                    Clean(Value(element, "description") ?? (original is null ? null : Value(original, "description"))),
                    [.. Fields(fields, own.Access, $"{prefix}_{name}", source)]));
            }
        }
    }

    /// <summary>
    /// The names and address steps of an element and, for an array (dim), each of its elements: <c>%s</c> in the name
    /// replaced by the index (dimIndex's, else 0, 1, ...), a <c>[%s]</c> array by the plain index.
    /// </summary>
    private static IEnumerable<(string Name, ulong Step)> Instances(XElement element, string source)
    {
        var name = Value(element, "name") ?? throw new ChipSharpException($"{source}: a {element.Name.LocalName} has no name.");
        if (Number(element, "dim", source) is not { } dim)
        {
            yield return (name, 0);
            yield break;
        }

        var increment = Number(element, "dimIncrement", source) ?? 0;
        var indices = Value(element, "dimIndex") is { } text ? DimIndex(text) : [];
        for (var i = 0UL; i < dim; i++)
        {
            var index = i < (ulong)indices.Count ? indices[(int)i] : i.ToString(CultureInfo.InvariantCulture);
            yield return (name.Replace("[%s]", index, StringComparison.Ordinal).Replace("%s", index, StringComparison.Ordinal),
                i * increment);
        }
    }

    /// <summary>dimIndex: "0-3", "A,B,C" or "0,2,4".</summary>
    private static List<string> DimIndex(string text)
    {
        var range = DimRange().Match(text);
        if (range.Success)
        {
            var from = int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
            var to = int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
            return [.. Enumerable.Range(from, to - from + 1).Select(i => i.ToString(CultureInfo.InvariantCulture))];
        }

        return [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
    }

    private static IEnumerable<ChipField> Fields(XElement? fields, RegisterAccess access, string register, string source)
    {
        foreach (var field in fields?.Elements("field") ?? [])
        {
            var name = Value(field, "name") ?? throw new ChipSharpException($"{source}: a field of {register} has no name.");
            int offset, width;
            if (Number(field, "bitOffset", source) is { } bitOffset)
            {
                offset = (int)bitOffset;
                width = (int)(Number(field, "bitWidth", source) ?? 1);
            }
            else if (Number(field, "lsb", source) is { } lsb)
            {
                offset = (int)lsb;
                width = (int)(Number(field, "msb", source) ?? lsb) - offset + 1;
            }
            else if (Value(field, "bitRange") is { } text && BitRange().Match(text) is { Success: true } range)
            {
                offset = int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
                width = int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture) - offset + 1;
            }
            else
            {
                throw new ChipSharpException($"{source}: field {register}.{name} has no bits (bitOffset, lsb or bitRange).");
            }

            var values = field.Elements("enumeratedValues").SelectMany(e => e.Elements("enumeratedValue"))
                .Where(v => Value(v, "value") is not null && Value(v, "name") is not null)
                .Select(v => (Value: EnumeratedValue(Value(v, "value")!), Element: v))
                .Where(v => v.Value is not null)
                .Select(v => new ChipFieldValue(Value(v.Element, "name")!, v.Value!.Value, Clean(Value(v.Element, "description"))))
                .ToList();
            yield return new ChipField(name, offset, width, ParseAccess(Value(field, "access")) ?? access,
                Clean(Value(field, "description")), values);
        }
    }

    /// <summary>An enumerated value: a number, 0x..., 0b... or #... (binary); null for one with "don't care" bits (x).</summary>
    private static ulong? EnumeratedValue(string text)
    {
        text = text.Trim();
        if (text.StartsWith('#') || text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            var bits = text.StartsWith('#') ? text[1..] : text[2..];
            return bits.All(c => c is '0' or '1') ? Convert.ToUInt64(bits, 2) : null;
        }

        return ParseNumber(text);
    }

    /// <summary>SVD's scaledNonNegativeInteger: decimal, 0x... hex, #... binary.</summary>
    private static ulong? ParseNumber(string text)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex
            : text.StartsWith('#') ? Convert.ToUInt64(text[1..], 2)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number
            : null;
    }

    private static string? Value(XElement element, string name) => element.Element(name)?.Value.Trim();

    private static ulong? Number(XElement element, string name, string source) => Value(element, name) is { } text
        ? ParseNumber(text) ?? throw new ChipSharpException($"{source}: <{name}>{text}</{name}> is not a number.")
        : null;

    /// <summary>
    /// A description on one line. Espressif's SVD files break a description's lines with two backslashes
    /// ("...the RWDT.\\0: Disable\\1: Enable"): each break is "; " (a space after a full stop or a colon), and one at
    /// the end goes, so that no backslash ends a line of C (it would join the next line to its comment).
    /// </summary>
    private static string? Clean(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var line = Spaces().Replace(text, " ").Trim();
        line = LineBreaks().Replace(line, match => match.Index + match.Length == line.Length ? ""
            : match.Index > 0 && line[match.Index - 1] is '.' or ':' or ';' ? " " : "; ");
        return line.TrimEnd('\\', ' ');
    }

    [GeneratedRegex(@"^\s*\[(\d+):(\d+)\]\s*$")]
    private static partial Regex BitRange();

    [GeneratedRegex(@"^\s*(\d+)\s*-\s*(\d+)\s*$")]
    private static partial Regex DimRange();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*\\\\\s*")]
    private static partial Regex LineBreaks();

    /// <summary>The register settings an element gives what it holds, over those of the element around it.</summary>
    private sealed record Defaults(int Size, RegisterAccess Access, ulong? Reset)
    {
        public Defaults Under(XElement? element, string source) => element is null
            ? this
            : new Defaults(
                Number(element, "size", source) is { } size ? (int)size : Size,
                ParseAccess(Value(element, "access")) ?? Access,
                Number(element, "resetValue", source) ?? Reset);
    }
}
