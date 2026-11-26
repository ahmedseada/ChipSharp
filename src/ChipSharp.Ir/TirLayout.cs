using System.Collections.Concurrent;

namespace ChipSharp.Ir;

/// <summary>
/// Where things are in memory on one target: the size and alignment of every TIR type and the offset of every
/// struct field, worked out from the target's pointer size (4 or 8 bytes). Everything is naturally aligned, as C
/// lays it out on the targets ChipSharp supports (x86-64, ARM's AAPCS, RISC-V):
/// <list type="bullet">
/// <item>bool and i8 take 1 byte, i16 2, i32 and f32 4, i64 and f64 8, nint, ptr and ref the pointer size; each is
/// aligned to its size.</item>
/// <item>A struct puts each field, in order, at the first offset its alignment allows; it is aligned like its most
/// aligned field, and its size is rounded up to that alignment (so the fields of an array of it stay aligned).</item>
/// <item>An array is its elements one after another: count times the element's size, aligned like the element.</item>
/// </list>
/// The llvm code generator writes struct and array types that LLVM lays out the same way, and takes the alignment
/// of every load, store, slot and global from here.
/// <para>
/// An object on the heap is laid out as its class's object type says (see <see cref="TirTypeDescriptor"/>): a struct
/// whose first field is the header, the address of the type descriptor. An array object is the header, its length
/// (an i32) at <see cref="ArrayLengthOffset"/>, then its elements from <see cref="ArrayDataOffset"/> on: 8 bytes in
/// with 4-byte pointers, 16 with 8-byte ones, so that the elements of every type are aligned (the heap gives every
/// object an address that is a multiple of 8).
/// </para>
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

    /// <summary>The size of a pointer, an nint, a ptr and a ref, in bytes.</summary>
    public int PointerSize { get; }

    /// <summary>Where the length (an i32) of an array object is: right after its header, at the pointer size.</summary>
    public int ArrayLengthOffset => PointerSize;

    /// <summary>
    /// Where element 0 of an array object is: after its header and its length, rounded up to 8 (8 with 4-byte pointers,
    /// 16 with 8-byte ones), whatever the element type.
    /// </summary>
    public int ArrayDataOffset => (PointerSize + 4 + 7) / 8 * 8;

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

    /// <summary>
    /// Where the refs in a value of <paramref name="type"/> are, in bytes from its start, in order: each field of type
    /// ref, also inside structs and arrays (a <c>[3 x ref]</c> has three). What the garbage collector reads in an object
    /// (see <see cref="TirTypeDescriptor"/>) and in a global. Empty for a type without refs, e.g. any number or ptr.
    /// </summary>
    public IReadOnlyList<long> ReferenceOffsets(TirType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var offsets = new List<long>();
        AddReferenceOffsets(type, 0, offsets);
        return offsets;
    }

    private void AddReferenceOffsets(TirType type, long start, List<long> offsets)
    {
        switch (type)
        {
            case { Kind: TirTypeKind.Ref }:
                offsets.Add(start);
                break;

            case TirStructType structType:
                var fieldOffsets = Of(structType).Offsets;
                for (var i = 0; i < fieldOffsets.Count; i++)
                {
                    AddReferenceOffsets(structType.Fields[i].Type, start + fieldOffsets[i], offsets);
                }

                break;

            case TirArrayType array when ReferenceOffsets(array.Element) is { Count: > 0 } inElement:
                var size = SizeOf(array.Element);
                for (var i = 0; i < array.Count; i++)
                {
                    offsets.AddRange(inElement.Select(offset => start + i * size + offset));
                }

                break;
        }
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
        TirTypeKind.NativeInt or TirTypeKind.Ptr or TirTypeKind.Ref => PointerSize,
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
