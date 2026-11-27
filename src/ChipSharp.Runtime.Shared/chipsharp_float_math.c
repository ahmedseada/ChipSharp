// ChipSharp runtime, shared by every target: the float math of .NET programs, with .NET's results bit for bit.
//
//   fmod, fmodf                        what LLVM calls for C#'s % on a double or a float (IL's rem): x - n * y for
//                                      the whole number n nearest x / y toward zero, exactly, with the sign of x
//   ChipSharp_Runtime_SqrtFloat64      Math.Sqrt(double) and MathF.Sqrt(float): the square root rounded to nearest,
//   ChipSharp_Runtime_SqrtFloat32      as IEEE 754 says; -0 for -0, NaN below zero
//   ChipSharp_Runtime_AbsFloat64       Math.Abs(double), Math.Abs(float) and MathF.Abs(float): the value with its
//   ChipSharp_Runtime_AbsFloat32       sign bit cleared (NaN too)
//   ChipSharp_Runtime_MinFloat64/32    Math.Min and MathF.Min, Math.Max and MathF.Max: IEEE 754:2019's minimum and
//   ChipSharp_Runtime_MaxFloat64/32    maximum, as .NET has them: NaN when either value is NaN, and -0 below +0
//   ChipSharp_Runtime_FloorFloat64/32, Math.Floor, Ceiling, Truncate and Round(x) (to the nearest whole number, an
//   ...CeilingFloat64/32,              even one from halfway): the whole number below, above, toward zero, nearest;
//   ...TruncateFloat64/32,             the sign of x kept (Floor(-0.5) is -1, Ceiling(-0.5) is -0), NaN quiet
//   ...RoundFloat64/32
//   ChipSharp_Runtime_CopySign...,     Math.CopySign, BitIncrement, BitDecrement, MinMagnitude, MaxMagnitude, ILogB:
//   ...BitIncrement..., ...BitDecrement..., ...MinMagnitude..., ...MaxMagnitude..., ...ILogB...
//                                      on the bits, as System.Private.CoreLib writes them
//   ChipSharp_Runtime_ScaleB...,       Math.ScaleB, IEEERemainder, ReciprocalEstimate, ReciprocalSqrtEstimate: .NET's
//   ...IEEERemainder..., ...ReciprocalEstimate..., ...ReciprocalSqrtEstimate...
//                                      own float arithmetic, step for step (an estimate is 1 / x and 1 / Sqrt(x), what
//                                      .NET computes without the processor's estimate instructions)
//
// Every function but the last four works on the bits of its values with integer operations only, so the results
// cannot depend on a processor's float hardware or its lack (the Cortex-M0+ has none), and one implementation serves
// doubles and floats: the format is given by its number of fraction bits and exponent bits; the last four do what .NET
// does with + - * / %, each rounded as IEEE 754 says on every target. Freestanding: no headers, no C library.

#pragma STDC FP_CONTRACT OFF

typedef unsigned int u32;
typedef unsigned long long u64;

#define INLINE static inline __attribute__((always_inline))

typedef union
{
    double value;
    u64 bits;
} float64_bits;

typedef union
{
    float value;
    u32 bits;
} float32_bits;

static u64 bits64(double value)
{
    float64_bits number;
    number.value = value;
    return number.bits;
}

static double float64(u64 bits)
{
    float64_bits number;
    number.bits = bits;
    return number.value;
}

static u32 bits32(float value)
{
    float32_bits number;
    number.value = value;
    return number.bits;
}

static float float32(u64 bits)
{
    float32_bits number;
    number.bits = (u32)bits;
    return number.value;
}

// The sign bit of a format; the magnitude of a value is its bits without it.
INLINE u64 sign_bit(int fraction_bits, int exponent_bits)
{
    return (u64)1 << (fraction_bits + exponent_bits);
}

// The bits of +infinity: every exponent bit set, no fraction bit. A magnitude above it is a NaN.
INLINE u64 infinity(int fraction_bits, int exponent_bits)
{
    return sign_bit(fraction_bits, exponent_bits) - ((u64)1 << fraction_bits);
}

// The highest fraction bit: set, it makes a NaN quiet.
INLINE u64 quiet_bit(int fraction_bits)
{
    return (u64)1 << (fraction_bits - 1);
}

