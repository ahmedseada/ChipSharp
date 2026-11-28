using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;

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
internal sealed class AvrdudeLocator(Func<string, string?> environment)
{
    private const string ConfigName = "avrdude.conf";

    /// <summary>What to do when avrdude is missing.</summary>
    internal static string Advice =>
        "avrdude uploads a program through an Arduino's USB port, to its bootloader. Get it with the Arduino IDE " +
        "(https://www.arduino.cc/en/software: it installs avrdude with the \"Arduino AVR Boards\", which it adds for an " +
        "Uno, and ChipSharp finds it there), or download avrdude itself (https://github.com/avrdudes/avrdude/releases, " +
        $"for Windows the windows-x64 zip), unpack it and set {AvrdudeFlasher.AvrdudeVariable} to its " +
        $"{ProgrammerTool.FileName("avrdude")}, or put its folder on the PATH.";

    public AvrdudeTool Find()
    {
        if (ProgrammerTool.FromVariable("avrdude", AvrdudeFlasher.AvrdudeVariable, environment, Advice) is { } set)
        {
            return Tool(set);
        }

        var file = ProgrammerTool.FileName("avrdude");
        var places = ArduinoPlaces();
        if ((ProgrammerTool.OnPath("avrdude", environment)
                ?? places.SelectMany(place => Copies(place, file)).Where(File.Exists).Select(Path.GetFullPath).FirstOrDefault())
            is { } found)
        {
            return Tool(found);
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
    private static IEnumerable<string> Copies((string Folder, bool HasVersions) place, string file) =>
        place.HasVersions
            ? ProgrammerTool.Versions(place.Folder).Select(folder => Path.Combine(folder, "bin", file))
            : [Path.Combine(place.Folder, file)];

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
}
