using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ChipSharp.Toolchain.Clang;

/// <summary>
/// The build cache: what clang and llvm-dlltool made before (an object file for each source, an import library for each
/// DLL), and SDCC (a .rel for each source, for the toolchain "sdcc"), kept by a key made of everything that goes into
/// it: the tool itself (where it is, its size and its date, so a new LLVM or SDCC makes new files), the arguments
/// (target, cpu, port, flags), the file's name and text, and the headers it can
/// include. The runtime's C is the same in every build for a target, so a build compiles it once and later builds only
/// link it again.
/// </summary>
/// <remarks>
/// Builds may run at once, in one process and in many: a file is moved in under a name of its own and then renamed to
/// its key, so a file under a key is always whole; a build that finds the same key being made in this process waits for
/// it rather than making it twice. A build links the objects where they are in the cache: each new file costs time
/// (a virus scanner reads it), and a copy is a new file. The cache keeps at most <see cref="MaxBytes"/>: past that, the
/// files used longest ago go, but never those of the last hour. Deleting the folder is always safe.
/// </remarks>
internal sealed class ObjectCache
{
    /// <summary>The environment variable that moves the cache, e.g. to a faster disk.</summary>
    public const string FolderVariable = "CHIPSHARP_CACHE";

    /// <summary>What the cache may hold before the files used longest ago go.</summary>
    private const long MaxBytes = 512L << 20;

    /// <summary>What a trim leaves.</summary>
    private const long TrimmedBytes = 256L << 20;

    /// <summary>Part of every key: a new layout of the cache, or of what goes into a key, makes new files.</summary>
    private const string Version = "chipsharp-build-cache 1";

    /// <summary>The files being made in this process, by key.</summary>
    private static readonly ConcurrentDictionary<string, Task> Making = new(StringComparer.Ordinal);

    /// <summary>Each tool's identity, by its path.</summary>
    private static readonly ConcurrentDictionary<string, string> Tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many files this process kept (see <see cref="Trim"/>).</summary>
    private static int _kept;

    private ObjectCache(string folder) => Folder = folder;

    /// <summary>Where the files are.</summary>
    public string Folder { get; }

    /// <summary>The cache <paramref name="options"/> ask for, or null when they turn it off.</summary>
    public static ObjectCache? For(ClangToolchainOptions options, Func<string, string?> environment) =>
        For(options.UseCache, options.CacheFolder, environment);

    /// <summary>
    /// The cache in <paramref name="cacheFolder"/> (when not set: the environment variable <see cref="FolderVariable"/>,
    /// then <see cref="DefaultFolder"/>), or null when <paramref name="useCache"/> is false. Every toolchain that keeps
    /// what it compiles (clang's, SDCC's) keeps it here.
    /// </summary>
    public static ObjectCache? For(bool useCache, string? cacheFolder, Func<string, string?> environment)
    {
        if (!useCache)
        {
            return null;
        }

        var folder = cacheFolder;
        if (string.IsNullOrWhiteSpace(folder) && environment(FolderVariable) is { } variable
            && !string.IsNullOrWhiteSpace(variable))
        {
            folder = variable.Trim().Trim('"');
        }

        return new ObjectCache(Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? DefaultFolder() : folder));
    }

    /// <summary>%LOCALAPPDATA%\ChipSharp\cache (on Windows), else chipsharp-cache in the system temp folder.</summary>
    public static string DefaultFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local)
            ? Path.Combine(Path.GetTempPath(), "chipsharp-cache")
            : Path.Combine(local, "ChipSharp", "cache");
    }

    /// <summary>The key of what <paramref name="tool"/> makes from <paramref name="parts"/>, with the file extension it has.</summary>
    public static string Key(string tool, string extension, IEnumerable<string> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        Add(Version);
        Add(Identity(tool));
        foreach (var part in parts)
        {
            Add(part);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset()) + extension;
    }

    /// <summary>
    /// The file of <paramref name="key"/>: the cache's, when it has it; else made by <paramref name="make"/> (which writes
    /// <paramref name="path"/>) and moved into the cache. Returns where the file is: in the cache, or at
    /// <paramref name="path"/> when the cache cannot be written. When another build of this process is making it, waits
    /// for that one.
    /// </summary>
    public async Task<string> GetAsync(string key, string path, Func<Task> make)
    {
        if (Find(key) is { } found)
        {
            return found;
        }

        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var making = Making.GetOrAdd(key, mine.Task);
        if (making != mine.Task)
        {
            // another build makes it; when that one failed, this one makes it and reports its own error
            await making.ConfigureAwait(false);
            if (Find(key) is { } made)
            {
                return made;
            }

            await make().ConfigureAwait(false);
            return Keep(key, path);
        }

        try
        {
            await make().ConfigureAwait(false);
            return Keep(key, path);
        }
        finally
        {
            Making.TryRemove(key, out _);
            mine.SetResult();
        }
    }

    /// <summary>The cache's file of <paramref name="key"/>, or null when it has none.</summary>
    private string? Find(string key)
    {
        var cached = Path.Combine(Folder, key);
        try
        {
            var written = File.GetLastWriteTimeUtc(cached);
            if (written.Year < 1700)
            {
                return null; // not there: Windows gives the year 1601 for a file that does not exist
            }

            // used now: a trim keeps it longer (the date of a file used today is left as it is)
            if (DateTime.UtcNow - written > TimeSpan.FromHours(12))
            {
                File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
            }

            return cached;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return File.Exists(cached) ? cached : null;
        }
    }

    /// <summary>
    /// Moves the file at <paramref name="path"/> into the cache under <paramref name="key"/>; returns where the file is
    /// then. A cache that cannot be written is no error: the file stays where it is.
    /// </summary>
    private string Keep(string key, string path)
    {
        var cached = Path.Combine(Folder, key);
        var temporary = $"{cached}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Folder);
            File.Move(path, temporary);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }

        try
        {
            File.Move(temporary, cached, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // another build kept the same file first
            if (File.Exists(cached))
            {
                TryDelete(temporary);
                return cached;
            }

            return temporary;
        }

        Trim();
        return cached;
    }

    /// <summary>
    /// When the cache holds more than <see cref="MaxBytes"/>, deletes the files used longest ago, but those of the last
    /// hour (a build may be linking them). It looks at the first file a process keeps and at every 32nd after it.
    /// </summary>
    private void Trim()
    {
        if (Interlocked.Increment(ref _kept) % 32 != 1)
        {
            return;
        }

        try
        {
            var files = new DirectoryInfo(Folder).GetFiles();
            var total = files.Sum(file => file.Length);
            if (total <= MaxBytes)
            {
                return;
            }

            var recent = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var file in files.Where(file => file.LastWriteTimeUtc < recent).OrderBy(file => file.LastWriteTimeUtc))
            {
                if (total <= TrimmedBytes)
                {
                    break;
                }

                total -= file.Length;
                TryDelete(file.FullName);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // trimming can wait for another build
        }
    }

    /// <summary>The tool as a key takes it: its full path, size and date, so another version of it makes other files.</summary>
    private static string Identity(string tool) => Tools.GetOrAdd(tool, path =>
    {
        var file = new FileInfo(path);
        return file.Exists
            ? FormattableString.Invariant($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")
            : path;
    });

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a leftover in the cache folder does no harm
        }
    }
}
