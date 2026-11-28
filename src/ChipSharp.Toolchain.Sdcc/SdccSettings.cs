using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Targets;

namespace ChipSharp.Toolchain.Sdcc;

/// <summary>
/// What the target file says about building a program with SDCC: the port ("sdcc.port", sdcc's -m, e.g. pdk14 or
/// stm8), more sdcc arguments ("sdcc.flags", e.g. --opt-code-size), and the "link" settings: the memory region of the
/// code, the constants and the starting values of the variables ("link.code"), counted in words when the region says
/// how many bytes of the .ihx a word takes ("memory.&lt;region&gt;.wordSize": 2 for a Padauk chip's 14-bit words), the
/// region of the variables and the stack ("link.data") and how much RAM the variables must leave for the stack
/// ("link.stack").
/// </summary>
/// <param name="Port">SDCC's name of the processor, e.g. pdk14: sdcc -mpdk14, and its assembler sdaspdk14.</param>
/// <param name="CodeWordSize">The bytes of the .ihx (and of the region's origin and size) one word of the code takes.</param>
public sealed record SdccSettings(
    string Port,
    IReadOnlyList<string> Flags,
    SdccRegion Code,
    SdccRegion Data,
    ulong StackSize,
    ulong CodeWordSize)
{
    /// <summary>
    /// The arguments that place the code: --code-loc at the code region's start when that is not 0 (an STM8's flash at
    /// 0x8000, where its interrupt vectors are). At 0 (a Padauk chip), SDCC's own layout keeps the reset and interrupt
    /// vectors at words 0 and 0x10 and puts the code after them, which --code-loc would move. The variables stay where
    /// SDCC puts them: from address 1 (STM8), or after its pseudo-register p (Padauk), so that none is at address 0,
    /// a null pointer; the stack is the chip's (an STM8's starts at the end of RAM, a Padauk's after the variables).
    /// </summary>
    public IReadOnlyList<string> PlacementFlags =>
        Code.Region.Origin == 0 ? [] : ["--code-loc", string.Create(CultureInfo.InvariantCulture, $"0x{Code.Region.Origin:x}")];

    /// <summary>True when the target is built with SDCC: it says which port ("sdcc.port").</summary>
    public static bool Applies(TargetDescription target) =>
        target.Settings["sdcc"] is JsonObject sdcc && sdcc["port"] is not null;

    /// <exception cref="ChipSharpException">A setting is missing or not what it must be; the message says which.</exception>
    public static SdccSettings Read(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ChipSharpException Error(string message) => new($"Target '{target.Name}': {message}");

        if (!target.IsComplete)
        {
            throw Error($"it is incomplete; still missing: {string.Join(", ", target.MissingValues)}.");
        }

        if (target.Settings["sdcc"] is not JsonObject sdcc)
        {
            throw Error("'sdcc' is missing; the toolchain \"sdcc\" needs SDCC's port, e.g. { \"sdcc\": { \"port\": \"stm8\" } }.");
        }

        var port = sdcc["port"] is JsonValue portValue && portValue.TryGetValue(out string? portText)
            && !string.IsNullOrWhiteSpace(portText) && portText.All(char.IsAsciiLetterOrDigit)
                ? portText
                : throw Error("'sdcc.port' must be SDCC's name of the processor (sdcc's -m), e.g. \"stm8\" or \"pdk14\".");

        IReadOnlyList<string> flags = sdcc["flags"] switch
        {
            null => [],
            JsonArray items when items.All(item => item is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                => [.. items.Select(item => item!.GetValue<string>())],
            _ => throw Error("'sdcc.flags' must be a list of text, e.g. [\"--opt-code-size\"]."),
        };

        if (target.Settings["link"] is not JsonObject link)
        {
            throw Error("'link' is missing; it names the memory regions of the code and the variables, e.g. " +
                "{ \"code\": \"flash\", \"data\": \"ram\" }.");
        }

        SdccRegion Region(string key)
        {
            var name = link[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s)
                ? s
                : throw Error($"'link.{key}' must name a memory region, e.g. \"{(key == "code" ? "flash" : "ram")}\".");
            return target.Memory.TryGetValue(name, out var region)
                ? new SdccRegion(name, region)
                : throw Error($"'link.{key}' is \"{name}\", but the target has no memory region of that name" +
                    (target.Memory.Count == 0 ? "; build for a chip or board that gives them." : $" (it has: {string.Join(", ", target.Memory.Keys)})."));
        }

        var code = Region("code");
        var data = Region("data");
        var stack = Size(link["stack"], "link.stack", 0, Error);
        if (stack >= data.Region.Size)
        {
            throw Error($"'link.stack' is {MemorySize.Text(stack)}, which leaves nothing of the " +
                $"{MemorySize.Text(data.Region.Size)} of {data.Name} ('link.data') for the variables.");
        }

        var wordSize = Size(target.Settings["memory"]?[code.Name]?["wordSize"], $"memory.{code.Name}.wordSize", 1, Error);
        if (wordSize is not (1 or 2 or 4) || code.Region.Origin % wordSize != 0 || code.Region.Size % wordSize != 0)
        {
            throw Error($"'memory.{code.Name}.wordSize' is {wordSize}; it must be 1, 2 or 4 (the bytes of the .ihx a " +
                "word takes), and the region's origin and size whole words.");
        }

        return new SdccSettings(port, flags, code, data, stack, wordSize);
    }

    private static ulong Size(JsonNode? node, string path, ulong fallback, Func<string, ChipSharpException> error)
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
                    throw error($"'{path}' is \"{text}\"; write it like 16, 256 or 0x100.");
                }
            default:
                throw error($"'{path}' must be a number or text like 256 or 0x100.");
        }
    }
}

/// <summary>A memory region with the name the target gives it, e.g. flash.</summary>
public sealed record SdccRegion(string Name, MemoryRegion Region);
