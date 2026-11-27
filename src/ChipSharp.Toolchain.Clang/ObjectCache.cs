using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ChipSharp.Toolchain.Clang;

/// <summary>
/// The build cache: what clang and llvm-dlltool made before (an object file for each source, an import library for each
/// DLL), kept by a key made of everything that goes into it: the tool itself (where it is, its size and its date, so a
/// new LLVM makes new files), the arguments (target, cpu, flags), the file's name and text, and the headers it can
/// include. The runtime's C is the same in every build for a target, so a build compiles it once and later builds only
/// link it again.
/// </summary>
/// <remarks>
/// Builds may run at once, in one process and in many: a file is written under a name of its own and then renamed to
/// its key, so a file under a key is always whole; a build that finds the same key being made in this process waits for
/// it rather than making it twice. The cache keeps at most <see cref="MaxBytes"/>: past that, the files used longest ago
/// go. Deleting the folder is always safe.
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
    public static ObjectCache? For(ClangToolchainOptions options, Func<string, string?> environment)
    {
        if (!options.UseCache)
        {
            return null;
        }

        var folder = options.CacheFolder;
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
    /// Puts the file of <paramref name="key"/> at <paramref name="path"/>: copied from the cache when it is there, else
    /// made by <paramref name="make"/> (which writes <paramref name="path"/>) and then kept. When another build of this
    /// process is making it, waits for that one.
    /// </summary>
    public async Task GetAsync(string key, string path, Func<Task> make)
    {
        if (TryCopy(key, path))
        {
            return;
        }

        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var making = Making.GetOrAdd(key, mine.Task);
        if (making != mine.Task)
        {
            // another build makes it; when that one failed, this one makes it and reports its own error
            await making.ConfigureAwait(false);
            if (TryCopy(key, path))
            {
                return;
            }

            await make().ConfigureAwait(false);
            Keep(key, path);
            return;
        }

        try
        {
            await make().ConfigureAwait(false);
            Keep(key, path);
        }
        finally
        {
            Making.TryRemove(key, out _);
            mine.SetResult();
        }
    }

    /// <summary>Copies the file of <paramref name="key"/> to <paramref name="path"/>; false when the cache does not have it.</summary>
    private bool TryCopy(string key, string path)
    {
        var cached = Path.Combine(Folder, key);
        try
        {
            File.Copy(cached, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // not there (or going): make it
        }

        // used now: a trim keeps it longer (the date of a file used today is left as it is)
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(cached) > TimeSpan.FromHours(12))
            {
                File.SetLastWriteTimeUtc(cached, now);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the copy is what matters
        }

        return true;
    }

    /// <summary>Keeps a copy of the file at <paramref name="path"/> under <paramref name="key"/>; a cache that cannot be written is no error.</summary>
    private void Keep(string key, string path)
    {
        var cached = Path.Combine(Folder, key);
        var temporary = $"{cached}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Folder);
            File.Copy(path, temporary);
            File.Move(temporary, cached, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // another build kept the same file first, or the cache cannot be written: the build goes on without it
            TryDelete(temporary);
            return;
        }

        Trim();
    }

    /// <summary>
    /// When the cache holds more than <see cref="MaxBytes"/>, deletes the files used longest ago. It looks at the first
    /// file a process keeps and at every 32nd after it.
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

            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
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
