using ChipSharp.Abstractions;
using ChipSharp.Toolchain.Clang;
using Xunit.Sdk;

namespace ChipSharp.Tests.Host;

/// <summary>
/// A test that builds and runs a real PC program. It is skipped, with the reason, on a machine that cannot:
/// one that is not Windows, or has no LLVM (clang, llvm-dlltool).
/// </summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionFactDiscoverer", "ChipSharp.Tests")]
public sealed class HostFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> SkipReason = new(FindSkipReason);

    public HostFactAttribute()
    {
        if (SkipReason.Value is { } reason)
        {
            Skip = reason;
        }
    }

    private static string? FindSkipReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "PC programs are Windows programs; this machine runs another system.";
        }

        try
        {
            new ClangToolchain().FindDlltool();
            return null;
        }
        catch (ChipSharpException e)
        {
            return e.Message;
        }
    }
}
