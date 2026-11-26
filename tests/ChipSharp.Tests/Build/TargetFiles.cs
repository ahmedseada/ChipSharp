namespace ChipSharp.Tests.Build;

/// <summary>Writes a folder of target files for one test.</summary>
internal static class TargetFiles
{
    /// <summary>Writes &lt;name&gt;.json for each target into <paramref name="folder"/> (made if missing); returns the folder.</summary>
    public static string Write(string folder, params (string Name, string Json)[] targets)
    {
        Directory.CreateDirectory(folder);
        foreach (var (name, json) in targets)
        {
            File.WriteAllText(Path.Combine(folder, name + ".json"), json);
        }

        return folder;
    }
}
