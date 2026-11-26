using ChipSharp.Abstractions;

namespace ChipSharp.Toolchain.Clang;

/// <summary>
/// Finds clang and llvm-dlltool. A place someone set (an option or an environment variable) must hold the tool;
/// otherwise the tool is looked for next to clang, in LLVM's install folder and on the PATH.
/// </summary>
internal sealed class LlvmTools(ClangToolchainOptions options, Func<string, string?> environment)
{
    private static readonly string ExeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";

    public string Clang() => Find("clang", clangFolder: null);

    public string Dlltool(string clang) => Find("llvm-dlltool", Path.GetDirectoryName(clang));

    private string Find(string tool, string? clangFolder)
    {
        var file = tool + ExeSuffix;
        var isClang = clangFolder is null;

        var configured = new List<(string Path, string SetBy)>();
        if (isClang && !string.IsNullOrWhiteSpace(options.ClangPath))
        {
            configured.Add((options.ClangPath, "the ClangPath option"));
        }

        if (!string.IsNullOrWhiteSpace(options.LlvmBinFolder))
        {
            configured.Add((Path.Combine(options.LlvmBinFolder, file), "the LlvmBinFolder option"));
        }

        if (isClang && Variable(ClangToolchain.ClangVariable) is { } clangVariable)
        {
            configured.Add((clangVariable, $"the environment variable {ClangToolchain.ClangVariable}"));
        }

        if (Variable(ClangToolchain.LlvmBinVariable) is { } binVariable)
        {
            configured.Add(
                (Path.Combine(binVariable, file), $"the environment variable {ClangToolchain.LlvmBinVariable}"));
        }

        if (configured.Count > 0)
        {
            var (path, setBy) = configured[0];
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath)
                ? fullPath
                : throw new ChipSharpException($"{tool} was not found at '{fullPath}' (set by {setBy}).");
        }

        var folders = new List<string>();
        if (clangFolder is not null)
        {
            folders.Add(clangFolder);
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0)
        {
            folders.Add(Path.Combine(programFiles, "LLVM", "bin"));
        }

        var searchPath = (environment("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => folder.Trim('"'));
        foreach (var folder in folders.Concat(searchPath))
        {
            var path = Path.Combine(folder, file);
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        var clangHint = isClang ? $", {ClangToolchain.ClangVariable} to clang itself," : "";
        throw new ChipSharpException(
            $"{tool} was not found. Install LLVM (https://github.com/llvm/llvm-project/releases), or set " +
            $"{ClangToolchain.LlvmBinVariable} to the folder with LLVM's tools{clangHint} " +
            "or put that folder on the PATH. " +
            $"Looked in: {string.Join(", ", folders.Select(f => $"'{f}'"))} and the PATH.");
    }

    private string? Variable(string name) =>
        environment(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim().Trim('"') : null;
}
