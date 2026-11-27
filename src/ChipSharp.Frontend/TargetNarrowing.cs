using ChipSharp.Abstractions;
using ChipSharp.Ir.Optimizations;

namespace ChipSharp.Frontend;

/// <summary>
/// Whether a program's TIR is narrowed for its target, arithmetic done in 8 or 16 bits where that gives C#'s result
/// (see <see cref="TirNarrowing"/>), and what the narrowing knows of the target. The capability "narrowing" says so; a
/// target that does not say narrows when its pointers are 2 bytes (an 8-bit AVR, where an int takes four registers)
/// and not otherwise, so the code for the Pico and the PC is what it was.
/// </summary>
public static class TargetNarrowing
{
    /// <summary>The capability that turns narrowing on (true) or off (false).</summary>
    public const string Capability = "narrowing";

    /// <summary>The narrowing <paramref name="target"/> asks for; null for none (and without a target).</summary>
    public static TirNarrowingOptions? For(TargetDescription? target)
    {
        if (target is null)
        {
            return null;
        }

        var on = target.Capabilities.TryGetValue(Capability, out var value)
            ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            : target.PointerSize <= 2;
        if (!on)
        {
            return null;
        }

        // an element takes a byte at least, so an array or span has no more elements than the largest memory has bytes
        var largest = target.Memory.Values.Select(region => region.Size).DefaultIfEmpty(0UL).Max();
        return new TirNarrowingOptions
        {
            PointerSize = target.PointerSize,
            MaxLength = largest is > 0 and < int.MaxValue ? (long)largest : int.MaxValue,
            LengthFields = [(StackArrays.ArrayType, StackArrays.LengthField)],
        };
    }
}
