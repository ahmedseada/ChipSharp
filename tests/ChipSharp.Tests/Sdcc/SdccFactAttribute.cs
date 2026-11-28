using ChipSharp.Abstractions;
using ChipSharp.Toolchain.Sdcc;
using Xunit.Sdk;

namespace ChipSharp.Tests.Sdcc;

/// <summary>A test that builds a program with SDCC. It is skipped, with the reason, on a machine without SDCC.</summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionFactDiscoverer", "ChipSharp.Tests")]
public sealed class SdccFactAttribute : FactAttribute
{
    public SdccFactAttribute()
    {
        if (SdccInstall.Missing.Value is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Where SDCC is for the tests, and why a test cannot run when it is missing.</summary>
internal static class SdccInstall
{
    /// <summary>Null when SDCC is there; otherwise why not (the toolchain's own message).</summary>
    public static readonly Lazy<string?> Missing = new(() =>
    {
        try
        {
            new SdccToolchain().FindSdcc();
            return null;
        }
        catch (ChipSharpException e)
        {
            return e.Message;
        }
    });
}
