using System.Security.Cryptography;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Where the PC programs the tests build are run from: a copy named after the program's bytes, in a folder of the system
/// temp folder that outlives the test run. A PC's virus scanner reads every new program the first time it runs, up to a
/// second each, a few at a time; a build is deterministic (the same files make the same bytes), so the same program built
/// again, by another test or in the next test run, runs from the copy the scanner has read. The bytes that run are the
/// program's own, checked byte for byte.
/// </summary>
internal static class ProgramStore
{
    /// <summary>How long a copy no test ran is kept.</summary>
    private static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    private static readonly Lazy<string> Folder = new(Open);

    /// <summary>
    /// The path to run <paramref name="program"/> from: for a .exe the tests built (in their scratch folders, under the
    /// system temp folder), its copy in the store; for any other program (dotnet), itself.
    /// </summary>
    public static string Runnable(string program)
    {
        var path = Path.GetFullPath(program);
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return program;
        }

        var bytes = File.ReadAllBytes(path);
        var copy = Path.Combine(Folder.Value, Convert.ToHexString(SHA256.HashData(bytes)) + ".exe");
        try
        {
            if (File.Exists(copy) && File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes))
            {
                Touch(copy);
                return copy;
            }

            // written under a name of its own, then renamed: a copy under its name is always whole
            var temporary = $"{copy}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporary, bytes);
            try
            {
                File.Move(temporary, copy, overwrite: false);
            }
            catch (IOException)
            {
                File.Delete(temporary); // another test stored it first
            }

            return File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes) ? copy : program;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return program; // the store is busy or cannot be written: run the program where it is
        }
    }

    /// <summary>Marks a copy as run today, so that it is kept another <see cref="KeptFor"/>.</summary>
    private static void Touch(string copy)
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(copy) > TimeSpan.FromDays(1))
            {
                File.SetLastWriteTimeUtc(copy, DateTime.UtcNow);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // running elsewhere: another time
        }
    }

    /// <summary>The store's folder, made if missing, without the copies no test ran for <see cref="KeptFor"/>.</summary>
    private static string Open()
    {
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "programs");
        Directory.CreateDirectory(folder);
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            try
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > KeptFor)
                {
                    file.Delete();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // in use, or going: another time
            }
        }

        return folder;
    }
}
