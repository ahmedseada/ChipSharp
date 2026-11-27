using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

/// <summary>
/// Sizes, alignments and field offsets with 4-byte pointers (Pico, RISC-V), 8-byte pointers (PC), and the 2-byte pointers
/// and no alignment of an AVR (Arduino Uno).
/// </summary>
public sealed class TirLayoutTests
{
    private static readonly TirLayout Four = new(4);
    private static readonly TirLayout Eight = new(8);
    private static readonly TirLayout Avr = new(2, maxAlignment: 1);

    [Theory]
    [InlineData(TirTypeKind.Bool, 1, 1)]
    [InlineData(TirTypeKind.I8, 1, 1)]
    [InlineData(TirTypeKind.I16, 2, 2)]
    [InlineData(TirTypeKind.I32, 4, 4)]
    [InlineData(TirTypeKind.I64, 8, 8)]
    [InlineData(TirTypeKind.F32, 4, 4)]
    [InlineData(TirTypeKind.F64, 8, 8)]
    public void Scalars_are_the_same_size_on_every_target_and_aligned_to_their_size(TirTypeKind kind, long size, int alignment)
    {
        var type = Scalar(kind);
        foreach (var layout in new[] { Four, Eight })
        {
            Assert.Equal(size, layout.SizeOf(type));
            Assert.Equal(alignment, layout.AlignmentOf(type));
        }
    }

    [Fact]
    public void Nint_and_ptr_follow_the_pointer_size()
    {
        Assert.Equal(4, Four.PointerSize);
        Assert.Equal(4, Four.SizeOf(TirType.NativeInt));
        Assert.Equal(4, Four.AlignmentOf(TirType.NativeInt));
        Assert.Equal(4, Four.SizeOf(TirType.Ptr));
        Assert.Equal(4, Four.AlignmentOf(TirType.Ptr));
        Assert.Equal(8, Eight.SizeOf(TirType.NativeInt));
        Assert.Equal(8, Eight.AlignmentOf(TirType.NativeInt));
        Assert.Equal(8, Eight.SizeOf(TirType.Ptr));
        Assert.Equal(8, Eight.AlignmentOf(TirType.Ptr));
    }

    [Fact]
    public void A_struct_of_ints_has_no_padding()
    {
        var point = TirMemorySamples.Point;

        Assert.Equal(8, Four.SizeOf(point));
        Assert.Equal(4, Four.AlignmentOf(point));
        Assert.Equal([0L, 4L], Four.OffsetsOf(point));
        Assert.Equal(8, Eight.SizeOf(point));
        Assert.Equal(4, Eight.AlignmentOf(point));
    }

    [Fact]
    public void Fields_are_padded_to_their_alignment_and_the_size_to_the_struct_alignment()
    {
        // struct Mixed { byte A; long B; short C; }: like C, B starts at 8 and the size rounds up to 24
        var mixed = new TirStructType("Program.Mixed", ("A", TirType.I8), ("B", TirType.I64), ("C", TirType.I16));

        foreach (var layout in new[] { Four, Eight })
        {
            Assert.Equal([0L, 8L, 16L], layout.OffsetsOf(mixed));
            Assert.Equal(8, layout.OffsetOf(mixed, 1));
            Assert.Equal(24, layout.SizeOf(mixed));
            Assert.Equal(8, layout.AlignmentOf(mixed));
        }
    }

    [Fact]
    public void A_pointer_field_moves_the_fields_after_it_with_the_pointer_size()
    {
        // struct Node { byte Tag; void* Next; int Value; }
        var node = new TirStructType("Program.Node", ("Tag", TirType.I8), ("Next", TirType.Ptr), ("Value", TirType.I32));

        Assert.Equal([0L, 4L, 8L], Four.OffsetsOf(node));
        Assert.Equal(12, Four.SizeOf(node));
        Assert.Equal(4, Four.AlignmentOf(node));
        Assert.Equal([0L, 8L, 16L], Eight.OffsetsOf(node));
        Assert.Equal(24, Eight.SizeOf(node));
        Assert.Equal(8, Eight.AlignmentOf(node));
    }

