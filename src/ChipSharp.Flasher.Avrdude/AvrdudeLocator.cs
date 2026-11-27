using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;

namespace ChipSharp.Flasher.Avrdude;

/// <summary>avrdude itself, and the configuration file (avrdude.conf) to give it when it would not find its own.</summary>
public sealed record AvrdudeTool(string Path, string? ConfigFile);

/// <summary>
/// Finds avrdude: the environment variable CHIPSHARP_AVRDUDE (avrdude.exe or its folder, which must hold it), else
/// avrdude on the PATH, else the copy an Arduino IDE installed: the IDE 2's (and the 1.8 boards manager's) in
/// Arduino15\packages\arduino\tools\avrdude\&lt;version&gt;\bin, the newest version first; the Microsoft Store IDE's in
/// Documents\ArduinoData; the IDE 1.8's own in Arduino\hardware\tools\avr\bin under Program Files. Those copies keep
/// avrdude.conf in the etc folder next to bin, where some avrdude versions do not look: then it is given with -C.
/// </summary>
internal sealed partial class AvrdudeLocator(Func<string, string?> environment)
{
    private const string ConfigName = "avrdude.conf";

    private static readonly string ExeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";

    /// <summary>What to do when avrdude is missing.</summary>
    internal static string Advice =>
        "avrdude uploads a program through an Arduino's USB port, to its bootloader. Get it with the Arduino IDE " +
        "(https://www.arduino.cc/en/software: it installs avrdude with the \"Arduino AVR Boards\", which it adds for an " +
        "Uno, and ChipSharp finds it there), or download avrdude itself (https://github.com/avrdudes/avrdude/releases, " +
        $"for Windows the windows-x64 zip), unpack it and set {AvrdudeFlasher.AvrdudeVariable} to its avrdude{ExeSuffix}, " +
        "or put its folder on the PATH.";

    public AvrdudeTool Find()
    {
        var file = "avrdude" + ExeSuffix;
        if (Variable(AvrdudeFlasher.AvrdudeVariable) is { } set)
        {
            // a folder is taken to mean the avrdude in it
            var fullPath = Path.GetFullPath(Directory.Exists(set) ? Path.Combine(set, file) : set);
            return File.Exists(fullPath)
                ? Tool(fullPath)
                : throw new ChipSharpException(
                    $"avrdude was not found at '{fullPath}' (set by the environment variable " +
                    $"{AvrdudeFlasher.AvrdudeVariable}). {Advice}");
        }

        var searchPath = (environment("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), file));
        var places = ArduinoPlaces();
        foreach (var candidate in searchPath.Concat(places.SelectMany(place => Copies(place, file))))
        {
            if (File.Exists(candidate))
            {
                return Tool(Path.GetFullPath(candidate));
            }
        }

        var looked = places.Count == 0 ? "" : $" and in the Arduino IDE's folders ({string.Join(", ", places.Select(p => $"'{p.Folder}'"))})";
        throw new ChipSharpException($"avrdude was not found (looked on the PATH{looked}). {Advice}");
    }

    /// <summary>
    /// Where an Arduino IDE keeps avrdude, from this computer's folders (read through the environment, so that a test
    /// can move them): each a folder of versions (tools\avrdude, with &lt;version&gt;\bin in it) or a bin folder itself.
    /// </summary>
    private List<(string Folder, bool HasVersions)> ArduinoPlaces()
    {
        var places = new List<(string, bool)>();
        void Versions(string? root, params string[] parts)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                places.Add((Path.Combine([root.Trim('"'), .. parts, "packages", "arduino", "tools", "avrdude"]), true));
            }
        }

        void Bin(string? root)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                places.Add((Path.Combine(root.Trim('"'), "Arduino", "hardware", "tools", "avr", "bin"), false));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            Versions(environment("LOCALAPPDATA"), "Arduino15");
            Versions(environment("USERPROFILE"), "Documents", "ArduinoData");
            Bin(environment("ProgramFiles(x86)"));
            Bin(environment("ProgramFiles"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            Versions(environment("HOME"), "Library", "Arduino15");
        }
        else
        {
            Versions(environment("HOME"), ".arduino15");
        }

        return places;
    }

    /// <summary>The avrdude files of one place, the newest version first.</summary>
    private static IEnumerable<string> Copies((string Folder, bool HasVersions) place, string file)
    {
        if (!place.HasVersions)
        {
            return [Path.Combine(place.Folder, file)];
        }

        if (!Directory.Exists(place.Folder))
        {
            return [];
        }

        return Directory.GetDirectories(place.Folder)
            .OrderByDescending(folder => Path.GetFileName(folder), VersionComparer.Instance)
            .Select(folder => Path.Combine(folder, "bin", file));
    }

    /// <summary>avrdude, with ../etc/avrdude.conf when that is there and no avrdude.conf is next to it.</summary>
    private static AvrdudeTool Tool(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        if (File.Exists(Path.Combine(folder, ConfigName)))
        {
            return new AvrdudeTool(path, null);
        }

        var etc = Path.GetFullPath(Path.Combine(folder, "..", "etc", ConfigName));
        return new AvrdudeTool(path, File.Exists(etc) ? etc : null);
    }

    private string? Variable(string name) =>
        environment(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim().Trim('"') : null;

    /// <summary>Orders versions such as 6.3.0-arduino17 and 7.1-arduino.1 by their numbers, in turn.</summary>
    private sealed partial class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Numbers(x ?? "");
            var b = Numbers(y ?? "");
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                if (a[i] != b[i])
                {
                    return a[i].CompareTo(b[i]);
                }
            }

            return a.Count != b.Count ? a.Count.CompareTo(b.Count) : string.CompareOrdinal(x, y);
        }

        private static List<long> Numbers(string version) =>
            [.. Digits().Matches(version).Select(match => long.Parse(match.Value, CultureInfo.InvariantCulture))];

        [GeneratedRegex(@"\d{1,18}")]
        private static partial Regex Digits();
    }
}