// A finite value that is not zero as significand * 2^(*exponent - bias - fraction_bits), with the significand's
// highest bit the implicit one, 2^fraction_bits; a subnormal value is shifted up to that, its exponent going below 1.
INLINE u64 unpack(u64 magnitude, int fraction_bits, int *exponent)
{
    u64 implicit = (u64)1 << fraction_bits;
    u64 fraction = magnitude & (implicit - 1);
    *exponent = (int)(magnitude >> fraction_bits);
    if (*exponent != 0)
    {
        return fraction | implicit;
    }

    *exponent = 1;
    while ((fraction & implicit) == 0)
    {
        fraction <<= 1;
        *exponent -= 1;
    }

    return fraction;
}

// fmod: long division of the significands, one bit per step from x's exponent down to y's, keeping only the
// remainder, which is exact. The remainder is smaller than y, so it has y's exponent or a lower one.
INLINE u64 remainder_bits(u64 x, u64 y, int fraction_bits, int exponent_bits)
{
    u64 sign = x & sign_bit(fraction_bits, exponent_bits);
    u64 magnitude_x = x & ~sign_bit(fraction_bits, exponent_bits);
    u64 magnitude_y = y & ~sign_bit(fraction_bits, exponent_bits);
    u64 inf = infinity(fraction_bits, exponent_bits);
    if (magnitude_x > inf)
    {
        return x | quiet_bit(fraction_bits);
    }

    if (magnitude_y > inf)
    {
        return y | quiet_bit(fraction_bits);
    }

    if (magnitude_y == 0 || magnitude_x == inf)
    {
        return inf | quiet_bit(fraction_bits);
    }

    if (magnitude_x < magnitude_y)
    {
        return x; // also when x is 0 or -0, and when x is finite and y infinite
    }

    if (magnitude_x == magnitude_y)
    {
        return sign;
    }

    int exponent_x;
    int exponent_y;
    u64 rest = unpack(magnitude_x, fraction_bits, &exponent_x);
    u64 divisor = unpack(magnitude_y, fraction_bits, &exponent_y);
    for (; exponent_x > exponent_y; exponent_x--)
    {
        if (rest >= divisor)
        {
            rest -= divisor;
        }

        rest <<= 1; // below 2 * divisor: fraction_bits + 2 bits at most
    }

    if (rest >= divisor)
    {
        rest -= divisor;
    }

    if (rest == 0)
    {
        return sign;
    }

    u64 implicit = (u64)1 << fraction_bits;
    int exponent = exponent_y;
    while ((rest & implicit) == 0)
    {
        rest <<= 1;
        exponent--;
    }

    if (exponent < 1)
    {
        // subnormal: the bits shifted out are zeros, as the remainder is a whole multiple of y's lowest bit
        return sign | (rest >> (1 - exponent));
    }

    return sign | ((u64)exponent << fraction_bits) | (rest & (implicit - 1));
}

// The square root of significand * 2^power, with power made even: the root's significand comes from the integer
// square root of the significand shifted up by fraction_bits + 2, worked out two bits of the radicand at a time,
// which gives fraction_bits + 2 bits of root (one more than the result keeps) and whether anything is left over.
INLINE u64 square_root_bits(u64 x, int fraction_bits, int exponent_bits)
{
    u64 sign = sign_bit(fraction_bits, exponent_bits);
    u64 inf = infinity(fraction_bits, exponent_bits);
    u64 magnitude = x & ~sign;
    if (magnitude > inf)
    {
        return x | quiet_bit(fraction_bits);
    }

    if (magnitude == 0 || x == inf)
    {
        return x; // 0, -0 and +infinity are their own roots
    }

    if ((x & sign) != 0)
    {
        return sign | inf | quiet_bit(fraction_bits); // the NaN the PC's float hardware makes
    }

    int exponent;
    u64 radicand = unpack(magnitude, fraction_bits, &exponent);
    int bias = (1 << (exponent_bits - 1)) - 1;
    int power = exponent - bias; // the value is radicand / 2^fraction_bits * 2^power
    if ((power & 1) != 0)
    {
        radicand <<= 1;
        power -= 1;
    }

    // root = floor(sqrt(radicand * 2^(fraction_bits + 2))); with an odd shift, one more bit goes into the radicand
    int shift = fraction_bits + 2;
    if ((shift & 1) != 0)
    {
        radicand <<= 1;
        shift -= 1;
    }

    int pairs = (fraction_bits + 4) / 2; // the radicand's two-bit digits; it is below 2^(fraction_bits + 4)
    int top = 2 * pairs - 2;
    u64 root = 0;
    u64 left = 0;
    for (int step = 0; step < pairs + shift / 2; step++)
    {
        left = (left << 2) | ((radicand >> top) & 3);
        radicand <<= 2;
        u64 trial = (root << 2) | 1;
        root <<= 1;
        if (left >= trial)
        {
            left -= trial;
            root |= 1;
        }
    }

    // to nearest, ties to even (a square root is never exactly halfway, but the rule costs nothing)
    u64 half = root & 1;
    root >>= 1;
    if (half != 0 && (left != 0 || (root & 1) != 0))
    {
        root++;
    }

    u64 implicit = (u64)1 << fraction_bits;
    if (root == implicit << 1)
    {
        root >>= 1;
        power += 2;
    }

    return ((u64)(power / 2 + bias) << fraction_bits) | (root & (implicit - 1));
}

