using System.Text.Json;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;

namespace ChipSharp.Targets;

/// <summary>Checks a merged target file and turns it into a <see cref="TargetDescription"/>.</summary>
internal static class TargetParser
{
    public static TargetDescription Parse(JsonObject json, IReadOnlyList<string> chain)
    {
        var where = $"Target '{chain[0]}' ({string.Join(" -> ", chain)})";
        ChipSharpException Error(string message) => new($"{where}: {message}");

        string? OptionalText(string key) => json[key] switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out string? s) => s,
            _ => throw Error($"'{key}' must be text."),
        };

        string RequiredText(string key) => OptionalText(key) ?? throw Error($"'{key}' is missing.");

        var pointerSize = json["pointerSize"] switch
        {
            null => throw Error("'pointerSize' is missing."),
            JsonValue v when v.TryGetValue(out int n) && n is 2 or 4 or 8 => n,
            _ => throw Error("'pointerSize' must be 2, 4 or 8."),
        };

        var endian = RequiredText("endian").ToLowerInvariant() switch
        {
            "little" => Endianness.Little,
            "big" => Endianness.Big,
            _ => throw Error("'endian' must be \"little\" or \"big\"."),
        };

        var missing = new List<string>();
        var memory = ParseMemory(json["memory"], missing, Error);
        var capabilities = ParseValues(json["capabilities"], "capabilities", Error);

        return new TargetDescription
        {
            Name = chain[0],
            InheritanceChain = [.. chain],
            Backend = RequiredText("backend"),
            Toolchain = OptionalText("toolchain"),
            Runtime = OptionalText("runtime"),
            Triple = OptionalText("triple"),
            Cpu = OptionalText("cpu"),
            PointerSize = pointerSize,
            Endian = endian,
            Image = OptionalText("image"),
            Flasher = OptionalText("flasher"),
            Memory = memory,
            Pins = ParseValues(json["pins"], "pins", Error),
            Capabilities = capabilities,
            HeapSize = ParseHeap(json, memory, Error),
            Settings = json.DeepClone().AsObject(),
            MissingValues = missing,
        };
    }

    /// <summary>
    /// Reads the memory regions. A region without an origin or a size is left out and its missing
    /// value listed: a target that inherits this one may complete it (a chip's flash size comes from the board).
    /// </summary>
    private static Dictionary<string, MemoryRegion> ParseMemory(JsonNode? node, List<string> missing,
        Func<string, ChipSharpException> error)
    {
        var regions = new Dictionary<string, MemoryRegion>(StringComparer.OrdinalIgnoreCase);
        if (node is null)
        {
            return regions;
        }

        if (node is not JsonObject memory)
        {
            throw error("'memory' must be an object of regions, e.g. { \"flash\": { \"origin\": ..., \"size\": ... } }.");
        }

        foreach (var (name, value) in memory)
        {
            if (value is not JsonObject region)
            {
                throw error($"'memory.{name}' must be an object with 'origin' and 'size'.");
            }

            var origin = Size(region["origin"], $"memory.{name}.origin", missing, error);
            var size = Size(region["size"], $"memory.{name}.size", missing, error);
            if (origin is not null && size is not null)
            {
                regions[name] = new MemoryRegion(origin.Value, size.Value);
            }
        }

        return regions;
    }

    /// <summary>
    /// The heap's budget, the capability "heap": false (or not given) for none, else the most the heap may take, e.g.
    /// "224K". With the stack ("link.stack") it must leave room for the static fields in the RAM ("link.data"'s region).
    /// </summary>
    private static ulong? ParseHeap(JsonObject json, Dictionary<string, MemoryRegion> memory,
        Func<string, ChipSharpException> error)
    {
        const string Key = "capabilities.heap";
        var node = json["capabilities"]?["heap"];
        var kind = node?.GetValueKind();
        if (kind is null or JsonValueKind.False)
        {
            return null;
        }

        var budget = kind is JsonValueKind.String or JsonValueKind.Number ? Size(node, Key, [], error) : null;
        if (budget is null or 0)
        {
            throw error($"'{Key}' must be false (no heap) or the most the heap may take, e.g. \"224K\".");
        }

        var link = json["link"] as JsonObject;
        var dataName = link?["data"] is JsonValue data && data.TryGetValue(out string? name) ? name : "ram";
        if (!memory.TryGetValue(dataName, out var ram))
        {
            return budget;
        }

        var stack = link?["stack"] is { } stackNode ? Size(stackNode, "link.stack", [], error) ?? 0 : 0;
        return budget.Value + stack < ram.Size
            ? budget
            : throw error($"'{Key}' is {MemorySize.Text(budget.Value)}, and with the stack ({MemorySize.Text(stack)}, " +
                $"link.stack) that leaves nothing of the {MemorySize.Text(ram.Size)} of {dataName} for the static " +
                "fields: make the heap smaller.");
    }

    private static ulong? Size(JsonNode? node, string path, List<string> missing,
        Func<string, ChipSharpException> error)
    {
        switch (node)
        {
            case null:
                missing.Add(path);
                return null;
            case JsonValue v when v.TryGetValue(out ulong number):
                return number;
            case JsonValue v when v.TryGetValue(out string? text):
                try
                {
                    return SizeParser.Parse(text);
                }
                catch (Exception e) when (e is FormatException or OverflowException)
                {
                    throw error($"'{path}' is \"{text}\"; write it like 2M, 264K, 0x10000000 or 4096.");
                }
            default:
                throw error($"'{path}' must be a number or text like 2M, 264K or 0x10000000.");
        }
    }

    private static Dictionary<string, string> ParseValues(JsonNode? node, string key,
        Func<string, ChipSharpException> error)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is null)
        {
            return values;
        }

        if (node is not JsonObject obj)
        {
            throw error($"'{key}' must be an object of name/value pairs.");
        }

        foreach (var (name, value) in obj)
        {
            values[name] = value is JsonValue single
                ? single.ToString()
                : throw error($"'{key}.{name}' must be a single value (text, number, true or false).");
        }

        return values;
    }
}