    [Fact]
    public void Small_fields_share_the_space_before_a_larger_one()
    {
        // struct Flags { bool A; byte B; short C; int D; }
        var flags = new TirStructType("Program.Flags",
            ("A", TirType.Bool), ("B", TirType.I8), ("C", TirType.I16), ("D", TirType.I32));

        Assert.Equal([0L, 1L, 2L, 4L], Eight.OffsetsOf(flags));
        Assert.Equal(8, Eight.SizeOf(flags));
    }

    [Fact]
    public void A_nested_struct_is_aligned_like_its_most_aligned_field()
    {
        // struct Line { Point From; Point To; byte Color; }: 4 + 4, 4 + 4, 1, then padding to 4
        var line = TirMemorySamples.Line;

        Assert.Equal([0L, 8L, 16L], Four.OffsetsOf(line));
        Assert.Equal(20, Four.SizeOf(line));
        Assert.Equal(4, Four.AlignmentOf(line));

        // struct Outer { byte Tag; Mixed Inner; byte Last; }
        var mixed = new TirStructType("Program.Mixed", ("A", TirType.I8), ("B", TirType.I64), ("C", TirType.I16));
        var outer = new TirStructType("Program.Outer", ("Tag", TirType.I8), ("Inner", mixed), ("Last", TirType.I8));
        Assert.Equal([0L, 8L, 32L], Four.OffsetsOf(outer));
        Assert.Equal(40, Four.SizeOf(outer));
        Assert.Equal(8, Four.AlignmentOf(outer));
    }

    [Fact]
    public void An_array_is_its_elements_one_after_another()
    {
        Assert.Equal(40, Four.SizeOf(TirMemorySamples.TenInts));
        Assert.Equal(4, Four.AlignmentOf(TirMemorySamples.TenInts));
        Assert.Equal(3, Eight.SizeOf(new TirArrayType(TirType.Bool, 3)));
        Assert.Equal(1, Eight.AlignmentOf(new TirArrayType(TirType.Bool, 3)));
        Assert.Equal(16, Four.SizeOf(new TirArrayType(TirType.Ptr, 4)));
        Assert.Equal(32, Eight.SizeOf(new TirArrayType(TirType.Ptr, 4)));
        Assert.Equal(8, Eight.AlignmentOf(new TirArrayType(TirType.Ptr, 4)));
    }

    [Fact]
    public void Arrays_of_structs_and_arrays_in_structs_keep_every_element_aligned()
    {
        // the struct's size already ends on its alignment, so element i is at i * 24
        var mixed = new TirStructType("Program.Mixed", ("A", TirType.I8), ("B", TirType.I64), ("C", TirType.I16));
        Assert.Equal(72, Eight.SizeOf(new TirArrayType(mixed, 3)));
        Assert.Equal(8, Eight.AlignmentOf(new TirArrayType(mixed, 3)));

        // struct Buffer { byte Length; fixed short Data[3]; nint Handle; }
        var buffer = new TirStructType("Program.Buffer",
            ("Length", TirType.I8), ("Data", new TirArrayType(TirType.I16, 3)), ("Handle", TirType.NativeInt));
        Assert.Equal([0L, 2L, 8L], Four.OffsetsOf(buffer));
        Assert.Equal(12, Four.SizeOf(buffer));
        Assert.Equal([0L, 2L, 8L], Eight.OffsetsOf(buffer));
        Assert.Equal(16, Eight.SizeOf(buffer));

        // [2 x [3 x i16]]
        var grid = new TirArrayType(new TirArrayType(TirType.I16, 3), 2);
        Assert.Equal(12, Four.SizeOf(grid));
        Assert.Equal(2, Four.AlignmentOf(grid));
    }

