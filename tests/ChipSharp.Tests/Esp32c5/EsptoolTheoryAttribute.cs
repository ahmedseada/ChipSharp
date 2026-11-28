using ChipSharp.Flasher.Esptool;
using ChipSharp.Tests.Pico;
using Xunit.Sdk;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// A test that builds a program for the ESP32-C5 and checks it with the real esptool (never writing a board). Skipped,
/// with the reason, on a machine without LLVM or without esptool (<see cref="Path"/> says where it is found).
/// </summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionTheoryDiscoverer", "ChipSharp.Tests")]
public sealed class EsptoolTheoryAttribute : TheoryAttribute
{
    private static readonly Lazy<EsptoolTool?> Found = new(() =>
    {
        try
        {
            return new EsptoolFlasher().FindEsptool();
        }
        catch (ChipSharp.Abstractions.ChipSharpException)
        {
            return null;
        }
    });

    /// <summary>esptool as found on this machine; null when there is none.</summary>
    public static EsptoolTool? Tool => Found.Value;

    public EsptoolTheoryAttribute()
    {
        if (PicoTools.MissingLlvm.Value is { } reason)
        {
            Skip = reason;
        }
        else if (Found.Value is null)
        {
            Skip = "esptool was not found (CHIPSHARP_ESPTOOL, the PATH, " + EsptoolFlasher.ToolsFolder + ", py -m esptool).";
        }
    }
}
