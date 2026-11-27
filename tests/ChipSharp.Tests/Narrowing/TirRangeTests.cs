using ChipSharp.Ir;
using ChipSharp.Ir.Optimizations;

namespace ChipSharp.Tests.Narrowing;

/// <summary>
/// The arithmetic of ranges: what each operation gives for every pair of values in two ranges, wrapping as C# does, at
/// the edges of each width (255 + 1, -128 / -1, int.MaxValue + 1, shifts by the width or more, negative values read as
/// unsigned).
/// </summary>
public sealed class TirRangeTests
{
    private static TirRange R(long min, long max) => TirRange.Of(min, max);

    private static TirRange R(long value) => TirRange.Of(value);

    [Fact]
    public void A_byte_plus_one_is_256_in_32_bits_and_wraps_to_0_in_8()
    {
        Assert.Equal(R(256), TirRange.Binary(TirBinaryOp.Add, R(255), R(1), 32));
        Assert.Equal(R(0), TirRange.Binary(TirBinaryOp.Add, R(-1), R(1), 8)); // an i8 holding 255 reads -1

        var next = TirRange.Binary(TirBinaryOp.Add, TirRange.Unsigned(8), R(1), 32);
        Assert.Equal(R(1, 256), next);
        Assert.False(next.FitsUnsigned(8));
        Assert.False(next.FitsSigned(8));
        Assert.True(next.FitsUnsigned(16));
    }

    [Fact]
    public void Arithmetic_that_runs_past_the_end_of_the_width_wraps_or_becomes_every_value()
    {
        Assert.Equal(R(int.MinValue), TirRange.Binary(TirBinaryOp.Add, R(int.MaxValue), R(1), 32));
        Assert.Equal(R(int.MaxValue), TirRange.Binary(TirBinaryOp.Sub, R(int.MinValue), R(1), 32));
        Assert.Equal(TirRange.Signed(32), TirRange.Binary(TirBinaryOp.Add, R(0, int.MaxValue), R(1), 32));
        Assert.Equal(R(-56, -6), TirRange.Binary(TirBinaryOp.Add, R(100, 150), R(100), 8));
        Assert.Equal(R(long.MinValue), TirRange.Binary(TirBinaryOp.Add, R(long.MaxValue), R(1), 64));
        Assert.Equal(TirRange.Signed(64), TirRange.Binary(TirBinaryOp.Mul, R(1L << 40), R(1, 1L << 30), 64));
        Assert.Equal(R(-128 * 127, 128 * 128), TirRange.Binary(TirBinaryOp.Mul, TirRange.Signed(8), TirRange.Signed(8), 32));
    }

    [Fact]
    public void The_smallest_value_divided_by_minus_one_overflows_its_width_and_the_remainder_has_the_dividend_s_sign()
    {
        Assert.Equal(R(128), TirRange.Binary(TirBinaryOp.Div, R(-128), R(-1), 32));
        Assert.Equal(R(-128), TirRange.Binary(TirBinaryOp.Div, R(-128), R(-1), 8));
        Assert.Equal(R(-128, 128), TirRange.Binary(TirBinaryOp.Div, TirRange.Signed(8), TirRange.Signed(8), 32));
        Assert.Equal(R(-3, 3), TirRange.Binary(TirBinaryOp.Div, R(-7, 7), R(2, 5), 32));
        Assert.Equal(R(-2, 2), TirRange.Binary(TirBinaryOp.Rem, R(-7, 7), R(3), 32));
        Assert.Equal(R(-2, 0), TirRange.Binary(TirBinaryOp.Rem, R(-7, -1), R(-3), 32));
        Assert.Equal(R(0, 9), TirRange.Binary(TirBinaryOp.Rem, R(0, int.MaxValue), R(10), 32));
    }

    [Fact]
    public void A_divisor_of_zero_adds_nothing_as_the_division_is_checked_before()
    {
        Assert.Equal(R(10, 100), TirRange.Binary(TirBinaryOp.Div, R(100), R(0, 10), 32));
        Assert.True(TirRange.Binary(TirBinaryOp.Div, R(100), R(0), 32).IsEmpty);
        Assert.True(TirRange.Binary(TirBinaryOp.RemUnsigned, R(100), R(0), 32).IsEmpty);
    }

