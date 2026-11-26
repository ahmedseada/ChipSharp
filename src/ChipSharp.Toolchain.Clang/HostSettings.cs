using System.Text.Json.Nodes;
using ChipSharp.Abstractions;

namespace ChipSharp.Toolchain.Clang;

/// <summary>
/// What the target file says about building a PC program: the triple, "clang.flags" and the "link" settings
/// (entry point, subsystem, and the DLL functions to make import libraries for).
/// </summary>
internal sealed record HostSettings(
    string Triple,
    IReadOnlyList<string> ClangFlags,
    string Entry,
    string Subsystem,
    string? Machine,
    IReadOnlyList<DllImports> Imports)
{
    public static HostSettings Read(TargetDescription target)
    {
        ChipSharpException Error(string message) => new($"Target '{target.Name}': {message}");

        if (target.Settings["link"] is not JsonObject link)
        {
            throw Error(
                "it has no 'link' settings, so the clang toolchain does not know how to link for it. A PC target " +
                "needs 'link.entry' and 'link.subsystem' (see host.json); a chip target needs 'link.entry', and " +
                "'link.code' and 'link.data' naming its memory regions (see armv6m.json).");
        }

        if (!target.IsComplete)
        {
            throw Error($"it is incomplete; still missing: {string.Join(", ", target.MissingValues)}.");
        }

        string Text(JsonObject owner, string key, string path) => owner[key] switch
        {
            null => throw Error($"'{path}' is missing."),
            JsonValue v when v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) => s,
            _ => throw Error($"'{path}' must be text."),
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

        var triple = string.IsNullOrWhiteSpace(target.Triple)
            ? throw Error("'triple' is missing; clang needs it, e.g. \"x86_64-pc-windows-msvc\".")
            : target.Triple;

        var clangFlags = target.Settings["clang"] switch
        {
            null => [],
            JsonObject clang => TextList(clang["flags"], "clang.flags"),
            _ => throw Error("'clang' must be an object, e.g. { \"flags\": [\"-O2\"] }."),
        };

        var imports = new List<DllImports>();
        switch (link["imports"])
        {
            case null:
                break;
            case JsonObject dlls:
                foreach (var (dll, functions) in dlls)
                {
                    imports.Add(new DllImports(dll, TextList(functions, $"link.imports.{dll}")));
                }

                break;
            default:
                throw Error(
                    "'link.imports' must map each DLL to its functions, e.g. { \"kernel32.dll\": [\"ExitProcess\"] }.");
        }

        var machine = link["machine"] is null ? null : Text(link, "machine", "link.machine");
        if (imports.Count > 0 && machine is null)
        {
            throw Error(
                "'link.machine' is missing; llvm-dlltool needs it to make the import libraries, e.g. \"i386:x86-64\".");
        }

        return new HostSettings(
            triple,
            clangFlags,
            Text(link, "entry", "link.entry"),
            Text(link, "subsystem", "link.subsystem"),
            machine,
            imports);
    }
}

/// <summary>The functions a program calls in one DLL, e.g. kernel32.dll: GetStdHandle, WriteFile, ExitProcess.</summary>
internal sealed record DllImports(string Dll, IReadOnlyList<string> Functions);
