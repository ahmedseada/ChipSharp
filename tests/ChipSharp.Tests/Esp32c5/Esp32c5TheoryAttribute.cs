using ChipSharp.Tests.Pico;
using Xunit.Sdk;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// A test that builds programs for the ESP32-C5 with clang and ld.lld, once per case, each case in a collection of its
/// own (they run side by side). It is skipped, with the reason, on a machine without LLVM.
/// </summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionTheoryDiscoverer", "ChipSharp.Tests")]
public sealed class Esp32c5TheoryAttribute : TheoryAttribute
{
    public Esp32c5TheoryAttribute()
    {
        if (PicoTools.MissingLlvm.Value is { } reason)
        {
            Skip = reason;
        }
    }
}
