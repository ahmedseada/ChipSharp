using ChipSharp.Abstractions;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// A test that builds a program for the Pico with clang and ld.lld. It is skipped, with the reason, on a machine
/// without LLVM.
/// </summary>
public sealed class PicoFactAttribute : FactAttribute
{
    public PicoFactAttribute()
    {
        if (PicoTools.MissingLlvm.Value is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// A test that builds a program for the Pico and runs it in the rp2040js simulator (tools/rp2040-sim). It is
/// skipped, with the reason, when LLVM, Node.js or the simulator's packages are missing.
/// </summary>
public sealed class SimulatorFactAttribute : FactAttribute
{
    public SimulatorFactAttribute()
    {
        if (PicoTools.MissingSimulator.Value is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Like <see cref="SimulatorFactAttribute"/>, for a test that runs once per case.</summary>
public sealed class SimulatorTheoryAttribute : TheoryAttribute
{
    public SimulatorTheoryAttribute()
    {
        if (PicoTools.MissingSimulator.Value is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Where the tools the Pico tests need are, and why a test cannot run when one is missing.</summary>
internal static class PicoTools
{
    /// <summary>Null when clang and ld.lld are there; otherwise why not.</summary>
    public static readonly Lazy<string?> MissingLlvm = new(() =>
    {
        try
        {
            var clang = new ClangToolchain().FindClang();
            var lld = Path.Combine(Path.GetDirectoryName(clang)!, OperatingSystem.IsWindows() ? "ld.lld.exe" : "ld.lld");
            return File.Exists(lld) ? null : $"ld.lld is not next to clang ('{clang}'); it comes with LLVM.";
        }
        catch (ChipSharpException e)
        {
            return e.Message;
        }
    });

    /// <summary>Null when the simulator can run; otherwise why not.</summary>
    public static readonly Lazy<string?> MissingSimulator = new(() =>
    {
        if (MissingLlvm.Value is { } llvm)
        {
            return llvm;
        }

        if (Node is null)
        {
            return "Node.js (node) is not on the PATH; the rp2040js simulator runs on it. Install Node.js 18 or later.";
        }

        var package = Path.Combine(Repository.Simulator, "node_modules", "rp2040js", "package.json");
        return File.Exists(package)
            ? null
            : $"The simulator's packages are not installed: run 'npm ci' in '{Repository.Simulator}' once.";
    });

    /// <summary>node on the PATH, or null.</summary>
    public static string? Node { get; } = OnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");

    /// <summary>An LLVM tool next to clang, e.g. llvm-objdump.</summary>
    public static string LlvmTool(string name)
    {
        var clang = new ClangToolchain().FindClang();
        return Path.Combine(Path.GetDirectoryName(clang)!, OperatingSystem.IsWindows() ? name + ".exe" : name);
    }

    private static string? OnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), fileName))
            .FirstOrDefault(File.Exists);
}

/// <summary>Folders of the repository the tests run from.</summary>
internal static class Repository
{
    /// <summary>The first folder above the tests that has ChipSharp.slnx.</summary>
    public static string Root { get; } = FindRoot();

    public static string Samples => Path.Combine(Root, "samples");

    public static string Simulator => Path.Combine(Root, "tools", "rp2040-sim");

    public static string Examples => Path.Combine(Root, "examples");

    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "ChipSharp.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException($"No ChipSharp.slnx above '{AppContext.BaseDirectory}'.");
    }
}
