using System.Diagnostics.CodeAnalysis;

namespace ChipSharp.Abstractions;

/// <summary>
/// Holds every pluggable part (code generators, toolchains, image formats, flashers, architecture runtimes)
/// by name. A target names its parts as text; asking for a name nobody registered fails loudly.
/// Names are not case-sensitive.
/// </summary>
public sealed class ComponentRegistry
{
    private static readonly Dictionary<Type, string> KindNames = new()
    {
        [typeof(ICodeGenerator)] = "code generator",
        [typeof(IToolchain)] = "toolchain",
        [typeof(IImageFormat)] = "image format",
        [typeof(IFlasher)] = "flasher",
        [typeof(IArchRuntime)] = "architecture runtime",
    };

    private readonly Dictionary<Type, Dictionary<string, INamedComponent>> _byKind = [];

    /// <summary>
    /// Registers a part under every ChipSharp contract it implements
    /// (a class that is both a toolchain and a flasher is found as either).
    /// </summary>
    public ComponentRegistry Register(INamedComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        var kinds = component.GetType().GetInterfaces()
            .Where(i => i != typeof(INamedComponent) && typeof(INamedComponent).IsAssignableFrom(i))
            .ToList();
        if (kinds.Count == 0)
        {
            throw new ChipSharpException(
                $"'{component.GetType().Name}' implements no ChipSharp contract (code generator, toolchain, ...).");
        }

        if (string.IsNullOrWhiteSpace(component.Name))
        {
            throw new ChipSharpException($"'{component.GetType().Name}' has no name.");
        }

        foreach (var kind in kinds)
        {
            if (Items(kind).ContainsKey(component.Name))
            {
                throw new ChipSharpException($"{Capitalized(kind)} '{component.Name}' is already registered.");
            }
        }

        foreach (var kind in kinds)
        {
            Items(kind)[component.Name] = component;
        }

        return this;
    }

    /// <summary>Returns the part registered under <paramref name="name"/>, or fails with the names that are known.</summary>
    public T Get<T>(string name) where T : class, INamedComponent
    {
        if (TryGet<T>(name, out var component))
        {
            return component;
        }

        var known = Names<T>();
        var hint = known.Count == 0
            ? $"No {KindName(typeof(T))}s are registered."
            : $"Registered: {string.Join(", ", known)}.";
        throw new ChipSharpException($"{Capitalized(typeof(T))} '{name}' is not registered. {hint}");
    }

    public bool TryGet<T>(string name, [NotNullWhen(true)] out T? component) where T : class, INamedComponent
    {
        if (_byKind.TryGetValue(typeof(T), out var items) && items.TryGetValue(name, out var found))
        {
            component = (T)found;
            return true;
        }

        component = null;
        return false;
    }

    /// <summary>The names registered for one kind of part, sorted.</summary>
    public IReadOnlyList<string> Names<T>() where T : class, INamedComponent =>
        _byKind.TryGetValue(typeof(T), out var items)
            ? [.. items.Keys.Order(StringComparer.OrdinalIgnoreCase)]
            : [];

    private Dictionary<string, INamedComponent> Items(Type kind)
    {
        if (!_byKind.TryGetValue(kind, out var items))
        {
            items = new Dictionary<string, INamedComponent>(StringComparer.OrdinalIgnoreCase);
            _byKind[kind] = items;
        }

        return items;
    }

    private static string KindName(Type kind) => KindNames.GetValueOrDefault(kind, kind.Name);

    private static string Capitalized(Type kind)
    {
        var name = KindName(kind);
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
