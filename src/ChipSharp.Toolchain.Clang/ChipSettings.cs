using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Targets;

namespace ChipSharp.Toolchain.Clang;

/// <summary>
/// What the target file says about building a program for a chip (no operating system): the triple and "cpu",
/// "clang.flags", and the "link" settings: the entry point, which memory region holds the code ("link.code") and
/// which the variables and the stack ("link.data"), how much RAM the stack needs ("link.stack"), the vector table's
/// alignment ("link.vectorAlign"), sections that must come first in the code region ("link.head") and more linker
/// arguments ("link.flags").
/// </summary>
internal sealed record ChipSettings(
    string Triple,
    string? Cpu,
    IReadOnlyList<string> ClangFlags,
    string Entry,
    NamedRegion Code,
    NamedRegion Data,
    ulong StackSize,
    ulong VectorAlign,
    IReadOnlyList<HeadSection> Head,
    IReadOnlyList<string> LinkFlags)
{
    /// <summary>True when the target is a chip: its "link" settings name the memory region for the code.</summary>
    public static bool Applies(TargetDescription target) =>
        target.Settings["link"] is JsonObject link && link["code"] is not null;

    public static ChipSettings Read(TargetDescription target)
    {
        ChipSharpException Error(string message) => new($"Target '{target.Name}': {message}");

        if (!target.IsComplete)
        {
            throw Error($"it is incomplete; still missing: {string.Join(", ", target.MissingValues)}.");
        }

        var link = (JsonObject)target.Settings["link"]!;

        string Text(string key) => link[key] switch
        {
            null => throw Error($"'link.{key}' is missing."),
            JsonValue v when v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) => s,
            _ => throw Error($"'link.{key}' must be text."),
        };

        IReadOnlyList<string> TextList(JsonNode? node, string path) => node switch
        {
            null => [],
            JsonArray items => [.. items.Select(item =>
                item is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s)
                    ? s
                    : throw Error($"'{path}' must be a list of text, e.g. [\"a\", \"b\"]."))],
            _ => throw Error($"'{path}' must be a list of text, e.g. [\"a\", \"b\"]."),
        };

        ulong Size(JsonNode? node, string path, ulong fallback)
        {
            switch (node)
            {
                case null:
                    return fallback;
                case JsonValue v when v.GetValueKind() == JsonValueKind.Number
                    && ulong.TryParse(v.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number):
                    return number;
                case JsonValue v when v.TryGetValue(out string? text):
                    try
                    {
                        return SizeParser.Parse(text);
                    }
                    catch (Exception e) when (e is FormatException or OverflowException)
                    {
                        throw Error($"'{path}' is \"{text}\"; write it like 8K, 256, 0x100.");
                    }
                default:
                    throw Error($"'{path}' must be a number or text like 8K or 0x100.");
            }
        }

        NamedRegion Region(string key)
        {
            var name = Text(key);
            if (target.Memory.Count == 0)
            {
                throw Error($"it has no memory regions, so there is nowhere to put the program ('link.{key}' is " +
                    $"\"{name}\"). Build for a chip or board that gives them, e.g. 'pico'.");
            }

            return target.Memory.TryGetValue(name, out var region)
                ? new NamedRegion(name, region)
                : throw Error($"'link.{key}' is \"{name}\", but the target has no memory region of that name " +
                    $"(it has: {string.Join(", ", target.Memory.Keys)}).");
        }

        var triple = string.IsNullOrWhiteSpace(target.Triple)
            ? throw Error("'triple' is missing; clang needs it, e.g. \"thumbv6m-none-eabi\".")
            : target.Triple;

        var clangFlags = target.Settings["clang"] switch
        {
            null => [],
            JsonObject clang => TextList(clang["flags"], "clang.flags"),
            _ => throw Error("'clang' must be an object, e.g. { \"flags\": [\"-O2\"] }."),
        };

        var head = new List<HeadSection>();
        switch (link["head"])
        {
            case null:
                break;
            case JsonArray sections:
                foreach (var section in sections)
                {
                    if (section is not JsonObject entry
                        || entry["section"] is not JsonValue nameValue || !nameValue.TryGetValue(out string? name)
                        || string.IsNullOrWhiteSpace(name) || entry["size"] is null)
                    {
                        throw Error("'link.head' must be a list of { \"section\": \".name\", \"size\": 256 }.");
                    }

                    head.Add(new HeadSection(name, Size(entry["size"], $"link.head size of {name}", 0)));
                }

                break;
            default:
                throw Error("'link.head' must be a list of { \"section\": \".name\", \"size\": 256 }.");
        }

        var vectorAlign = Size(link["vectorAlign"], "link.vectorAlign", 4);
        if (vectorAlign == 0 || (vectorAlign & (vectorAlign - 1)) != 0)
        {
            throw Error($"'link.vectorAlign' is {vectorAlign}; it must be a power of two, e.g. 256.");
        }

        var entryPoint = Text("entry");
        var code = Region("code");
        var data = Region("data");
        var stack = Size(link["stack"], "link.stack", 0);
        if (stack > data.Region.Size)
        {
            throw Error($"'link.stack' is {MemorySize.Text(stack)}, more than the {MemorySize.Text(data.Region.Size)} " +
                $"of {data.Name} ('link.data') it is kept in.");
        }

        return new ChipSettings(
            triple,
            string.IsNullOrWhiteSpace(target.Cpu) ? null : target.Cpu,
            clangFlags,
            entryPoint,
            code,
            data,
            stack,
            vectorAlign,
            head,
            TextList(link["flags"], "link.flags"));
    }
}

/// <summary>A memory region with the name the target gives it, e.g. flash.</summary>
internal sealed record NamedRegion(string Name, MemoryRegion Region);

/// <summary>A section placed at the start of the code region, which must be exactly <paramref name="Size"/> bytes.</summary>
internal sealed record HeadSection(string Name, ulong Size);
