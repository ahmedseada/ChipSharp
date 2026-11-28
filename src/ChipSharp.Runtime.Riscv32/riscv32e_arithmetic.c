// ChipSharp runtime for 32-bit RISC-V chips without M (the riscv32e runtime: RV32EC, the CH32V003's QingKe V2A): the
// multiplications and divisions the compiler calls.
//
// With no M extension (and no Zmmul) a core has no instruction to multiply or divide, and LLVM calls these for every
// product, quotient and remainder of two values it does not know (a product with a constant it makes of shifts and
// adds itself):
//
//   __mulsi3                          a 32-bit product (the low 32 bits: the same signed or not)
//   __udivsi3, __umodsi3              the quotient and the remainder of two 32-bit unsigned values
//   __divsi3, __modsi3                of two signed ones: the quotient rounds toward zero, the remainder has the
//                                     dividend's sign (as in C and C#)
//   __muldi3                          a 64-bit product (riscv32_helpers.c has the 64-bit division, compiler-rt the float)
//
// compiler-rt's names, as LLVM calls them on RISC-V. A division by zero gives what M's div and rem give (quotient all
// ones, remainder the dividend; a signed quotient -1), and the smallest int divided by -1 the smallest int, remainder
// 0: the code generator checks both before it divides (DivideByZeroException, OverflowException), so a program behaves
// as on a core with M.
//
// Written with shifts, adds and compares alone: a *, / or % of 32 or 64 bits here would be compiled into a call of
// these very functions. Long division, one bit a step, from where the divisor lines up with the dividend's highest bit;
// a product, one bit of the smaller factor a step.

typedef unsigned int u32;
typedef int s32;
typedef unsigned long long u64;

// The number of zero bits above the highest one bit of a value that is not 0.
static int leading_zeros32(u32 x)
{
    int count = 0;
    for (u32 bit = 1u << 31; (x & bit) == 0; bit >>= 1)
    {
        count++;
    }

    return count;
}

// The quotient and the remainder of two unsigned values; for a divisor of 0 what M's divu and remu give.
static u32 udivmod32(u32 dividend, u32 divisor, u32 *remainder)
{
    if (divisor == 0)
    {
        *remainder = dividend;
        return 0xffffffffu;
    }

    u32 quotient = 0;
    if (dividend >= divisor)
    {
        int shift = leading_zeros32(divisor) - leading_zeros32(dividend);
        divisor <<= shift; // its highest bit is now the dividend's: nothing is shifted out
        for (int step = 0; step <= shift; step++)
        {
            quotient <<= 1;
            if (dividend >= divisor)
            {
                dividend -= divisor;
                quotient |= 1;
            }

            divisor >>= 1;
        }
    }

    *remainder = dividend;
    return quotient;
}

static u32 magnitude32(s32 value)
{
    return value < 0 ? 0u - (u32)value : (u32)value;
}

u32 __mulsi3(u32 a, u32 b)
{
    if (a < b)
    {
        u32 larger = b;
        b = a;
        a = larger;
    }

    u32 product = 0;
    for (; b != 0; b >>= 1, a <<= 1)
    {
        if (b & 1)
        {
            product += a;
        }
    }

    return product;
}

u64 __muldi3(u64 a, u64 b)
{
    if (a < b)
    {
        u64 larger = b;
        b = a;
        a = larger;
    }

    u64 product = 0;
    for (; b != 0; b >>= 1, a <<= 1)
    {
        if (b & 1)
        {
            product += a;
        }
    }

    return product;
}

u32 __udivsi3(u32 dividend, u32 divisor)
{
    u32 remainder;
    return udivmod32(dividend, divisor, &remainder);
}

u32 __umodsi3(u32 dividend, u32 divisor)
{
    u32 remainder;
    udivmod32(dividend, divisor, &remainder);
    return remainder;
}

s32 __divsi3(s32 dividend, s32 divisor)
{
    if (divisor == 0)
    {
        return -1;
    }

    u32 remainder;
    u32 quotient = udivmod32(magnitude32(dividend), magnitude32(divisor), &remainder);
    return (dividend ^ divisor) < 0 ? (s32)(0u - quotient) : (s32)quotient;
}

s32 __modsi3(s32 dividend, s32 divisor)
{
    u32 remainder;
    udivmod32(magnitude32(dividend), magnitude32(divisor), &remainder);
    return dividend < 0 ? (s32)(0u - remainder) : (s32)remainder;
}
