using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Toolchain.Sdcc;

/// <summary>
/// The toolchain "sdcc": builds a program for a Padauk or STM8 chip with SDCC, the Small Device C Compiler, from the C
/// the code generator "c" writes and the C (and assembly) of the target's runtime. The target's "sdcc.port" is the
/// processor (sdcc's -m: pdk14, stm8), its "sdcc.flags" more arguments (--opt-code-size); see
/// <see cref="SdccSettings"/>.
/// <code>
/// sdcc -mstm8 --opt-code-size -c Blink.c -o Blink.c.rel           (each .c on its own: SDCC compiles one file a run)
/// sdasstm8 -plosgffw runtime.s.rel runtime.s                       (each .s and .asm, with the port's assembler)
/// sdar -rcs Blink.lib pins.c.rel clock.c.rel                       (the runtime's library files, when it has some)
/// sdcc -mstm8 --opt-code-size --code-loc 0x8000 --out-fmt-ihx Blink.c.rel runtime.s.rel -L . -l Blink.lib -o Blink.ihx
/// </code>
/// The link writes the program as Intel HEX (&lt;name&gt;.ihx; a Padauk chip's 14-bit word n in bytes 2n and 2n + 1,
/// little endian, as its programmer reads it) and a map of its areas (&lt;name&gt;.map), from which the memory report
/// and the check that the program fits are made (<see cref="SdccMap"/>): a program too big for the target's memory
/// fails with how much it needs and how much the target has.
/// </summary>
/// <remarks>
/// The .c file that defines main() is linked first, whatever the order of the files: SDCC puts the start-up code (the
/// vectors, clearing and setting the variables) in its file, and the areas of the first file come first, so the
/// starting values another file sets before it would be skipped. SDCC's linker keeps every file it is given whole, and
/// has no weak symbols: the files a runtime marks <see cref="GeneratedFile.Library"/> go into a library
/// (&lt;name&gt;.lib, made with sdar), from which the linker takes a file only for a symbol nothing else defines, so a
/// program has only the runtime it uses, and a file linked whole takes the place of a library file that defines the
/// same symbol (a chip runtime's chipsharp_chip_init, the architecture's empty one). What sdcc and the assembler make is
/// kept in the build cache, the clang toolchain's (<see cref="SdccToolchainOptions.UseCache"/>). Every SDCC program runs
/// with SDCC's folder first on the PATH: SDCC's preprocessor finds its cc1 there when SDCC is not installed where it was
/// built for.
/// </remarks>
public sealed partial class SdccToolchain : IToolchain
{
    /// <summary>The environment variable that points at SDCC: sdcc.exe, or the folder it is in.</summary>
    public const string SdccVariable = "CHIPSHARP_SDCC";

    private static readonly HashSet<string> CSources = new(StringComparer.OrdinalIgnoreCase) { ".c" };
    private static readonly HashSet<string> AssemblySources = new(StringComparer.OrdinalIgnoreCase) { ".s", ".asm" };

    /// <summary>How many tools a process runs at once to compile: one per processor, however many builds there are.</summary>
    private static readonly SemaphoreSlim Compiles = new(Environment.ProcessorCount);

    private readonly SdccTools _tools;
    private readonly ObjectCache? _cache;

