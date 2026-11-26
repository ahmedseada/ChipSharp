namespace ChipSharp.Hardware;

/// <summary>The error every method of this library gives when it is called on .NET instead of on a chip.</summary>
internal static class ChipOnly
{
    public static PlatformNotSupportedException Method(string name) => new(
        $"ChipSharp.Hardware.{name} runs on a chip, not on .NET: build this program with ChipSharp for a target " +
        "such as 'pico' and run it there (or in the rp2040js simulator).");
}
