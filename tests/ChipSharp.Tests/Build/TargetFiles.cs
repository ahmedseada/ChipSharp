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

    /// <summary>
    /// Copies the shipped target files into <paramref name="folder"/>, then writes &lt;name&gt;.json for each of
    /// <paramref name="targets"/> next to them, so that they can inherit the shipped ones; returns the folder.
    /// </summary>
    public static string WithShipped(string folder, params (string Name, string Json)[] targets)
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "targets");
        foreach (var file in Directory.GetFiles(shipped, "*.json", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(folder, Path.GetRelativePath(shipped, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
        }

        Write(Path.Combine(folder, "test"), targets);
        return folder;
    }
}