// The order of values as unsigned integers: negative values below positive ones, -0 just below +0.
INLINE u64 order(u64 x, int fraction_bits, int exponent_bits)
{
    u64 sign = sign_bit(fraction_bits, exponent_bits);
    return (x & sign) != 0 ? ~x & (sign | (sign - 1)) : x | sign;
}

INLINE int is_nan(u64 x, int fraction_bits, int exponent_bits)
{
    return (x & ~sign_bit(fraction_bits, exponent_bits)) > infinity(fraction_bits, exponent_bits);
}

// Math.Min and Math.Max: the first value when it is NaN, else the second when it is NaN, else the lower (or higher).
INLINE u64 minimum_bits(u64 a, u64 b, int fraction_bits, int exponent_bits)
{
    if (is_nan(a, fraction_bits, exponent_bits))
    {
        return a;
    }

    if (is_nan(b, fraction_bits, exponent_bits))
    {
        return b;
    }

    return order(a, fraction_bits, exponent_bits) <= order(b, fraction_bits, exponent_bits) ? a : b;
}

INLINE u64 maximum_bits(u64 a, u64 b, int fraction_bits, int exponent_bits)
{
    if (is_nan(a, fraction_bits, exponent_bits))
    {
        return a;
    }

    if (is_nan(b, fraction_bits, exponent_bits))
    {
        return b;
    }

    return order(a, fraction_bits, exponent_bits) >= order(b, fraction_bits, exponent_bits) ? a : b;
}

double fmod(double x, double y)
{
    return float64(remainder_bits(bits64(x), bits64(y), 52, 11));
}

float fmodf(float x, float y)
{
    return float32(remainder_bits(bits32(x), bits32(y), 23, 8));
}

double ChipSharp_Runtime_SqrtFloat64(double value)
{
    return float64(square_root_bits(bits64(value), 52, 11));
}

float ChipSharp_Runtime_SqrtFloat32(float value)
{
    return float32(square_root_bits(bits32(value), 23, 8));
}

double ChipSharp_Runtime_AbsFloat64(double value)
{
    return float64(bits64(value) & ~sign_bit(52, 11));
}

float ChipSharp_Runtime_AbsFloat32(float value)
{
    return float32(bits32(value) & ~sign_bit(23, 8));
}

double ChipSharp_Runtime_MinFloat64(double value1, double value2)
{
    return float64(minimum_bits(bits64(value1), bits64(value2), 52, 11));
}

float ChipSharp_Runtime_MinFloat32(float value1, float value2)
{
    return float32(minimum_bits(bits32(value1), bits32(value2), 23, 8));
}

double ChipSharp_Runtime_MaxFloat64(double value1, double value2)
{
    return float64(maximum_bits(bits64(value1), bits64(value2), 52, 11));
}

float ChipSharp_Runtime_MaxFloat32(float value1, float value2)
{
    return float32(maximum_bits(bits32(value1), bits32(value2), 23, 8));
}

// ---- Floor, Ceiling, Truncate and Round -------------------------------------------------------------------------

#define TOWARD_ZERO 0
#define DOWN 1
#define UP 2
#define NEAREST_EVEN 3

