using System.Collections.Concurrent;

namespace ChipSharp.Ir;

/// <summary>
/// Where things are in memory on one target: the size and alignment of every TIR type and the offset of every
/// struct field, worked out from the target's pointer size (4 or 8 bytes). Everything is naturally aligned, as C
/// lays it out on the targets ChipSharp supports (x86-64, ARM's AAPCS, RISC-V):
/// <list type="bullet">
/// <item>bool and i8 take 1 byte, i16 2, i32 and f32 4, i64 and f64 8, nint and ptr the pointer size; each is
/// aligned to its size.</item>
/// <item>A struct puts each field, in order, at the first offset its alignment allows; it is aligned like its most
/// aligned field, and its size is rounded up to that alignment (so the fields of an array of it stay aligned).</item>
/// <item>An array is its elements one after another: count times the element's size, aligned like the element.</item>
/// </list>
/// The llvm code generator writes struct and array types that LLVM lays out the same way, and takes the alignment
/// of every load, store, slot and global from here.
/// </summary>
public sealed class TirLayout
{
    private readonly ConcurrentDictionary<TirStructType, StructLayout> _structs = new();

    /// <param name="pointerSize">The target's pointer size in bytes: 4 or 8.</param>
    public TirLayout(int pointerSize)
    {
        if (pointerSize is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(pointerSize), pointerSize,
                "TIR lays out memory for 4-byte and 8-byte pointers only.");
        }

        PointerSize = pointerSize;
    }

    /// <summary>The size of a pointer, an nint and a ptr, in bytes.</summary>
    public int PointerSize { get; }

    /// <summary>How many bytes a value of the type takes in memory, padding included (e.g. 8 for <c>{ i8, i32 }</c>).</summary>
    public long SizeOf(TirType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            TirStructType structType => Of(structType).Size,
            TirArrayType array => Checked(array, () => checked(SizeOf(array.Element) * array.Count)),
            _ => ScalarSize(type),
        };
    }

    /// <summary>The alignment of the type in bytes: the address of a value of it is a multiple of this.</summary>
    public int AlignmentOf(TirType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            TirStructType structType => Of(structType).Alignment,
            TirArrayType array => AlignmentOf(array.Element),
            _ => ScalarSize(type),
        };
    }

    /// <summary>Where field number <paramref name="fieldIndex"/> starts, in bytes from the start of the struct.</summary>
    public long OffsetOf(TirStructType structType, int fieldIndex)
    {
        ArgumentNullException.ThrowIfNull(structType);
        ArgumentOutOfRangeException.ThrowIfNegative(fieldIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fieldIndex, structType.Fields.Count);
        return Of(structType).Offsets[fieldIndex];
    }

    /// <summary>The offset of each field, in field order.</summary>
    public IReadOnlyList<long> OffsetsOf(TirStructType structType)
    {
        ArgumentNullException.ThrowIfNull(structType);
        return Of(structType).Offsets;
    }

    private StructLayout Of(TirStructType structType) => _structs.GetOrAdd(structType, Compute);

    private StructLayout Compute(TirStructType structType) => Checked(structType, () =>
    {
        var offsets = new long[structType.Fields.Count];
        var (size, alignment) = (0L, 1);
        for (var i = 0; i < offsets.Length; i++)
        {
            var type = structType.Fields[i].Type;
            var fieldAlignment = AlignmentOf(type);
            offsets[i] = RoundUp(size, fieldAlignment);
            size = checked(offsets[i] + SizeOf(type));
            alignment = Math.Max(alignment, fieldAlignment);
        }

        return new StructLayout(RoundUp(size, alignment), alignment, Array.AsReadOnly(offsets));
    });

    private int ScalarSize(TirType type) => type.Kind switch
    {
        TirTypeKind.Bool or TirTypeKind.I8 => 1,
        TirTypeKind.I16 => 2,
        TirTypeKind.I32 or TirTypeKind.F32 => 4,
        TirTypeKind.I64 or TirTypeKind.F64 => 8,
        TirTypeKind.NativeInt or TirTypeKind.Ptr => PointerSize,
        TirTypeKind.Void => throw new TirException("void has no size: nothing of type void is ever in memory."),
        _ => throw new TirException($"The layout does not know the type {type}."),
    };

    private static long RoundUp(long value, int alignment) => checked(value + alignment - 1) / alignment * alignment;

    /// <summary>Runs <paramref name="compute"/>, turning an overflow into an error that names the type.</summary>
    private static T Checked<T>(TirType type, Func<T> compute)
    {
        try
        {
            return compute();
        }
        catch (OverflowException)
        {
            throw new TirException($"The type {type} is too large: its size does not fit in 64 bits.");
        }
    }

    private sealed record StructLayout(long Size, int Alignment, IReadOnlyList<long> Offsets);
}
