using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Targets;
using ChipSharp.Tests.Sdcc;
using Xunit.Sdk;

namespace ChipSharp.Tests.Flash;

/// <summary>
/// A test that builds a C# program for the SDCC boards (pfs154-bare, stm8s103-board) with SDCC. It is skipped, with the
/// reason, on a machine without SDCC, and until ChipSharp has the boards' runtimes (pdk14, stm8).
/// </summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionFactDiscoverer", "ChipSharp.Tests")]
public sealed class SdccBoardsFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Missing = new(() =>
    {
        if (SdccInstall.Missing.Value is { } sdcc)
        {
            return sdcc;
        }

        var targets = new TargetLoader(Path.Combine(AppContext.BaseDirectory, "targets"));
        foreach (var board in new[] { "pfs154-bare", "stm8s103-board" })
        {
            var target = targets.Load(board);
            var registry = DefaultComponents.CreateRegistry(new BuildContext(new BuildOptions(), target, "Program",
                Path.GetTempPath()));
            if (!registry.TryGet<IArchRuntime>(target.Runtime!, out _))
            {
                return $"The runtime \"{target.Runtime}\" is not in ChipSharp yet: a program is built for '{board}' once it is.";
            }
        }

        return null;
    });

    public SdccBoardsFactAttribute()
    {
        if (Missing.Value is { } reason)
        {
            Skip = reason;
        }
    }
}
