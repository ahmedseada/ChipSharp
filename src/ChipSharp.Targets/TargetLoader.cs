using System.Text.Json;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;

namespace ChipSharp.Targets;

/// <summary>
/// Loads targets from a folder of JSON files (any layout; by convention arch/, chip/ and board/).
/// A file is found by its name: pico.json is the target "pico". A target may <c>"inherits"</c> one
/// other target; the child's values win, and nested objects (memory, capabilities, ...) merge key by key.
/// Comments and trailing commas are allowed in the files.
/// </summary>
public sealed class TargetLoader
{
    /// <summary>The environment variable that points at the targets folder.</summary>
    public const string EnvironmentVariable = "CHIPSHARP_TARGETS";

    private static readonly JsonDocumentOptions FileOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _root;
    private readonly Dictionary<string, string> _files;

    public TargetLoader(string targetsFolder)
    {
        if (!Directory.Exists(targetsFolder))
        {
            throw new ChipSharpException($"Targets folder '{targetsFolder}' does not exist.");
        }

        _root = Path.GetFullPath(targetsFolder);
        _files = IndexFiles(_root);
    }

    /// <summary>Every target name in the folder, sorted.</summary>
    public IReadOnlyList<string> Names => [.. _files.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Loads a target and everything it inherits, merged and checked.</summary>
    public TargetDescription Load(string name)
    {
        var chain = new List<string>();
        var merged = LoadMerged(name, chain);
        return TargetParser.Parse(merged, chain);
    }

    private JsonObject LoadMerged(string name, List<string> chain)
    {
        if (chain.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new ChipSharpException(
                $"Target '{chain[0]}' has an inheritance loop: {string.Join(" -> ", chain)} -> {name}.");
        }

        if (!_files.TryGetValue(name, out var path))
        {
            var inheritedBy = chain.Count > 0 ? $" ('{chain[^1]}' inherits it.)" : "";
            throw new ChipSharpException(
                $"Target '{name}' not found in '{_root}'.{inheritedBy} Known targets: {string.Join(", ", Names)}.");
        }

        var canonicalName = Path.GetFileNameWithoutExtension(path);
        chain.Add(canonicalName);
        var own = ReadFile(canonicalName, path);

        var parentName = own["inherits"] switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) => s,
            _ => throw new ChipSharpException($"Target file '{path}': \"inherits\" must be the name of one target."),
        };
        own.Remove("inherits");

        return parentName is null ? own : Merge(LoadMerged(parentName, chain), own);
    }

    /// <summary>A copy of <paramref name="parent"/> with <paramref name="child"/> laid over it.</summary>
    internal static JsonObject Merge(JsonObject parent, JsonObject child)
    {
        var result = parent.DeepClone().AsObject();
        foreach (var (key, value) in child)
        {
            result[key] = value is JsonObject childObject && result[key] is JsonObject parentObject
                ? Merge(parentObject, childObject)
                : value?.DeepClone();
        }

        return result;
    }

    private static JsonObject ReadFile(string name, string path)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path), documentOptions: FileOptions);
        }
        catch (JsonException e)
        {
            throw new ChipSharpException(
                $"Target file '{path}' is not valid JSON (line {e.LineNumber + 1}): {e.Message}", e);
        }

        if (node is not JsonObject target)
        {
            throw new ChipSharpException($"Target file '{path}' must contain a JSON object.");
        }

        switch (target["name"])
        {
            case null:
                break;
            case JsonValue v when v.TryGetValue(out string? s) && string.Equals(s, name, StringComparison.OrdinalIgnoreCase):
                break;
            default:
                throw new ChipSharpException(
                    $"Target file '{path}' says \"name\": {target["name"]!.ToJsonString()}, " +
                    $"but its file name says '{name}'. They must match.");
        }

        target["name"] = name;
        return target;
    }

    private static Dictionary<string, string> IndexFiles(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!files.TryAdd(name, path))
            {
                throw new ChipSharpException(
                    $"Two target files are named '{name}': '{files[name]}' and '{path}'. Target names must be unique.");
            }
        }

        return files;
    }
}
