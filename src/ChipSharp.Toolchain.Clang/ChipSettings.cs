using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Targets;

namespace ChipSharp.Toolchain.Clang;

/// <summary>
/// What the target file says about building a program for a chip (no operating system): the triple, "cpu" and (AVR)
/// "mmcu", (RISC-V) "march" and "mabi", "clang.flags", Zig's name of the target ("zig.target", for the toolchain "zig"),
/// and the "link" settings: the entry point, which memory region holds the code ("link.code") and which the variables and
/// the stack ("link.data": the same region on a chip that runs from RAM, as the ESP32-C5 does), how much RAM the stack
/// needs ("link.stack"), the vector table's alignment ("link.vectorAlign"),
/// sections that must come first in the code region ("link.head"), more linker arguments ("link.flags") and what the
/// .elf adds to the data region's addresses ("link.dataOffset"); and from the capabilities, how much RAM the heap
/// takes ("heap", 0 for none).
/// </summary>
/// <param name="Mmcu">The AVR microcontroller, as avr-gcc and clang name it (-mmcu), e.g. atmega328p; null elsewhere.</param>
/// <param name="March">
/// The instruction set, as clang's -march names it ("march"): a RISC-V chip's base and extensions, e.g.
/// rv32imac_zicsr_zifencei; null elsewhere.
/// </param>
/// <param name="Mabi">The ABI, as clang's -mabi names it ("mabi"): RISC-V's ilp32 (no float registers); null elsewhere.</param>
/// <param name="ZigTarget">Zig's name of the target (zig cc's --target), e.g. avr-freestanding-none; null when not given.</param>
/// <param name="DataOffset">
/// What the .elf adds to the data region's addresses ("link.dataOffset"): 0x800000 on AVR, whose RAM and flash are two
/// address spaces that both start at 0 (binutils' convention, which ld.lld follows); 0 elsewhere.
/// </param>
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
    IReadOnlyList<string> LinkFlags,
    ulong HeapSize = 0,
    string? Mmcu = null,
    string? ZigTarget = null,
    ulong DataOffset = 0,
    string? March = null,
    string? Mabi = null)
{
    /// <summary>True for an AVR chip (the triple's processor is avr): the linker script lays it out as avr-gcc's does.</summary>
    public bool IsAvr => Triple.Split('-')[0].Equals("avr", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The flags that pick the processor, after --target: -mcpu (the target's "cpu"), for an AVR -mmcu (its "mmcu"), for a
    /// RISC-V chip -march and -mabi (its "march" and "mabi": its extensions, as LLVM has no -mcpu for the ESP32-C5's core).
    /// zig cc needs both for an AVR: it takes the processor from -mcpu alone (given only -mmcu, it builds for a generic
    /// avr2, without the multiply and the jmp and call instructions), and clang warns "no target microcontroller
    /// specified" without -mmcu. So with Zig an AVR's -mcpu is its "mmcu" when it gives no "cpu": LLVM names its AVR
    /// processors after the microcontrollers. The flags are part of every object's key in the build cache.
    /// </summary>
    public IReadOnlyList<string> ProcessorFlags(ClangDriver driver)
    {
        var cpu = Cpu ?? (driver == ClangDriver.Zig ? Mmcu : null);
        List<string> flags = [];
        if (cpu is not null)
        {
            flags.Add($"-mcpu={cpu}");
        }

        if (Mmcu is not null)
        {
            flags.Add($"-mmcu={Mmcu}");
        }

        if (March is not null)
        {
            flags.Add($"-march={March}");
        }

        if (Mabi is not null)
        {
            flags.Add($"-mabi={Mabi}");
        }

        return flags;
    }

    /// <summary>True for a RISC-V chip (the triple's processor is riscv32 or riscv64).</summary>
    public bool IsRiscV => Triple.StartsWith("riscv", StringComparison.OrdinalIgnoreCase);

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

        var dataOffset = Size(link["dataOffset"], "link.dataOffset", 0);
        if (dataOffset > uint.MaxValue - data.Region.End)
        {
            throw Error($"'link.dataOffset' is 0x{dataOffset:x}: with it, {data.Name} ('link.data') would end past the " +
                "4 GB a 32-bit .elf can address.");
        }

        string? OptionalText(JsonNode? node, string path) => node switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) => s,
            _ => throw Error($"'{path}' must be text."),
        };

        var mmcu = OptionalText(target.Settings["mmcu"], "mmcu");
        var zigTarget = target.Settings["zig"] switch
        {
            null => null,
            JsonObject zig => OptionalText(zig["target"], "zig.target"),
            _ => throw Error("'zig' must be an object, e.g. { \"target\": \"avr-freestanding-none\" }."),
        };

        var heap = target.HeapSize ?? 0;
        if (heap > data.Region.Size - stack)
        {
            throw Error($"the heap ('capabilities.heap', {MemorySize.Text(heap)}) and the stack ('link.stack', " +
                $"{MemorySize.Text(stack)}) need more than the {MemorySize.Text(data.Region.Size)} of {data.Name} " +
                "('link.data') they are kept in.");
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
            TextList(link["flags"], "link.flags"),
            heap,
            mmcu,
            zigTarget,
            dataOffset,
            OptionalText(target.Settings["march"], "march"),
            OptionalText(target.Settings["mabi"], "mabi"));
    }
}

/// <summary>A memory region with the name the target gives it, e.g. flash.</summary>
internal sealed record NamedRegion(string Name, MemoryRegion Region);

/// <summary>A section placed at the start of the code region, which must be exactly <paramref name="Size"/> bytes.</summary>
internal sealed record HeadSection(string Name, ulong Size);