// The whole number next to x in the direction of mode: TOWARD_ZERO, DOWN, UP, or NEAREST_EVEN (halfway: the even
// one). The result keeps the sign of x, so a value that becomes zero is a zero of that sign; a whole, infinite or zero
// x is itself, and a NaN is made quiet. Rounding a fraction up may carry into the exponent (1.5 to 2), which the bits
// do by themselves.
INLINE u64 integral_bits(u64 x, int mode, int fraction_bits, int exponent_bits)
{
    u64 sign = x & sign_bit(fraction_bits, exponent_bits);
    u64 magnitude = x & ~sign_bit(fraction_bits, exponent_bits);
    if (magnitude > infinity(fraction_bits, exponent_bits))
    {
        return x | quiet_bit(fraction_bits);
    }

    int bias = (1 << (exponent_bits - 1)) - 1;
    int exponent = (int)(magnitude >> fraction_bits) - bias;
    if (exponent >= fraction_bits || magnitude == 0)
    {
        return x;
    }

    u64 one = (u64)bias << fraction_bits;
    int up;
    if (exponent < 0)
    {
        // below 1: 0 or 1; to nearest, 1 above a half (exponent -1 and a fraction bit set)
        up = mode == DOWN ? sign != 0
            : mode == UP ? sign == 0
            : mode == NEAREST_EVEN ? exponent == -1 && magnitude != ((u64)(bias - 1) << fraction_bits)
            : 0;
        return sign | (up ? one : 0);
    }

    u64 unit = (u64)1 << (fraction_bits - exponent);
    u64 rest = magnitude & (unit - 1);
    if (rest == 0)
    {
        return x;
    }

    magnitude -= rest;
    up = mode == DOWN ? sign != 0
        : mode == UP ? sign == 0
        : mode == NEAREST_EVEN ? rest > unit / 2 || (rest == unit / 2 && (magnitude & unit) != 0)
        : 0;
    return sign | (up ? magnitude + unit : magnitude);
}

double ChipSharp_Runtime_FloorFloat64(double value)
{
    return float64(integral_bits(bits64(value), DOWN, 52, 11));
}

float ChipSharp_Runtime_FloorFloat32(float value)
{
    return float32(integral_bits(bits32(value), DOWN, 23, 8));
}

double ChipSharp_Runtime_CeilingFloat64(double value)
{
    return float64(integral_bits(bits64(value), UP, 52, 11));
}

float ChipSharp_Runtime_CeilingFloat32(float value)
{
    return float32(integral_bits(bits32(value), UP, 23, 8));
}

double ChipSharp_Runtime_TruncateFloat64(double value)
{
    return float64(integral_bits(bits64(value), TOWARD_ZERO, 52, 11));
}

float ChipSharp_Runtime_TruncateFloat32(float value)
{
    return float32(integral_bits(bits32(value), TOWARD_ZERO, 23, 8));
}

double ChipSharp_Runtime_RoundFloat64(double value)
{
    return float64(integral_bits(bits64(value), NEAREST_EVEN, 52, 11));
}

float ChipSharp_Runtime_RoundFloat32(float value)
{
    return float32(integral_bits(bits32(value), NEAREST_EVEN, 23, 8));
}

// ---- CopySign, BitIncrement, BitDecrement, MinMagnitude, MaxMagnitude, ILogB --------------------------------------

INLINE u64 copy_sign_bits(u64 x, u64 y, int fraction_bits, int exponent_bits)
{
    u64 sign = sign_bit(fraction_bits, exponent_bits);
    return (x & ~sign) | (y & sign);
}

// The next value up: that of -infinity is the lowest finite value, that of -0 the smallest positive one; NaN and
// +infinity are their own (NaN as it is, not made quiet).
INLINE u64 next_up_bits(u64 x, int fraction_bits, int exponent_bits)
{
    u64 sign = sign_bit(fraction_bits, exponent_bits);
    u64 inf = infinity(fraction_bits, exponent_bits);
    if ((x & ~sign) >= inf)
    {
        return x == (sign | inf) ? x - 1 : x;
    }

    return x == sign ? 1 : (x & sign) != 0 ? x - 1 : x + 1;
}

// The next value down: that of +infinity is the highest finite value, that of +0 the negative smallest one.
INLINE u64 next_down_bits(u64 x, int fraction_bits, int exponent_bits)
{
    u64 sign = sign_bit(fraction_bits, exponent_bits);
    u64 inf = infinity(fraction_bits, exponent_bits);
    if ((x & ~sign) >= inf)
    {
        return x == inf ? x - 1 : x;
    }

    return x == 0 ? sign | 1 : (x & sign) != 0 ? x + 1 : x - 1;
}