    public SdccToolchain(SdccToolchainOptions? options = null)
        : this(options, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>For tests: reads environment variables through <paramref name="environment"/>.</summary>
    internal SdccToolchain(SdccToolchainOptions? options, Func<string, string?> environment)
    {
        Options = options ?? new SdccToolchainOptions();
        _tools = new SdccTools(Options, environment);
        _cache = ObjectCache.For(Options.UseCache, Options.CacheFolder, environment);
    }

    public string Name => "sdcc";

    public SdccToolchainOptions Options { get; }

    /// <summary>The build cache's folder, or null when <see cref="SdccToolchainOptions.UseCache"/> turns it off.</summary>
    public string? CacheFolder => _cache?.Folder;

    /// <summary>The sdcc this toolchain runs, or an error that says where it looked and how to point at one.</summary>
    public string FindSdcc() => _tools.Sdcc();

    public LinkedBinary Build(GeneratedCode code, TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(target);

        var settings = SdccSettings.Read(target);
        var sources = Sources(code);
        var sdcc = FindSdcc();
        var folder = OutputFolder();
        var name = ProgramName(code);

        // the rules that mend SDCC's peephole optimizer on the port, in the build folder (and in the cache's key)
        var extraFlags = Options.ExtraFlags;
        if (SdccPeepholes.For(settings.Port) is { } rules)
        {
            code = new GeneratedCode([.. code.Files, new GeneratedFile(SdccPeepholes.FileName, rules)]);
            extraFlags = [.. extraFlags, "--peep-file", SdccPeepholes.FileName];
        }

        WriteFiles(code.Files, folder);

        var flags = CompileFlags(settings, extraFlags);
        var compiled = Compile(sdcc, settings.Port, flags, code, sources, folder);
        var inLibrary = code.Files.Where(file => file.Library).Select(file => file.FileName + ".rel")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var objects = MainFirst([.. compiled.Where(rel => !inLibrary.Contains(rel))], folder);
        List<string> members = [.. compiled.Where(inLibrary.Contains)];
        var library = members.Count == 0 ? null : name + ".lib";
        var program = name + ".ihx";
        var arguments = LinkArguments(settings, extraFlags, objects, program, library);
        var path = Path.Combine(folder, program);
        var map = Path.Combine(folder, name + ".map");
        try
        {
            // a program and map of an earlier build in the same folder would pass for this one's, and so would the
            // members of its library
            File.Delete(path);
            File.Delete(map);
            if (library is not null)
            {
                File.Delete(Path.Combine(folder, library));
                Run(SdccTools.Archiver(sdcc), ["-rcs", library, .. members], folder);
            }

            Run(sdcc, arguments, folder);
        }
        catch (ChipSharpException e) when (File.Exists(map)
            && Overflow(map, settings, name, target) is { } message)
        {
            // sdldpdk fails a program too big for its port's largest chip; the target's own sizes say more
            throw new ChipSharpException(message, e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ChipSharpException($"Cannot replace the program of an earlier build in '{folder}': {e.Message}", e);
        }

        if (!File.Exists(path) || !File.Exists(map))
        {
            throw new ChipSharpException($"sdcc reported success but wrote no '{(File.Exists(path) ? map : path)}':\n" +
                $"  {CommandLine(sdcc, arguments)}");
        }

        if (Overflow(map, settings, name, target) is { } overflow)
        {
            throw new ChipSharpException(overflow);
        }

        return new LinkedBinary(path);
    }

    /// <summary>The program's overflow of the target's memory, from its map, with the biggest functions; null when it fits.</summary>
    private static string? Overflow(string map, SdccSettings settings, string name, TargetDescription target)
    {
        var read = SdccMap.Read(map);
        return SdccMemoryOverflow.Message(read.Use(settings), name, target, read.Functions);
    }

    /// <summary>What every sdcc run gets, compiling and linking: -m&lt;port&gt;, the target's flags, then the extra ones.</summary>
    internal static IReadOnlyList<string> CompileFlags(SdccSettings settings, IReadOnlyList<string> extraFlags) =>
        [$"-m{settings.Port}", .. settings.Flags, .. extraFlags];

    /// <summary>
    /// The link: the flags, where the code goes, Intel HEX out, the objects (main()'s first), the library of the files
    /// the program may use nothing of when there is one (in the build's folder, before SDCC's own), the program.
    /// </summary>
    internal static IReadOnlyList<string> LinkArguments(SdccSettings settings, IReadOnlyList<string> extraFlags,
        IReadOnlyList<string> objects, string program, string? library = null) =>
    [
        .. CompileFlags(settings, extraFlags), .. settings.PlacementFlags, "--out-fmt-ihx", .. objects,
        .. library is null ? Array.Empty<string>() : ["-L", ".", "-l", library], "-o", program,
    ];

    /// <summary>The files SDCC compiles (.c) and assembles (.s, .asm); checks the names of all of them first.</summary>
    private static List<string> Sources(GeneratedCode code)
    {
        CheckFileNames(code.Files);
        var sources = code.Files.Select(f => f.FileName).Where(IsSource).ToList();
        return sources.Any(source => CSources.Contains(Path.GetExtension(source)))
            ? sources
            : throw new ChipSharpException("There is nothing to compile: the generated code has no .c file.");
    }

    private static bool IsSource(string fileName) =>
        CSources.Contains(Path.GetExtension(fileName)) || AssemblySources.Contains(Path.GetExtension(fileName));

    private string OutputFolder() => Path.GetFullPath(
        Options.OutputFolder ?? Path.Combine(Path.GetTempPath(), "chipsharp", Path.GetRandomFileName()));

    /// <summary>E.g. Blink: the program name option, else the first .c file's name, else "program"; spaces become '_'.</summary>
    private string ProgramName(GeneratedCode code)
    {
        var firstC = code.Files.FirstOrDefault(f => CSources.Contains(Path.GetExtension(f.FileName)));
        var name = Options.ProgramName ?? (firstC is null ? "program" : Path.GetFileNameWithoutExtension(firstC.FileName));
        return name.Replace(' ', '_');
    }

    private static void CheckFileNames(IReadOnlyList<GeneratedFile> files)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.FileName) || file.FileName.Trim('.').Length == 0
                || file.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ChipSharpException(
                    $"Generated file name '{file.FileName}' is not a plain file name (no folders allowed).");
            }

