// ChipSharp runtime for the chips SDCC compiles for (the STM8, the Padauk PDK14): fmodf, what the C code generator
// calls for C#'s % on two floats (IL's rem on float32). SDCC's libraries for these ports have no math functions
// (stm8.lib and pdk14.lib have no fmodf), so without this a program with a float % did not link: "?ASlink-Warning-
// Undefined Global _fmodf". (The LLVM targets have fmodf in chipsharp_float_math.c, which is clang's C.)
//
// The result is .NET's, and exact (IEEE 754's fmod rounds nothing): x - n * y for the whole number n nearest x / y
// toward zero, with the sign of x; NaN when x is infinite or NaN or y is 0 or NaN; x itself when |x| < |y| (y
// infinite too). It works on the bits alone, with 32-bit integers, as musl's fmodf does: the significands are divided
// bit by bit (a remainder below the divisor, shifted once per step of the exponents' difference), so neither SDCC's
// float arithmetic (lib/src/_fsadd.c and the others, which do not round as IEEE 754 does) nor its lack of subnormals
// and NaN touch it. Freestanding: no headers, as the other shared files.

// 32 bits: SDCC's unsigned long (its int has 16, enough for the exponents)
typedef unsigned long fmodf_u32;

typedef union
{
    float value;
    fmodf_u32 bits;
} chipsharp_fmodf_bits;

#define FMODF_SIGN 0x80000000ul
#define FMODF_INFINITY 0x7f800000ul
#define FMODF_HIDDEN 0x00800000ul
#define FMODF_FRACTION 0x007ffffful

// the NaN an invalid operation makes on the PC (x64's default NaN), where .NET's results come from
#define FMODF_DEFAULT_NAN 0xffc00000ul

float fmodf(float x, float y)
{
    chipsharp_fmodf_bits a, b;
    a.value = x;
    b.value = y;
    fmodf_u32 sign = a.bits & FMODF_SIGN;
    fmodf_u32 ax = a.bits & ~FMODF_SIGN;
    fmodf_u32 ay = b.bits & ~FMODF_SIGN;

    if (ax > FMODF_INFINITY)
    {
        return x; // x is NaN
    }

    if (ay > FMODF_INFINITY)
    {
        return y; // y is NaN
    }

    if (ax == FMODF_INFINITY || ay == 0)
    {
        a.bits = FMODF_DEFAULT_NAN;
        return a.value;
    }

    if (ax < ay)
    {
        return x; // |x| < |y|, or y infinite
    }

    if (ax == ay)
    {
        a.bits = sign; // a zero with the sign of x
        return a.value;
    }

    // the significands with their hidden bit, and the exponents; a subnormal one made normal, its exponent below 1
    int ex = (int)(ax >> 23);
    int ey = (int)(ay >> 23);
    fmodf_u32 mx, my;
    if (ex == 0)
    {
        mx = ax;
        ex = 1;
        while ((mx & FMODF_HIDDEN) == 0)
        {
            mx <<= 1;
            ex--;
        }
    }
    else
    {
        mx = (ax & FMODF_FRACTION) | FMODF_HIDDEN;
    }

    if (ey == 0)
    {
        my = ay;
        ey = 1;
        while ((my & FMODF_HIDDEN) == 0)
        {
            my <<= 1;
            ey--;
        }
    }
    else
    {
        my = (ay & FMODF_FRACTION) | FMODF_HIDDEN;
    }

    // mx below 2 * my at each step: subtract my when it fits, then the next bit
    for (; ex > ey; ex--)
    {
        if (mx >= my)
        {
            mx -= my;
        }

        mx <<= 1;
    }

    if (mx >= my)
    {
        mx -= my;
    }

    if (mx == 0)
    {
        a.bits = sign;
        return a.value;
    }

    // the remainder, mx * 2^(ey - 150), made normal again: a subnormal one is exact (its low bits are zeros)
    while ((mx & FMODF_HIDDEN) == 0)
    {
        mx <<= 1;
        ey--;
    }

    a.bits = ey > 0 ? sign | ((fmodf_u32)ey << 23) | (mx & FMODF_FRACTION) : sign | (mx >> (1 - ey));
    return a.value;
}