// MinMagnitude: x when it is NaN or nearer zero; of two values as far from it, the negative one. MaxMagnitude: x when
// it is NaN or farther; of two as far, the positive one.
INLINE u64 min_magnitude_bits(u64 x, u64 y, int fraction_bits, int exponent_bits)
{
    u64 sign = sign_bit(fraction_bits, exponent_bits);
    if (is_nan(x, fraction_bits, exponent_bits) || is_nan(y, fraction_bits, exponent_bits))
    {
        return is_nan(x, fraction_bits, exponent_bits) ? x : y;
    }

    u64 ax = x & ~sign;
    u64 ay = y & ~sign;
    return ax < ay ? x : ax > ay ? y : (x & sign) != 0 ? x : y;
}

INLINE u64 max_magnitude_bits(u64 x, u64 y, int fraction_bits, int exponent_bits)
{
    u64 sign = sign_bit(fraction_bits, exponent_bits);
    if (is_nan(x, fraction_bits, exponent_bits) || is_nan(y, fraction_bits, exponent_bits))
    {
        return is_nan(x, fraction_bits, exponent_bits) ? x : y;
    }

    u64 ax = x & ~sign;
    u64 ay = y & ~sign;
    return ax > ay ? x : ax < ay ? y : (x & sign) != 0 ? y : x;
}

// The exponent of x: int.MinValue for 0, int.MaxValue for infinity and NaN, below the lowest normal one for a
// subnormal value (by the leading zeros of its fraction).
INLINE int exponent_of(u64 x, int fraction_bits, int exponent_bits)
{
    u64 magnitude = x & ~sign_bit(fraction_bits, exponent_bits);
    if (magnitude == 0)
    {
        return -2147483647 - 1;
    }

    if (magnitude >= infinity(fraction_bits, exponent_bits))
    {
        return 2147483647;
    }

    int exponent;
    unpack(magnitude, fraction_bits, &exponent);
    return exponent - ((1 << (exponent_bits - 1)) - 1);
}

double ChipSharp_Runtime_CopySignFloat64(double x, double y)
{
    return float64(copy_sign_bits(bits64(x), bits64(y), 52, 11));
}

float ChipSharp_Runtime_CopySignFloat32(float x, float y)
{
    return float32(copy_sign_bits(bits32(x), bits32(y), 23, 8));
}

double ChipSharp_Runtime_BitIncrementFloat64(double value)
{
    return float64(next_up_bits(bits64(value), 52, 11));
}

float ChipSharp_Runtime_BitIncrementFloat32(float value)
{
    return float32(next_up_bits(bits32(value), 23, 8));
}

double ChipSharp_Runtime_BitDecrementFloat64(double value)
{
    return float64(next_down_bits(bits64(value), 52, 11));
}

float ChipSharp_Runtime_BitDecrementFloat32(float value)
{
    return float32(next_down_bits(bits32(value), 23, 8));
}

double ChipSharp_Runtime_MinMagnitudeFloat64(double x, double y)
{
    return float64(min_magnitude_bits(bits64(x), bits64(y), 52, 11));
}

float ChipSharp_Runtime_MinMagnitudeFloat32(float x, float y)
{
    return float32(min_magnitude_bits(bits32(x), bits32(y), 23, 8));
}

double ChipSharp_Runtime_MaxMagnitudeFloat64(double x, double y)
{
    return float64(max_magnitude_bits(bits64(x), bits64(y), 52, 11));
}

float ChipSharp_Runtime_MaxMagnitudeFloat32(float x, float y)
{
    return float32(max_magnitude_bits(bits32(x), bits32(y), 23, 8));
}

int ChipSharp_Runtime_ILogBFloat64(double value)
{
    return exponent_of(bits64(value), 52, 11);
}

int ChipSharp_Runtime_ILogBFloat32(float value)
{
    return exponent_of(bits32(value), 23, 8);
}

// ---- ScaleB, IEEERemainder, ReciprocalEstimate, ReciprocalSqrtEstimate: .NET's own arithmetic ---------------------

// Math.ScaleB: x * 2^n, as .NET computes it (from musl's scalbn): at most two scalings by 2^1023 or by 2^-969, then
// one by 2^n made from its bits, so that a result that becomes subnormal is rounded once.
double ChipSharp_Runtime_ScaleBFloat64(double x, int n)
{
    double y = x;
    if (n > 1023)
    {
        y *= 0x1p1023;
        n -= 1023;
        if (n > 1023)
        {
            y *= 0x1p1023;
            n -= 1023;
            if (n > 1023)
            {
                n = 1023;
            }
        }
    }
    else if (n < -1022)
    {
        y *= 0x1p-1022 * 0x1p53;
        n += 1022 - 53;
        if (n < -1022)
        {
            y *= 0x1p-1022 * 0x1p53;
            n += 1022 - 53;
            if (n < -1022)
            {
                n = -1022;
            }
        }
    }

    return y * float64((u64)(0x3ff + n) << 52);
}

