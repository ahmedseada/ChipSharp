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
//
// Every function works on the bits of its values with integer operations only, so the results cannot depend on a
// processor's float hardware or its lack (the Cortex-M0+ has none), and one implementation serves doubles and
// floats: the format is given by its number of fraction bits and exponent bits. Freestanding: no headers, no C
// library.

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