            if (file.FileName.Contains(' ', StringComparison.Ordinal))
            {
                throw new ChipSharpException(
                    $"Generated file name '{file.FileName}' has a space, which SDCC cannot take in a file name.");
            }

            if (!names.Add(file.FileName))
            {
                throw new ChipSharpException(
                    $"Two generated files are named '{file.FileName}'; every file of a build needs its own name.");
            }
        }
    }

    private static void WriteFiles(IReadOnlyList<GeneratedFile> files, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var file in files)
            {
                File.WriteAllText(Path.Combine(folder, file.FileName), file.Content);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ChipSharpException($"Cannot write the build files to '{folder}': {e.Message}", e);
        }
    }

    /// <summary>
    /// Compiles each .c file with sdcc and assembles each .s and .asm file with the port's assembler, several at once,
    /// each into an object of its own (&lt;file&gt;.rel, next to it); one the build cache has is copied from there
    /// instead (the linker takes no folder with a space, which the cache's may have). Returns the objects in the order
    /// of the sources.
    /// </summary>
    private List<string> Compile(string sdcc, string port, IReadOnlyList<string> flags, GeneratedCode code,
        IReadOnlyList<string> sources, string folder)
    {
        var texts = code.Files.ToDictionary(file => file.FileName, file => file.Content, StringComparer.OrdinalIgnoreCase);

        // what a source may include: the files that are not compiled (the runtime's headers), with their text
        List<string> headers =
        [
            .. code.Files.Where(file => !IsSource(file.FileName))
                .OrderBy(file => file.FileName, StringComparer.Ordinal)
                .SelectMany(file => new[] { file.FileName, file.Content }),
        ];
        var assembler = sources.Any(source => AssemblySources.Contains(Path.GetExtension(source)))
            ? SdccTools.Assembler(sdcc, port)
            : null;
        var objects = sources.Select(source => source + ".rel").ToList();

        async Task CompileOne(int i)
        {
            var isC = CSources.Contains(Path.GetExtension(sources[i]));
            var tool = isC ? sdcc : assembler!;
            IReadOnlyList<string> arguments = isC
                ? [.. flags, "-c", sources[i], "-o", objects[i]]
                : ["-plosgffw", objects[i], sources[i]];
            async Task Make()
            {
                await Compiles.WaitAsync().ConfigureAwait(false);
                try
                {
                    await RunAsync(tool, arguments, folder).ConfigureAwait(false);
                }
                finally
                {
                    Compiles.Release();
                }
            }

            if (_cache is null)
            {
                await Make().ConfigureAwait(false);
                return;
            }

            var path = Path.Combine(folder, objects[i]);
            var key = ObjectCache.Key(tool, ".rel", [.. arguments, texts[sources[i]], .. headers]);
            var cached = await _cache.GetAsync(key, path, Make).ConfigureAwait(false);
            if (!string.Equals(cached, path, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(cached, path, overwrite: true);
            }

            // the linker rewrites each object's listing with the final addresses (<file>.rst) and fails without one;
            // a .rel from the cache has none, and gets an empty one
            var listing = Path.Combine(folder, Path.ChangeExtension(objects[i], ".lst"));
            if (!File.Exists(listing))
            {
                await File.WriteAllTextAsync(listing, "").ConfigureAwait(false);
            }
        }

        // the first source that fails (in their order) is the error
        Task.WhenAll(Enumerable.Range(0, sources.Count).Select(i => Task.Run(() => CompileOne(i)))).GetAwaiter().GetResult();
        return objects;
    }

    /// <summary>
    /// The objects with the one that defines main() first (its symbol _main, "S _main Def..." in the .rel), the others
    /// in their order.
    /// </summary>
    private static List<string> MainFirst(IReadOnlyList<string> objects, string folder)
    {
        var main = objects.Where(rel => DefinesMain().IsMatch(File.ReadAllText(Path.Combine(folder, rel)))).ToList();
        return main.Count switch
        {
            1 => [main[0], .. objects.Where(rel => rel != main[0])],
            0 => throw new ChipSharpException("None of the C files defines main(), where an SDCC program starts: " +
                "SDCC puts the start-up code (the vectors, the variables' starting values) in main()'s file."),
            _ => throw new ChipSharpException($"More than one file defines main(): " +
                $"{string.Join(", ", main.Select(rel => rel[..^".rel".Length]))}."),
        };
    }

    /// <summary>Runs a tool in <paramref name="folder"/>; a failure reports the command and the tool's output.</summary>
    private static void Run(string tool, IReadOnlyList<string> arguments, string folder) =>
        Task.Run(() => RunAsync(tool, arguments, folder)).GetAwaiter().GetResult();

    /// <summary>
    /// Runs a tool of SDCC's in <paramref name="folder"/>, with SDCC's folder first on the PATH; a failure reports the
    /// command and the tool's output.
    /// </summary>
    private static async Task RunAsync(string tool, IReadOnlyList<string> arguments, string folder)
    {
        var start = new ProcessStartInfo(tool)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var path = start.Environment.TryGetValue("PATH", out var value) ? value : null;
        start.Environment["PATH"] = string.IsNullOrEmpty(path)
            ? Path.GetDirectoryName(tool)
            : Path.GetDirectoryName(tool) + Path.PathSeparator + path;

        var toolName = Path.GetFileNameWithoutExtension(tool);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new ChipSharpException(
                $"Could not start {toolName}: {e.Message}\n  in '{folder}':\n  {CommandLine(tool, arguments)}", e);
        }

        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var messages = (await output.ConfigureAwait(false) + await errors.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            throw new ChipSharpException(
                $"{toolName} failed (exit code {process.ExitCode}) in '{folder}':\n" +
                $"  {CommandLine(tool, arguments)}\n{messages}");
        }
    }

    private static string CommandLine(string tool, IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Prepend(tool).Select(a => a.Length == 0 || a.Contains(' ') ? $"\"{a}\"" : a));

    [GeneratedRegex(@"^S _main Def[0-9A-Fa-f]+\s*$", RegexOptions.Multiline)]
    private static partial Regex DefinesMain();
}