float ChipSharp_Runtime_ScaleBFloat32(float x, int n)
{
    float y = x;
    if (n > 127)
    {
        y *= 0x1p127f;
        n -= 127;
        if (n > 127)
        {
            y *= 0x1p127f;
            n -= 127;
            if (n > 127)
            {
                n = 127;
            }
        }
    }
    else if (n < -126)
    {
        y *= 0x1p-126f * 0x1p24f;
        n += 126 - 24;
        if (n < -126)
        {
            y *= 0x1p-126f * 0x1p24f;
            n += 126 - 24;
            if (n < -126)
            {
                n = -126;
            }
        }
    }

    return y * float32((u32)(0x7f + n) << 23);
}

// Math.IEEERemainder, as .NET computes it: x % y, or that minus |y| toward the sign of x when that is nearer zero; of
// two as near, the one that x / y rounded to even gives. A NaN x or y is the result as it is; when x % y is NaN, .NET's
// NaN (0xFFF8000000000000).
double ChipSharp_Runtime_IEEERemainderFloat64(double x, double y)
{
    if (x != x)
    {
        return x;
    }

    if (y != y)
    {
        return y;
    }

    double regular = fmod(x, y);
    if (regular != regular)
    {
        return float64(0xfff8000000000000ULL);
    }

    if (regular == 0 && (bits64(x) & sign_bit(52, 11)) != 0)
    {
        return float64(sign_bit(52, 11));
    }

    double sign = x < 0 ? -1.0 : x > 0 ? 1.0 : 0.0;
    double alternative = regular - ChipSharp_Runtime_AbsFloat64(y) * sign;
    double abs_alternative = ChipSharp_Runtime_AbsFloat64(alternative);
    double abs_regular = ChipSharp_Runtime_AbsFloat64(regular);
    if (abs_alternative == abs_regular)
    {
        double division = x / y;
        double rounded = ChipSharp_Runtime_RoundFloat64(division);
        return ChipSharp_Runtime_AbsFloat64(rounded) > ChipSharp_Runtime_AbsFloat64(division) ? alternative : regular;
    }

    return abs_alternative < abs_regular ? alternative : regular;
}

float ChipSharp_Runtime_IEEERemainderFloat32(float x, float y)
{
    if (x != x)
    {
        return x;
    }

    if (y != y)
    {
        return y;
    }

    float regular = fmodf(x, y);
    if (regular != regular)
    {
        return float32(0xffc00000U);
    }

    if (regular == 0 && (bits32(x) & sign_bit(23, 8)) != 0)
    {
        return float32(sign_bit(23, 8));
    }

    float sign = x < 0 ? -1.0f : x > 0 ? 1.0f : 0.0f;
    float alternative = regular - ChipSharp_Runtime_AbsFloat32(y) * sign;
    float abs_alternative = ChipSharp_Runtime_AbsFloat32(alternative);
    float abs_regular = ChipSharp_Runtime_AbsFloat32(regular);
    if (abs_alternative == abs_regular)
    {
        float division = x / y;
        float rounded = ChipSharp_Runtime_RoundFloat32(division);
        return ChipSharp_Runtime_AbsFloat32(rounded) > ChipSharp_Runtime_AbsFloat32(division) ? alternative : regular;
    }

    return abs_alternative < abs_regular ? alternative : regular;
}

// Math.ReciprocalEstimate and ReciprocalSqrtEstimate: what .NET computes where the processor has no estimate
// instructions (and for doubles on x64): 1 / x and 1 / Sqrt(x), each rounded to nearest.
double ChipSharp_Runtime_ReciprocalEstimateFloat64(double value)
{
    return 1.0 / value;
}

float ChipSharp_Runtime_ReciprocalEstimateFloat32(float value)
{
    return 1.0f / value;
}

double ChipSharp_Runtime_ReciprocalSqrtEstimateFloat64(double value)
{
    return 1.0 / ChipSharp_Runtime_SqrtFloat64(value);
}

float ChipSharp_Runtime_ReciprocalSqrtEstimateFloat32(float value)
{
    return 1.0f / ChipSharp_Runtime_SqrtFloat32(value);
}