    [Fact]
    public void Unsigned_operations_read_a_negative_value_as_a_large_one()
    {
        Assert.Equal(R(int.MaxValue), TirRange.Binary(TirBinaryOp.DivUnsigned, R(-1), R(2), 32));
        Assert.Equal(R(0, 9), TirRange.Binary(TirBinaryOp.RemUnsigned, R(-5, 5), R(10), 32));
        Assert.Equal(R(3, 5), TirRange.Binary(TirBinaryOp.RemUnsigned, R(3, 5), R(10, 20), 32)); // below every divisor: itself
        Assert.Equal(R(15), TirRange.Binary(TirBinaryOp.ShrUnsigned, R(-1), R(28), 32));
        Assert.Equal(R(0, (1L << 31) - 1), TirRange.Binary(TirBinaryOp.ShrUnsigned, R(-1, 1), R(1), 32));
    }

    [Fact]
    public void Shifts_follow_the_amount_and_an_amount_outside_the_width_may_be_any_amount()
    {
        Assert.Equal(R(1, 128), TirRange.Binary(TirBinaryOp.Shl, R(1), R(0, 7), 32));
        Assert.Equal(R(int.MinValue), TirRange.Binary(TirBinaryOp.Shl, R(1), R(31), 32));
        Assert.Equal(TirRange.Signed(32), TirRange.Binary(TirBinaryOp.Shl, R(1), R(0, 40), 32));
        Assert.Equal(R(-64, 63), TirRange.Binary(TirBinaryOp.Shr, TirRange.Signed(8), R(1), 32));
        Assert.Equal(R(-1, 0), TirRange.Binary(TirBinaryOp.Shr, R(-128, 127), R(7, 31), 32));
        Assert.Equal(R(0, 255), TirRange.Binary(TirBinaryOp.ShrUnsigned, R(0, 255), R(0, 3), 32));
    }

    [Fact]
    public void Masks_keep_the_bits_both_sides_have()
    {
        Assert.Equal(R(0, 255), TirRange.Binary(TirBinaryOp.And, TirRange.Signed(32), R(0xFF), 32));
        Assert.Equal(R(0, 5), TirRange.Binary(TirBinaryOp.And, R(0, 5), R(-16, -1), 32));
        Assert.Equal(R(-16, -16), TirRange.Binary(TirBinaryOp.And, R(-1), R(-16), 32));
        Assert.Equal(R(8, 15), TirRange.Binary(TirBinaryOp.Or, R(0, 5), R(8), 32));
        Assert.Equal(R(int.MinValue, -1), TirRange.Binary(TirBinaryOp.Or, R(0, 5), R(-8), 32));
        Assert.Equal(R(0, 127), TirRange.Binary(TirBinaryOp.Xor, R(0, 100), R(0, 3), 32));
    }

    [Fact]
    public void Conversions_extend_by_sign_or_zero_and_cut_keeping_the_low_bits()
    {
        Assert.Equal(R(0, 255), TirRange.Convert(TirRange.Signed(8), 8, 32, zeroExtend: true));
        Assert.Equal(R(255), TirRange.Convert(R(-1), 8, 32, zeroExtend: true));
        Assert.Equal(R(-1), TirRange.Convert(R(-1), 8, 32, zeroExtend: false));
        Assert.Equal(R(44), TirRange.Convert(R(300), 32, 8, zeroExtend: false));
        Assert.Equal(R(-56, -6), TirRange.Convert(R(200, 250), 32, 8, zeroExtend: false));
        Assert.Equal(TirRange.Signed(8), TirRange.Convert(R(0, 300), 32, 8, zeroExtend: false));
        Assert.Equal(R(uint.MaxValue), TirRange.Convert(R(-1), 32, 64, zeroExtend: true));
    }

    [Fact]
    public void A_range_fits_a_width_when_extending_its_low_bits_gives_every_value_back()
    {
        Assert.True(R(-128, 127).FitsSigned(8));
        Assert.False(R(-129, 0).FitsSigned(8));
        Assert.True(R(0, 255).FitsUnsigned(8));
        Assert.False(R(0, 256).FitsUnsigned(8));
        Assert.False(R(-1, 0).FitsUnsigned(16));
        Assert.True(R(0, 127).FitsUnsigned(8) && R(0, 127).FitsSigned(8));
        Assert.False(TirRange.Empty.FitsSigned(64));
        Assert.Equal("[0, 255]", TirRange.Unsigned(8).ToString());
        Assert.Equal("[7]", R(7).ToString());
        Assert.Equal("empty", TirRange.Empty.ToString());
    }

    [Fact]
    public void Only_i8_to_i64_have_ranges_nint_whose_width_the_target_decides_has_none()
    {
        Assert.Equal(new[] { 8, 16, 32, 64, 0, 0, 0 },
            new[] { TirType.I8, TirType.I16, TirType.I32, TirType.I64, TirType.NativeInt, TirType.Bool, TirType.Ptr }
                .Select(TirRange.BitsOf));
        Assert.Equal(TirRange.Signed(64), TirRange.Any(TirType.NativeInt));
    }
}
