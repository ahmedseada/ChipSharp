// ChipSharp runtime for 32-bit RISC-V chips (RV32IMAC): the helper functions the compiler calls.
//
// RV32's M extension multiplies and divides 32-bit values, and LLVM makes a 64-bit product, the 64-bit shifts and
// compares out of 32-bit instructions; what it cannot make, it calls, and no library provides it here:
//
//   __udivdi3, __divdi3, __umoddi3, __moddi3   64-bit division and remainder (compiler-rt's names, as LLVM calls them
//                                              on every target but ARM's)
//   memcpy, memmove, memset, memcmp            the copies LLVM makes calls of (a struct or an array copied, cleared,
//                                              filled), and what the runtime's C calls
//
// Float and double arithmetic in software (no F or D) is compiler-rt's (the armv6m runtime's CompilerRt files, which
// define the plain names on RISC-V: __addsf3, __muldf3, __ltdf2, __fixdfsi, ...), and so are __clzsi2 and __clzdi2.
//
// Written with 32-bit division only: a 64-bit one would be compiled into a call of itself. The file is compiled with
// -ffreestanding -fno-builtin (the target's clang flags), so that the copy loops are not turned into calls to memcpy.

typedef unsigned int u32;
typedef unsigned long long u64;
typedef long long s64;
typedef __SIZE_TYPE__ size_t;
typedef __UINTPTR_TYPE__ uintptr_t;

// riscv32_failures.c (weak), or the chip runtime's: says so, and stops
__attribute__((noreturn)) void ChipSharp_Runtime_DivideByZero(void);

// ---- 64-bit division ----------------------------------------------------------------------------------------------

// The number of zero bits above the highest one bit of a value that is not 0.
static int leading_zeros(u64 x)
{
    int count = 0;
    for (u64 bit = 1ull << 63; (x & bit) == 0; bit >>= 1)
    {
        count++;
    }

    return count;
}

// Long division, one quotient bit per step, from where the divisor lines up with the dividend's highest bit; a 32-bit
// dividend and divisor take divu and remu. Division by zero stops the program (DivideByZeroException).
static u64 udivmod64(u64 dividend, u64 divisor, u64 *remainder)
{
    if (divisor == 0)
    {
        ChipSharp_Runtime_DivideByZero();
    }

    if ((dividend >> 32) == 0 && (divisor >> 32) == 0)
    {
        u32 a = (u32)dividend, b = (u32)divisor;
        *remainder = a % b;
        return a / b;
    }

    u64 quotient = 0;
    if (dividend >= divisor)
    {
        int shift = leading_zeros(divisor) - leading_zeros(dividend);
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

static u64 magnitude64(s64 value)
{
    return value < 0 ? 0ull - (u64)value : (u64)value;
}

u64 __udivdi3(u64 dividend, u64 divisor)
{
    u64 remainder;
    return udivmod64(dividend, divisor, &remainder);
}

u64 __umoddi3(u64 dividend, u64 divisor)
{
    u64 remainder;
    udivmod64(dividend, divisor, &remainder);
    return remainder;
}

// Signed division rounds toward zero, and the remainder has the dividend's sign (as in C and C#).
s64 __divdi3(s64 dividend, s64 divisor)
{
    u64 remainder;
    u64 quotient = udivmod64(magnitude64(dividend), magnitude64(divisor), &remainder);
    return (dividend ^ divisor) < 0 ? (s64)(0ull - quotient) : (s64)quotient;
}

s64 __moddi3(s64 dividend, s64 divisor)
{
    u64 remainder;
    udivmod64(magnitude64(dividend), magnitude64(divisor), &remainder);
    return dividend < 0 ? (s64)(0ull - remainder) : (s64)remainder;
}

// ---- Memory -------------------------------------------------------------------------------------------------------

// Word by word where both addresses are word-aligned (RV32 may trap on a word at an address that is not), else byte
// by byte.
void *memcpy(void *destination, const void *source, size_t count)
{
    unsigned char *to = destination;
    const unsigned char *from = source;
    if ((((uintptr_t)to | (uintptr_t)from) & 3) == 0)
    {
        for (; count >= 4; count -= 4, to += 4, from += 4)
        {
            *(u32 *)to = *(const u32 *)from;
        }
    }

    while (count-- > 0)
    {
        *to++ = *from++;
    }

    return destination;
}

void *memmove(void *destination, const void *source, size_t count)
{
    unsigned char *to = destination;
    const unsigned char *from = source;
    if (to <= from || to >= from + count)
    {
        return memcpy(destination, source, count);
    }

    // they overlap and the destination is after the source: from the end, back
    while (count-- > 0)
    {
        to[count] = from[count];
    }

    return destination;
}

void *memset(void *destination, int value, size_t count)
{
    unsigned char *to = destination;
    unsigned char byte = (unsigned char)value;
    if (((uintptr_t)to & 3) == 0)
    {
        u32 word = byte * 0x01010101u;
        for (; count >= 4; count -= 4, to += 4)
        {
            *(u32 *)to = word;
        }
    }

    while (count-- > 0)
    {
        *to++ = byte;
    }

    return destination;
}

int memcmp(const void *left, const void *right, size_t count)
{
    const unsigned char *a = left, *b = right;
    for (size_t i = 0; i < count; i++)
    {
        if (a[i] != b[i])
        {
            return a[i] < b[i] ? -1 : 1;
        }
    }

    return 0;
}
