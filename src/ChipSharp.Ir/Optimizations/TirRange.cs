using System.Globalization;

namespace ChipSharp.Ir.Optimizations;

/// <summary>
/// The values an integer of some width can have, as an interval of its signed reading: an i8 that holds 200 (as a
/// <c>byte</c> does) reads -56, so a byte's range [0, 255] is the range of the i32 it widens to, and an i8 holding any
/// byte is [-128, 127]. Empty when no value is possible (code that never runs). The operations say what each TIR
/// operation gives for every pair of values in two ranges, with C#'s wrap-around: a result that does not fit the width
/// wraps as the operation does, and a range that wraps past the end of the width becomes the whole width.
/// </summary>
public readonly record struct TirRange
{
    private TirRange(long min, long max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>No value: what code that never runs computes.</summary>
    public static TirRange Empty { get; } = new(1, 0);

    /// <summary>The smallest value (signed reading); meaningless when <see cref="IsEmpty"/>.</summary>
    public long Min { get; }

    /// <summary>The largest value (signed reading); meaningless when <see cref="IsEmpty"/>.</summary>
    public long Max { get; }

    public bool IsEmpty => Min > Max;

    /// <summary>True for a range of one value.</summary>
    public bool IsConstant => Min == Max;

    /// <summary>The values from <paramref name="min"/> to <paramref name="max"/>; empty when max is below min.</summary>
    public static TirRange Of(long min, long max) => min > max ? Empty : new TirRange(min, max);

    /// <summary>One value.</summary>
    public static TirRange Of(long value) => new(value, value);

    /// <summary>Every value of a <paramref name="bits"/>-bit integer, read as signed: [-2^(bits-1), 2^(bits-1) - 1].</summary>
    public static TirRange Signed(int bits) => new(SignedMin(bits), SignedMax(bits));

    /// <summary>[0, 2^bits - 1]: every value of an unsigned integer of fewer than 64 bits.</summary>
    public static TirRange Unsigned(int bits) => bits < 64
        ? new TirRange(0, (1L << bits) - 1)
        : throw new ArgumentOutOfRangeException(nameof(bits), "An unsigned range has fewer than 64 bits.");

    /// <summary>True when every value is that of a signed <paramref name="bits"/>-bit integer: sign-extending its low bits gives it back.</summary>
    public bool FitsSigned(int bits) => !IsEmpty && Min >= SignedMin(bits) && Max <= SignedMax(bits);

    /// <summary>True when every value is that of an unsigned <paramref name="bits"/>-bit integer: zero-extending its low bits gives it back.</summary>
    public bool FitsUnsigned(int bits) => !IsEmpty && Min >= 0 && (bits >= 63 || Max <= (1L << bits) - 1);

    /// <summary>True when <paramref name="value"/> is one of the values.</summary>
    public bool Contains(long value) => value >= Min && value <= Max;

    /// <summary>Every value of either range (and those between).</summary>
    public TirRange Join(TirRange other) =>
        IsEmpty ? other : other.IsEmpty ? this : new TirRange(Math.Min(Min, other.Min), Math.Max(Max, other.Max));

    /// <summary>The values in both.</summary>
    public TirRange Intersect(TirRange other) =>
        IsEmpty || other.IsEmpty ? Empty : Of(Math.Max(Min, other.Min), Math.Min(Max, other.Max));

    /// <summary>"[0, 255]", "[7]" or "empty".</summary>
    public override string ToString() => IsEmpty ? "empty"
        : IsConstant ? string.Create(CultureInfo.InvariantCulture, $"[{Min}]")
        : string.Create(CultureInfo.InvariantCulture, $"[{Min}, {Max}]");

    /// <summary>
    /// What <paramref name="op"/> gives on <paramref name="bits"/>-bit integers in the two ranges. A division or remainder
    /// is never by zero (the translation checks the divisor first, see ChipSharp.Frontend's DivisionFunctions), so a zero
    /// divisor adds nothing; a shift amount is below the width (C# masks it), so a larger one is read as any amount.
    /// </summary>
    public static TirRange Binary(TirBinaryOp op, TirRange left, TirRange right, int bits)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return Empty;
        }

        Int128 lMin = left.Min, lMax = left.Max, rMin = right.Min, rMax = right.Max;
        switch (op)
        {
            case TirBinaryOp.Add:
                return Wrap(lMin + rMin, lMax + rMax, bits);

            case TirBinaryOp.Sub:
                return Wrap(lMin - rMax, lMax - rMin, bits);

            case TirBinaryOp.Mul:
                return Corners(lMin * rMin, lMin * rMax, lMax * rMin, lMax * rMax, bits);

            case TirBinaryOp.Div:
                return Divide(left, right, bits);

            case TirBinaryOp.Rem:
                return Remainder(left, right);

            case TirBinaryOp.DivUnsigned:
            {
                var (aMin, aMax) = UnsignedView(left, bits);
                var (bMin, bMax) = UnsignedView(right, bits);
                if (bMax == 0)
                {
                    return Empty;
                }

                return Wrap(aMin / bMax, aMax / Int128.Max(bMin, 1), bits);
            }

            case TirBinaryOp.RemUnsigned:
            {
                var (aMin, aMax) = UnsignedView(left, bits);
                var (bMin, bMax) = UnsignedView(right, bits);
                if (bMax == 0)
                {
                    return Empty;
                }

                // a % b is a itself when a is below every b
                return aMax < bMin ? Wrap(aMin, aMax, bits) : Wrap(0, Int128.Min(aMax, bMax - 1), bits);
            }

            case TirBinaryOp.And:
                if (left.IsConstant && right.IsConstant)
                {
                    return Of(left.Min & right.Min);
                }

                // the result has only bits both have: no larger than a value that is not negative
                if (left.Min >= 0 || right.Min >= 0)
                {
                    return Of(0, Math.Min(left.Min >= 0 ? left.Max : long.MaxValue, right.Min >= 0 ? right.Max : long.MaxValue));
                }

                return left.Max < 0 && right.Max < 0 ? Of(SignedMin(bits), Math.Min(left.Max, right.Max)) : Signed(bits);

            case TirBinaryOp.Or:
                if (left.IsConstant && right.IsConstant)
                {
                    return Of(left.Min | right.Min);
                }

                if (left.Min >= 0 && right.Min >= 0)
                {
                    return Of(Math.Max(left.Min, right.Min), AllOnes(Math.Max(left.Max, right.Max)));
                }

                // a negative operand keeps the sign bit, and the result is no smaller than it
                return left.Max < 0 || right.Max < 0 ? Of(SignedMin(bits), -1) : Signed(bits);

            case TirBinaryOp.Xor:
                if (left.IsConstant && right.IsConstant)
                {
                    return Of(left.Min ^ right.Min);
                }

                return left.Min >= 0 && right.Min >= 0 ? Of(0, AllOnes(Math.Max(left.Max, right.Max))) : Signed(bits);

            case TirBinaryOp.Shl:
            {
                var (kMin, kMax) = Amount(right, bits);
                return Corners(lMin << kMin, lMin << kMax, lMax << kMin, lMax << kMax, bits);
            }

            case TirBinaryOp.Shr:
            {
                var (kMin, kMax) = Amount(right, bits);
                return Corners(lMin >> kMin, lMin >> kMax, lMax >> kMin, lMax >> kMax, bits);
            }

            case TirBinaryOp.ShrUnsigned:
            {
                var (kMin, kMax) = Amount(right, bits);
                var (aMin, aMax) = UnsignedView(left, bits);
                return Wrap(aMin >> kMax, aMax >> kMin, bits);
            }

            default:
                return Signed(bits);
        }
    }

    /// <summary>
    /// What converting an integer in <paramref name="value"/> from <paramref name="fromBits"/> to <paramref name="toBits"/>
    /// gives: widening sign-extends, or zero-extends when <paramref name="zeroExtend"/>; narrowing keeps the low bits;
    /// the same width changes nothing (nint and an integer of the pointer's width).
    /// </summary>
    public static TirRange Convert(TirRange value, int fromBits, int toBits, bool zeroExtend)
    {
        if (value.IsEmpty || fromBits == toBits)
        {
            return value;
        }

        if (toBits < fromBits)
        {
            return Wrap(value.Min, value.Max, toBits);
        }

        if (!zeroExtend)
        {
            return value;
        }

        var (min, max) = UnsignedView(value, fromBits);
        return Wrap(min, max, toBits);
    }

    /// <summary>
    /// The values of the range read as unsigned <paramref name="bits"/>-bit integers, as an interval: the negative ones
    /// are 2^bits more; a range with both signs is every unsigned value.
    /// </summary>
    internal static (Int128 Min, Int128 Max) UnsignedView(TirRange range, int bits)
    {
        var modulus = Int128.One << bits;
        return range.Min >= 0 ? (range.Min, range.Max)
            : range.Max < 0 ? (range.Min + modulus, range.Max + modulus)
            : (0, modulus - 1);
    }

    /// <summary>
    /// The interval [min, max] of exact results, as <paramref name="bits"/>-bit integers wrap them: the same when it fits;
    /// shifted by a multiple of 2^bits when it fits after that; every value when it crosses the end of the width.
    /// </summary>
    internal static TirRange Wrap(Int128 min, Int128 max, int bits)
    {
        if (min > max)
        {
            return Empty;
        }

        if (max - min >= Int128.One << bits)
        {
            return Signed(bits);
        }

        var (low, high) = (WrapValue(min, bits), WrapValue(max, bits));
        return low <= high ? new TirRange(low, high) : Signed(bits);
    }

    private static long WrapValue(Int128 value, int bits)
    {
        var modulus = Int128.One << bits;
        var half = Int128.One << (bits - 1);
        var shifted = (value + half) % modulus;
        if (shifted < 0)
        {
            shifted += modulus;
        }

        return (long)(shifted - half);
    }

    private static TirRange Corners(Int128 a, Int128 b, Int128 c, Int128 d, int bits) =>
        Wrap(Int128.Min(Int128.Min(a, b), Int128.Min(c, d)), Int128.Max(Int128.Max(a, b), Int128.Max(c, d)), bits);

    /// <summary>A shift amount: below the width, as C# compilers mask it; outside that, any amount.</summary>
    private static (int Min, int Max) Amount(TirRange amount, int bits) =>
        amount.Min >= 0 && amount.Max < bits ? ((int)amount.Min, (int)amount.Max) : (0, bits - 1);

    /// <summary>Truncating division over the divisors other than 0: at the corners, for the negative and positive divisors apart.</summary>
    private static TirRange Divide(TirRange left, TirRange right, int bits)
    {
        Int128? min = null, max = null;
        foreach (var (low, high) in new[] { (right.Min, Math.Min(right.Max, -1)), (Math.Max(right.Min, 1), right.Max) })
        {
            if (low > high)
            {
                continue;
            }

            foreach (var quotient in new[] { (Int128)left.Min / low, (Int128)left.Min / high, (Int128)left.Max / low, (Int128)left.Max / high })
            {
                min = min is null ? quotient : Int128.Min(min.Value, quotient);
                max = max is null ? quotient : Int128.Max(max.Value, quotient);
            }
        }

        return min is null ? Empty : Wrap(min.Value, max!.Value, bits);
    }

    /// <summary>The remainder has the sign of the dividend and is smaller than the divisor in size.</summary>
    private static TirRange Remainder(TirRange left, TirRange right)
    {
        if (right.Min == 0 && right.Max == 0)
        {
            return Empty;
        }

        var largest = Int128.Max(Int128.Abs(right.Min), Int128.Abs(right.Max)) - 1;
        var bound = largest > long.MaxValue ? long.MaxValue : (long)largest;
        return left.Min >= 0 ? Of(0, Math.Min(left.Max, bound))
            : left.Max <= 0 ? Of(Math.Max(left.Min, -bound), 0)
            : Of(Math.Max(left.Min, -bound), Math.Min(left.Max, bound));
    }

    /// <summary>The smallest 2^k - 1 that is no less than <paramref name="value"/> (not negative).</summary>
    private static long AllOnes(long value)
    {
        var ones = 0L;
        while (ones < value)
        {
            ones = (ones << 1) | 1;
        }

        return ones;
    }

    private static long SignedMin(int bits) => bits >= 64 ? long.MinValue : -(1L << (bits - 1));

    private static long SignedMax(int bits) => bits >= 64 ? long.MaxValue : (1L << (bits - 1)) - 1;
}
