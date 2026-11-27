using System.Globalization;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// How the target rules' messages name a target's pins: by number as "GPIO 6" (the Pico's), or, on a target whose file
/// names them (<c>"pinNames": ["D0", ..., "A5"]</c>, the Uno's, by the numbers programs use), as "pin 18 (A4)".
/// </summary>
internal static class PinNames
{
    /// <summary>"GPIO 6", or "pin 18 (A4)".</summary>
    public static string Of(TargetDescription target, int pin) =>
        Name(Names(target), pin) is { } name ? $"pin {Text(pin)} ({name})" : $"GPIO {Text(pin)}";

    /// <summary>
    /// The pins, in order, a run of three or more as one, and <paramref name="last"/> ("or", "and") before the last
    /// part: "GPIO 4, 8, 20 or 24", "GPIO 0 to 29"; "pin 1 (D1)", "pins 3 (D3), 5 (D5) or 6 (D6)", "pins 14 to 19 (A0
    /// to A5)".
    /// </summary>
    public static string List(TargetDescription target, IReadOnlyList<int> pins, string last)
    {
        var names = Names(target);
        var parts = new List<string>();
        for (var i = 0; i < pins.Count;)
        {
            var end = i;
            while (end + 1 < pins.Count && pins[end + 1] == pins[end] + 1)
            {
                end++;
            }

            if (end - i >= 2)
            {
                parts.Add(Name(names, pins[i]) is { } first && Name(names, pins[end]) is { } final
                    ? $"{Text(pins[i])} to {Text(pins[end])} ({first} to {final})"
                    : $"{Text(pins[i])} to {Text(pins[end])}");
            }
            else
            {
                parts.AddRange(pins.Skip(i).Take(end - i + 1).Select(pin =>
                    Name(names, pin) is { } name ? $"{Text(pin)} ({name})" : Text(pin)));
            }

            i = end + 1;
        }

        var text = parts.Count switch
        {
            0 => "none",
            1 => parts[0],
            _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} {last} {parts[^1]}",
        };
        return names is null ? $"GPIO {text}" : pins.Count == 1 ? $"pin {text}" : $"pins {text}";
    }

    /// <summary>The target's "pinNames", by pin number; null when it gives none.</summary>
    private static IReadOnlyList<string>? Names(TargetDescription target) =>
        target.Settings["pinNames"] is JsonArray list
            ? [.. list.Select(item => item is JsonValue value && value.TryGetValue(out string? name) ? name : "")]
            : null;

    private static string? Name(IReadOnlyList<string>? names, int pin) =>
        names is not null && pin >= 0 && pin < names.Count && names[pin].Length > 0 ? names[pin] : null;

    private static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);
}
