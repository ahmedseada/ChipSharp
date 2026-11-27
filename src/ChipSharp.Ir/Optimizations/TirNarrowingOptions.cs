namespace ChipSharp.Ir.Optimizations;

/// <summary>What <see cref="TirNarrowing"/> and <see cref="TirRangeAnalysis"/> know of the target.</summary>
public sealed record TirNarrowingOptions
{
    /// <summary>The target's pointer size in bytes (2, 4 or 8): how wide nint is.</summary>
    public required int PointerSize { get; init; }

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

    /// <summary>How wide an integer type is on the target, in bits; 0 for a type that is not an integer.</summary>
    public int Bits(TirType type) => type.Kind switch
    {
        TirTypeKind.I8 => 8,
        TirTypeKind.I16 => 16,
        TirTypeKind.I32 => 32,
        TirTypeKind.I64 => 64,
        TirTypeKind.NativeInt => PointerSize * 8,
        _ => 0,
    };
}
