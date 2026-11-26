using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ChipSharp.Abstractions;

namespace ChipSharp.Toolchain.Clang;

/// <summary>
/// Builds PC programs (target "host") with clang and lld: writes the generated files (LLVM IR, the runtime's C)
/// into the output folder, makes an import library for each DLL in the target's "link.imports" with
/// llvm-dlltool, and compiles and links everything into one program with no C runtime.
/// The flags come from the target ("triple", "clang.flags", "link.entry", "link.subsystem", "link.machine"),
/// the output folder and tool locations from <see cref="ClangToolchainOptions"/> or the environment variables
/// <see cref="ClangVariable"/> and <see cref="LlvmBinVariable"/>.
/// </summary>
/// <remarks>
/// The caller passes everything to compile in one <see cref="GeneratedCode"/>: the code generator's files and
/// the runtime's (<see cref="IArchRuntime.Startup"/> and friends). The .ll, .c and .S files are compiled;
/// other files are only written.
/// </remarks>
public sealed class ClangToolchain : IToolchain
{
    /// <summary>The environment variable that points at clang itself.</summary>
    public const string ClangVariable = "CHIPSHARP_CLANG";

    /// <summary>The environment variable that points at the folder with LLVM's tools (clang, llvm-dlltool).</summary>
    public const string LlvmBinVariable = "CHIPSHARP_LLVM_BIN";

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ll", ".c", ".s",
    };

    private readonly LlvmTools _tools;

    public ClangToolchain(ClangToolchainOptions? options = null)
        : this(options, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>For tests: reads environment variables through <paramref name="environment"/>.</summary>
    internal ClangToolchain(ClangToolchainOptions? options, Func<string, string?> environment)
    {
        Options = options ?? new ClangToolchainOptions();
        _tools = new LlvmTools(Options, environment);
    }

    public string Name => "clang";

    public ClangToolchainOptions Options { get; }

    /// <summary>The clang this toolchain runs, or an error that says where it looked and how to point at one.</summary>
    public string FindClang() => _tools.Clang();

    /// <summary>The llvm-dlltool this toolchain runs, or an error that says where it looked.</summary>
    public string FindDlltool() => _tools.Dlltool(FindClang());

    public LinkedBinary Build(GeneratedCode code, TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(target);

        var settings = HostSettings.Read(target);
        CheckFileNames(code.Files);
        var sources = code.Files
            .Where(f => SourceExtensions.Contains(Path.GetExtension(f.FileName)))
            .Select(f => f.FileName)
            .ToList();
        if (sources.Count == 0)
        {
            throw new ChipSharpException(
                "There is nothing to compile: the generated code has no .ll, .c or .S file.");
        }

        var clang = FindClang();
        var folder = Path.GetFullPath(
            Options.OutputFolder ?? Path.Combine(Path.GetTempPath(), "chipsharp", Path.GetRandomFileName()));
        WriteFiles(code.Files, folder);
        var libraries = MakeImportLibraries(settings, clang, folder);

        var program = ProgramFileName(code, settings);
        List<string> arguments =
        [
            $"--target={settings.Triple}",
            .. settings.ClangFlags,
            .. Options.ExtraFlags,
            .. sources,
            .. libraries,
            "-o", program,
            $"-Wl,-entry:{settings.Entry}",
            $"-Wl,-subsystem:{settings.Subsystem}",
        ];
        Run(clang, arguments, folder);

        var path = Path.Combine(folder, program);
        return File.Exists(path)
            ? new LinkedBinary(path)
            : throw new ChipSharpException(
                $"clang reported success but wrote no '{path}':\n  {CommandLine(clang, arguments)}");
    }

    /// <summary>E.g. Square.exe: the program name option, else the first .ll file's name, else "program".</summary>
    private string ProgramFileName(GeneratedCode code, HostSettings settings)
    {
        var firstIr = code.Files.FirstOrDefault(f => f.FileName.EndsWith(".ll", StringComparison.OrdinalIgnoreCase));
        var name = Options.ProgramName
            ?? (firstIr is null ? "program" : Path.GetFileNameWithoutExtension(firstIr.FileName));
        var isWindows = settings.Triple.Contains("windows", StringComparison.OrdinalIgnoreCase);
        return isWindows ? name + ".exe" : name;
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

    /// <summary>Makes kernel32.lib and the like from the target's "link.imports"; returns their file names.</summary>
    private List<string> MakeImportLibraries(HostSettings settings, string clang, string folder)
    {
        var libraries = new List<string>();
        if (settings.Imports.Count == 0)
        {
            return libraries;
        }

        var dlltool = _tools.Dlltool(clang);
        foreach (var import in settings.Imports)
        {
            var stem = Path.GetFileNameWithoutExtension(import.Dll);
            var definition = new StringBuilder()
                .Append("LIBRARY ").Append(import.Dll).Append('\n')
                .Append("EXPORTS\n");
            foreach (var function in import.Functions)
            {
                definition.Append("  ").Append(function).Append('\n');
            }

            File.WriteAllText(Path.Combine(folder, stem + ".def"), definition.ToString());
            Run(dlltool, ["-d", stem + ".def", "-l", stem + ".lib", "-m", settings.Machine!], folder);
            libraries.Add(stem + ".lib");
        }

        return libraries;
    }

    /// <summary>Runs a tool in <paramref name="folder"/>; a failure reports the command and the tool's output.</summary>
    private static void Run(string tool, IReadOnlyList<string> arguments, string folder)
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
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var messages = (output + errors.GetAwaiter().GetResult()).Trim();

        if (process.ExitCode != 0)
        {
            throw new ChipSharpException(
                $"{toolName} failed (exit code {process.ExitCode}) in '{folder}':\n" +
                $"  {CommandLine(tool, arguments)}\n{messages}");
        }
    }

    private static string CommandLine(string tool, IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Prepend(tool).Select(a => a.Length == 0 || a.Contains(' ') ? $"\"{a}\"" : a));
}
