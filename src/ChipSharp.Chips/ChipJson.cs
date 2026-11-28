using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Targets;

namespace ChipSharp.Chips;

/// <summary>
/// Reads ChipSharp's own description of a chip (the repository's chips folder: <c>chips/stm8s103f3.json</c>), JSON with
/// comments, for the chips no vendor describes in CMSIS-SVD:
/// <code>
/// {
///   "name": "stm8s103f3", "vendor": "STMicroelectronics", "cpu": "stm8", "endian": "big",
///   "width": 8,                                   // a register's size when it does not say, in bits
///   "memory": { "flash": { "origin": "0x8000", "size": "8K", "access": "rx" } },
///   "peripherals": {
///     "UART1": {
///       "base": "0x5230", "description": "...",
///       "registers": {
///         "SR": { "offset": 0, "access": "read-only", "reset": "0xc0",
///                 "fields": { "TXE": 7, "RXNE": 5, "PE": "0:0" } },          // a bit, or "msb:lsb"
///         "BRR2": { "offset": 3, "fields": { "DIV_HIGH": { "bits": "7:4", "description": "..." } } }
///       }
///     },
///     "PB": { "base": "0x5005", "derivedFrom": "PA" },  // the same registers as PA's, from another base
///     "PORTA": { "space": "io", "prefix": "", ... }    // the Padauk's I/O space; registers named as they are (PA)
///   },
///   "interrupts": { "UART1_RX": { "number": 18, "peripheral": "UART1" },
///                   "T16": { "number": 0, "request": { "register": "INTRQ", "enable": "INTEN", "bit": 2 } } },
///   "ports": { "B": { "registers": { "out": "PB_ODR", "in": "PB_IDR", "direction": "PB_DDR" } } },
///   "pins": { "PB5": { "functions": ["I2C_SDA"] } },  // port B, bit 5, number 8 * 1 + 5 = 13 unless "number" says
///   "constants": { "CLKMD_IHRC_DIV8": { "value": "0x28", "description": "..." } }
/// }
/// </code>
/// A register's C name is its peripheral's "prefix" (the peripheral's name unless it says), an underscore and its own:
/// <c>UART1_SR</c>; with <c>"prefix": ""</c> its own alone. A field's values (<c>"values": { "DIV8": 3 }</c>) are
/// unshifted. Numbers are JSON numbers, or text: hex (0x...), or a size (8K).
/// </summary>
public static partial class ChipJson
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads the description in <paramref name="path"/>.</summary>
    public static ChipDescription Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Reads a description from its text; <paramref name="source"/> names it in messages.</summary>
    public static ChipDescription Parse(string text, string source = "chip description")
    {
        ArgumentNullException.ThrowIfNull(text);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: Options);
        }
        catch (JsonException e)
        {
            throw new ChipSharpException($"{source}: not valid JSON (line {e.LineNumber + 1}): {e.Message}", e);
        }

        if (node is not JsonObject chip)
        {
            throw new ChipSharpException($"{source}: a chip description is a JSON object.");
        }

        var reader = new Reader(source);
        var width = chip["width"] is { } w ? (int)reader.Number(w, "width") : 8;
        var peripherals = reader.Peripherals(chip["peripherals"] as JsonObject, width);
        var pins = reader.Pins(chip["pins"] as JsonObject);
        var description = new ChipDescription
        {
            Name = reader.Text(chip["name"], "name") ?? throw reader.Error("\"name\" is missing."),
            Vendor = reader.Text(chip["vendor"], "vendor"),
            Description = reader.Text(chip["description"], "description"),
            Cpu = reader.Text(chip["cpu"], "cpu"),
            Endian = reader.Text(chip["endian"], "endian") ?? "little",
            Width = width,
            Memory = reader.Memory(chip["memory"] as JsonObject),
            Peripherals = peripherals,
            Interrupts = reader.Interrupts(chip["interrupts"] as JsonObject),
            Ports = reader.Ports(chip["ports"] as JsonObject, pins),
            Pins = pins,
            Constants = reader.Constants(chip["constants"] as JsonObject),
        };
        Check(description, reader);
        return description;
    }

    /// <summary>What every description must be: one register per C name, fields inside their registers apart.</summary>
    private static void Check(ChipDescription chip, Reader reader)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var register in chip.Registers)
        {
            if (!names.Add(register.FullName))
            {
                throw reader.Error($"two registers are named {register.FullName}.");
            }

            ulong taken = 0;
            foreach (var field in register.Fields)
            {
                if (field.Offset + field.Width > register.Size)
                {
                    throw reader.Error($"{register.FullName}.{field.Name} (bits {field.Offset + field.Width - 1}:{field.Offset}) " +
                        $"is outside the register's {register.Size} bits.");
                }

                if ((taken & field.Mask) != 0)
                {
                    throw reader.Error($"{register.FullName}.{field.Name} overlaps another field of the register.");
                }

                taken |= field.Mask;
            }
        }

        foreach (var port in chip.Ports)
        {
            foreach (var (role, register) in port.Registers)
            {
                if (chip.Register(register) is null)
                {
                    throw reader.Error($"port {port.Name}'s {role} register {register} is not one of the chip's registers.");
                }
            }
        }

        foreach (var request in chip.Interrupts.Select(i => i.Request).OfType<ChipInterruptRequest>())
        {
            if (chip.Register(request.Register) is null || chip.Register(request.Enable) is null)
            {
                throw reader.Error($"an interrupt's request register {request.Register} or enable register " +
                    $"{request.Enable} is not one of the chip's registers.");
            }
        }
    }

    [GeneratedRegex(@"^P([A-Z])([0-7])$")]
    private static partial Regex PortPin();

    [GeneratedRegex(@"^(\d+)(?::(\d+))?$")]
    private static partial Regex Bits();

    private sealed class Reader(string source)
    {
        public ChipSharpException Error(string message) => new($"{source}: {message}");

        public string? Text(JsonNode? node, string what) => node switch
        {
            null => null,
            JsonValue value when value.TryGetValue(out string? text) => text,
            _ => throw Error($"\"{what}\" must be text."),
        };

        public ulong Number(JsonNode? node, string what)
        {
            try
            {
                return node switch
                {
                    JsonValue value when value.TryGetValue(out ulong number) => number,
                    JsonValue value when value.TryGetValue(out string? text) => SizeParser.Parse(text),
                    _ => throw Error($"\"{what}\" must be a number."),
                };
            }
            catch (Exception e) when (e is FormatException or OverflowException)
            {
                throw Error($"\"{what}\" is {node!.ToJsonString()}, not a number: {e.Message}");
            }
        }

        public IReadOnlyList<ChipMemory> Memory(JsonObject? memory) =>
        [
            .. (memory ?? []).Select(pair => pair.Value is JsonObject region
                ? new ChipMemory(pair.Key, Number(region["origin"], $"memory.{pair.Key}.origin"),
                    Number(region["size"], $"memory.{pair.Key}.size"), Text(region["access"], "access"),
                    Text(region["description"], "description"))
                : throw Error($"memory.{pair.Key} must be an object.")),
        ];

        public IReadOnlyList<ChipPeripheral> Peripherals(JsonObject? peripherals, int width)
        {
            var read = new List<ChipPeripheral>();
            foreach (var (name, node) in peripherals ?? [])
            {
                if (node is not JsonObject peripheral)
                {
                    throw Error($"peripheral {name} must be an object.");
                }

                var space = Text(peripheral["space"], "space") switch
                {
                    null or "memory" => AddressSpace.Memory,
                    "io" => AddressSpace.Io,
                    var other => throw Error($"peripheral {name}: \"space\" is \"{other}\": \"memory\" or \"io\"."),
                };
                var prefix = Text(peripheral["prefix"], "prefix") ?? name;
                var baseAddress = peripheral["base"] is { } b ? Number(b, $"{name}.base") : 0;
                var description = Text(peripheral["description"], "description");
                var size = peripheral["width"] is { } pw ? (int)Number(pw, $"{name}.width") : width;
                List<ChipRegister> registers;
                if (Text(peripheral["derivedFrom"], "derivedFrom") is { } from)
                {
                    var original = read.FirstOrDefault(p => p.Name == from)
                        ?? throw Error($"peripheral {name} is derived from {from}, which is not described before it.");
                    registers = [.. original.Registers.Select(register => register with
                    {
                        FullName = FullName(prefix, register.Name),
                        Address = baseAddress + (register.Address - original.Base),
                    })];
                    description ??= original.Description;
                }
                else
                {
                    registers = [];
                    foreach (var (registerName, registerNode) in peripheral["registers"] as JsonObject ?? [])
                    {
                        registers.Add(Register(name, prefix, baseAddress, size, registerName, registerNode));
                    }
                }

                read.Add(new ChipPeripheral(name, baseAddress, space, description, registers));
            }

            return read;
        }

        private static string FullName(string prefix, string name) => prefix.Length == 0 ? name : $"{prefix}_{name}";

        private ChipRegister Register(string peripheral, string prefix, ulong baseAddress, int width, string name, JsonNode? node)
        {
            if (node is not JsonObject register)
            {
                throw Error($"register {peripheral}.{name} must be an object.");
            }

            var address = register["address"] is { } absolute ? Number(absolute, $"{peripheral}.{name}.address")
                : register["offset"] is { } offset ? baseAddress + Number(offset, $"{peripheral}.{name}.offset")
                : throw Error($"register {peripheral}.{name} needs an \"offset\" (or an \"address\").");
            var size = register["size"] is { } s ? (int)Number(s, $"{peripheral}.{name}.size") : width;
            if (size is not (8 or 16 or 32 or 64))
            {
                throw Error($"register {peripheral}.{name} is {size} bits: 8, 16, 32 or 64.");
            }

            var access = Access(register["access"], $"{peripheral}.{name}", RegisterAccess.ReadWrite);
            var fields = new List<ChipField>();
            foreach (var (fieldName, fieldNode) in register["fields"] as JsonObject ?? [])
            {
                fields.Add(Field($"{peripheral}.{name}.{fieldName}", fieldName, fieldNode, access));
            }

            return new ChipRegister(name, FullName(prefix, name), address, size, access,
                register["reset"] is { } reset ? Number(reset, $"{peripheral}.{name}.reset") : null,
                Text(register["description"], "description"), [.. fields.OrderByDescending(f => f.Offset)]);
        }

        private ChipField Field(string where, string name, JsonNode? node, RegisterAccess access)
        {
            var (bits, field) = node switch
            {
                JsonObject o => (o["bits"], o),
                _ => (node, null),
            };
            var text = bits switch
            {
                JsonValue value when value.TryGetValue(out int bit) => bit.ToString(CultureInfo.InvariantCulture),
                JsonValue value when value.TryGetValue(out string? range) => range,
                _ => throw Error($"field {where}: its bits are a number (one bit) or \"msb:lsb\"."),
            };
            var match = Bits().Match(text);
            if (!match.Success)
            {
                throw Error($"field {where}: \"{text}\" is not a bit or \"msb:lsb\".");
            }

            var high = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var low = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : high;
            if (low > high)
            {
                throw Error($"field {where}: \"{text}\" has its highest bit first: \"{low}:{high}\".");
            }

            var values = new List<ChipFieldValue>();
            foreach (var (valueName, valueNode) in field?["values"] as JsonObject ?? [])
            {
                var value = valueNode is JsonObject v
                    ? new ChipFieldValue(valueName, Number(v["value"], $"{where}.{valueName}"), Text(v["description"], "description"))
                    : new ChipFieldValue(valueName, Number(valueNode, $"{where}.{valueName}"), null);
                if (value.Value >> (high - low + 1) != 0)
                {
                    throw Error($"field {where}: the value {valueName} ({value.Value}) does not fit in its {high - low + 1} bits.");
                }

                values.Add(value);
            }

            return new ChipField(name, low, high - low + 1, Access(field?["access"], where, access),
                Text(field?["description"], "description"), values);
        }

        private RegisterAccess Access(JsonNode? node, string where, RegisterAccess otherwise) => Text(node, "access") switch
        {
            null => otherwise,
            var text => Svd.ParseAccess(text) ?? throw Error($"{where}: \"access\" is \"{text}\": read-write, read-only, " +
                "write-only, writeOnce or read-writeOnce."),
        };

        public IReadOnlyList<ChipInterrupt> Interrupts(JsonObject? interrupts) =>
        [
            .. (interrupts ?? []).Select(pair => pair.Value is JsonObject i
                ? new ChipInterrupt(pair.Key, (int)Number(i["number"], $"interrupts.{pair.Key}.number"),
                    Text(i["peripheral"], "peripheral"), Text(i["description"], "description"),
                    i["request"] is JsonObject r
                        ? new ChipInterruptRequest(
                            Text(r["register"], "register") ?? throw Error($"interrupts.{pair.Key}.request needs a \"register\"."),
                            Text(r["enable"], "enable") ?? throw Error($"interrupts.{pair.Key}.request needs an \"enable\"."),
                            (int)Number(r["bit"], $"interrupts.{pair.Key}.request.bit"))
                        : null)
                : throw Error($"interrupts.{pair.Key} must be an object.")),
        ];

        public IReadOnlyList<ChipPin> Pins(JsonObject? pins)
        {
            var read = new List<ChipPin>();
            foreach (var (name, node) in pins ?? [])
            {
                var pin = node as JsonObject ?? throw Error($"pin {name} must be an object.");
                var named = PortPin().Match(name);
                var port = Text(pin["port"], "port") ?? (named.Success ? named.Groups[1].Value : null);
                int? bit = pin["bit"] is { } b ? (int)Number(b, $"pins.{name}.bit")
                    : named.Success ? named.Groups[2].Value[0] - '0' : null;
                var number = pin["number"] is { } n ? (int)Number(n, $"pins.{name}.number")
                    : port is { Length: 1 } && bit is { } at ? (port[0] - 'A') * 8 + at
                    : throw Error($"pin {name} needs a \"number\", or a port and a bit (PB5).");
                var functions = pin["functions"] is JsonArray list
                    ? list.Select(f => Text(f, $"pins.{name}.functions") ?? "").ToList()
                    : [];
                read.Add(new ChipPin(name, number, port, bit, functions));
            }

            if (read.GroupBy(pin => pin.Number).FirstOrDefault(group => group.Count() > 1) is { } twice)
            {
                throw Error($"pins {string.Join(" and ", twice.Select(p => p.Name))} have the same number, {twice.Key}.");
            }

            return read;
        }

        public IReadOnlyList<ChipPort> Ports(JsonObject? ports, IReadOnlyList<ChipPin> pins) =>
        [
            .. (ports ?? []).Select(pair => pair.Value is JsonObject port
                ? new ChipPort(pair.Key,
                    (port["registers"] as JsonObject ?? []).ToDictionary(r => r.Key,
                        r => Text(r.Value, $"ports.{pair.Key}.registers.{r.Key}") ?? ""),
                    port["bits"] is JsonArray bits
                        ? [.. bits.Select(b => (int)Number(b, $"ports.{pair.Key}.bits"))]
                        : [.. pins.Where(p => p.Port == pair.Key && p.Bit is not null).Select(p => p.Bit!.Value).Order()])
                : throw Error($"ports.{pair.Key} must be an object.")),
        ];

        public IReadOnlyList<ChipConstant> Constants(JsonObject? constants) =>
        [
            .. (constants ?? []).Select(pair => pair.Value is JsonObject c
                ? new ChipConstant(pair.Key, Number(c["value"], $"constants.{pair.Key}"), Text(c["description"], "description"))
                : new ChipConstant(pair.Key, Number(pair.Value, $"constants.{pair.Key}"), null)),
        ];
    }
}
