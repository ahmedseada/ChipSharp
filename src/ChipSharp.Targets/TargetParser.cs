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
            Capabilities = ParseValues(json["capabilities"], "capabilities", Error),
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