    [Fact]
    public void Only_2_4_and_8_byte_pointers_are_laid_out_up_to_an_alignment_of_8()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new TirLayout(1));
        Assert.StartsWith("TIR lays out memory for 2-byte, 4-byte and 8-byte pointers only.", error.Message);

        var alignment = Assert.Throws<ArgumentOutOfRangeException>(() => new TirLayout(4, maxAlignment: 3));
        Assert.StartsWith("The most alignment is 1, 2, 4 or 8 bytes.", alignment.Message);
    }

    [Fact]
    public void On_an_avr_pointers_take_2_bytes_and_nothing_is_aligned_or_padded()
    {
        Assert.Equal(2, Avr.PointerSize);
        Assert.Equal(1, Avr.MaxAlignment);
        Assert.Equal(2, Avr.SizeOf(TirType.Ptr));
        Assert.Equal(2, Avr.SizeOf(TirType.NativeInt));
        Assert.Equal(4, Avr.SizeOf(TirType.I32));
        Assert.Equal(8, Avr.SizeOf(TirType.F64));
        Assert.All(new[] { TirType.Ptr, TirType.I16, TirType.I32, TirType.I64, TirType.F64 },
            type => Assert.Equal(1, Avr.AlignmentOf(type)));

        // struct Mixed { byte A; long B; short C; } and struct Node { byte Tag; void* Next; int Value; }
        var mixed = new TirStructType("Program.Mixed", ("A", TirType.I8), ("B", TirType.I64), ("C", TirType.I16));
        var node = new TirStructType("Program.Node", ("Tag", TirType.I8), ("Next", TirType.Ptr), ("Value", TirType.I32));
        Assert.Equal([0L, 1L, 9L], Avr.OffsetsOf(mixed));
        Assert.Equal(11, Avr.SizeOf(mixed));
        Assert.Equal(1, Avr.AlignmentOf(mixed));
        Assert.Equal([0L, 1L, 3L], Avr.OffsetsOf(node));
        Assert.Equal(7, Avr.SizeOf(node));
        Assert.Equal(21, Avr.SizeOf(new TirArrayType(node, 3)));
        Assert.Equal(8, Avr.SizeOf(new TirArrayType(TirType.Ptr, 4)));
    }

    [Fact]
    public void On_an_avr_an_array_object_has_its_length_after_a_2_byte_header_and_its_elements_from_8()
    {
        var node = TirTypeDescriptor.ObjectStruct("Program.Node", ("Next", TirType.Ref), ("Value", TirType.I32),
            ("Other", TirType.Ref));

        Assert.Equal(2, Avr.ArrayLengthOffset);
        Assert.Equal(8, Avr.ArrayDataOffset);
        Assert.Equal([2L, 8L], Avr.ReferenceOffsets(node));
        Assert.Equal(10, Avr.SizeOf(node));
    }

    [Fact]
    public void Void_has_no_size_and_a_field_out_of_range_has_no_offset()
    {
        Assert.Equal("void has no size: nothing of type void is ever in memory.",
            Assert.Throws<TirException>(() => Four.SizeOf(TirType.Void)).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => Four.OffsetOf(TirMemorySamples.Point, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Four.OffsetOf(TirMemorySamples.Point, -1));
    }

    [Fact]
    public void A_type_too_large_for_64_bits_is_an_error()
    {
        var huge = new TirArrayType(new TirArrayType(TirType.I64, int.MaxValue), int.MaxValue);
        var holder = new TirStructType("Program.Huge", ("A", TirType.I8), ("B", huge));

        var error = Assert.Throws<TirException>(() => Eight.SizeOf(holder));
        Assert.Equal("The type [2147483647 x [2147483647 x i64]] is too large: its size does not fit in 64 bits.",
            error.Message);
    }

    private static TirType Scalar(TirTypeKind kind) =>
        new[] { TirType.Bool, TirType.I8, TirType.I16, TirType.I32, TirType.I64, TirType.F32, TirType.F64 }
            .Single(t => t.Kind == kind);
}
