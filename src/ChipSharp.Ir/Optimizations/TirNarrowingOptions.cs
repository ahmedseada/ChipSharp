namespace ChipSharp.Ir.Optimizations;

/// <summary>
/// What <see cref="TirNarrowing"/> and <see cref="TirRangeAnalysis"/> know of the target. Nothing else of it: they leave
/// nint alone (no range, never narrowed), the one integer whose width TIR does not fix, so a module narrows alike for
/// every target.
/// </summary>
public sealed record TirNarrowingOptions
{
    /// <summary>
    /// The most elements an array or a span can have: every element takes a byte at least, so no more than the bytes of
    /// the target's largest memory (32 KB of flash on an Arduino Uno); int.MaxValue when that is not known.
    /// </summary>
    public long MaxLength { get; init; } = int.MaxValue;

    /// <summary>
    /// The struct fields that hold the number of elements of an array or span (ChipSharp.Frontend's ChipSharp.Array
    /// <c>length</c>): every load of one is in [0, <see cref="MaxLength"/>].
    /// </summary>
    public IReadOnlyList<(TirStructType Struct, int Field)> LengthFields { get; init; } = [];

    /// <summary>The widths, in bits, that values and operations may be narrowed to: some of 8, 16 and 32.</summary>
    public IReadOnlyList<int> Widths { get; init; } = [8, 16, 32];
}
