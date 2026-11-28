using ChipSharp.Tests.Pico;
using Xunit.Sdk;

namespace ChipSharp.Tests.Ch32v003;

/// <summary>
/// A test that builds programs for the CH32V003 with clang and ld.lld, once per case, each case in a collection of its
/// own (they run side by side). It is skipped, with the reason, on a machine without LLVM.
/// </summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionTheoryDiscoverer", "ChipSharp.Tests")]
public sealed class Ch32v003TheoryAttribute : TheoryAttribute
{
    public Ch32v003TheoryAttribute()
    {
        if (PicoTools.MissingLlvm.Value is { } reason)
        {
            Skip = reason;
        }
    }
}
