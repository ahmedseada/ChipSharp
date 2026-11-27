using ChipSharp.Abstractions;
using ChipSharp.Ir.Optimizations;

namespace ChipSharp.Frontend;

/// <summary>
/// Whether a program's TIR is narrowed for its target, arithmetic done in 8 or 16 bits where that gives C#'s result
/// (see <see cref="TirNarrowing"/>), and what the narrowing knows of the target. The capability "narrowing" says so; a
/// target that does not say narrows when its pointers are 2 bytes (an 8-bit AVR, where an int takes four registers)
/// and not otherwise, so the code for the Pico and the PC is what it was. The environment variable
/// <c>CHIPSHARP_NARROWING</c>, <c>on</c> or <c>off</c>, decides for every target instead, and <c>on</c> narrows a
/// program translated for no target too: to compare a program's results with and without it anywhere.
/// </summary>
public static class TargetNarrowing
{
    /// <summary>The capability that turns narrowing on (true) or off (false).</summary>
    public const string Capability = "narrowing";

    /// <summary>The environment variable that turns narrowing on or off for every target.</summary>
    public const string EnvironmentVariable = "CHIPSHARP_NARROWING";

    /// <summary>
    /// The narrowing <paramref name="target"/> asks for, or that <see cref="EnvironmentVariable"/> forces (without a
    /// target, only that); null for none.
    /// </summary>
    public static TirNarrowingOptions? For(TargetDescription? target)
    {
        var forced = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();
        var on = string.Equals(forced, "on", StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(forced, "off", StringComparison.OrdinalIgnoreCase) && target is not null
                && (target.Capabilities.TryGetValue(Capability, out var value)
                    ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    : target.PointerSize <= 2));
        if (!on)
        {
            return null;
        }

        // an element takes a byte at least, so an array or span has no more elements than the largest memory has bytes
        var largest = target?.Memory.Values.Select(region => region.Size).DefaultIfEmpty(0UL).Max() ?? 0;
        return new TirNarrowingOptions
        {
            MaxLength = largest is > 0 and < int.MaxValue ? (long)largest : int.MaxValue,
            LengthFields = [(StackArrays.ArrayType, StackArrays.LengthField)],
        };
    }
}
