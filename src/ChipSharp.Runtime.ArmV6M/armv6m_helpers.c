// ChipSharp runtime for ARMv6-M chips (Cortex-M0 and M0+): the helper functions the compiler calls.
//
// ARMv6-M has no divide instruction, no 64-bit multiply and no 64-bit shifts, so LLVM turns those operations into
// calls to the ARM run-time ABI (RTABI) functions below, and copies of memory into calls to __aeabi_memcpy and
// friends. No library provides them here, so the runtime does.
//
// Every function is written with 32-bit operations only. That matters: written with 64-bit types, a helper would
// be compiled into a call to itself. The file is compiled with -ffreestanding -fno-builtin (from the target's
// clang flags) so that the copy loops are not turned into calls to memcpy either.
//
//   __aeabi_uidiv, __aeabi_idiv            32-bit division
//   __aeabi_uidivmod, __aeabi_idivmod      32-bit division and remainder (quotient in r0, remainder in r1)
//   __aeabi_uldivmod, __aeabi_ldivmod      64-bit division and remainder (in armv6m_divmod.S: quotient in r0:r1,
//                                          remainder in r2:r3), built on chipsharp_udivmod64, chipsharp_divmod64
//   __aeabi_lmul                           64-bit multiply
//   __aeabi_llsl, __aeabi_llsr, __aeabi_lasr  64-bit shifts
//   __aeabi_memcpy, __aeabi_memmove, __aeabi_memset, __aeabi_memclr (and their 4 and 8 forms), memcpy, memmove,
//   memset, memcmp
//
// Division by zero calls chipsharp_divide_by_zero, which stops the program (a breakpoint, then sleep forever).

typedef unsigned int u32;
typedef int s32;
typedef unsigned long long u64;
typedef __SIZE_TYPE__ size_t;

// What a division by zero does. C# would throw DivideByZeroException; until exceptions arrive the program stops.
__attribute__((noreturn, noinline)) void chipsharp_divide_by_zero(void)
{
#if defined(__arm__)
    for (;;)
    {
        __asm__ volatile("bkpt #0\n\twfi");
    }
#else
    __builtin_trap(); // this file built for a PC, as the tests do to check the arithmetic
#endif
}

// The number of zero bits above the highest one bit (32 for zero). ARMv6-M has no CLZ instruction, and
// __builtin_clz would call __clzsi2, which nothing provides.
static int leading_zeros(u32 x)
{
    if (x == 0)
    {
        return 32;
    }

    int count = 0;
    if ((x & 0xFFFF0000u) == 0)
    {
        count += 16;
        x <<= 16;
    }

    if ((x & 0xFF000000u) == 0)
    {
        count += 8;
        x <<= 8;
    }

    if ((x & 0xF0000000u) == 0)
    {
        count += 4;
        x <<= 4;
    }

    if ((x & 0xC0000000u) == 0)
    {
        count += 2;
        x <<= 2;
    }

    if ((x & 0x80000000u) == 0)
    {
        count += 1;
    }

    return count;
}

// Long division, one quotient bit per step, starting where the divisor lines up with the dividend's highest bit.
static u32 udivmod32(u32 dividend, u32 divisor, u32 *remainder)
{
    if (divisor == 0)
    {
        chipsharp_divide_by_zero();
    }

    u32 quotient = 0;
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

// Two 32-bit values in the r0:r1 pair a function returns a 64-bit value in (low word in r0).
static u64 pair(u32 low, u32 high)
{
    return ((u64)high << 32) | low;
}

static u32 magnitude32(s32 value)
{
    return value < 0 ? 0u - (u32)value : (u32)value;
}

u32 __aeabi_uidiv(u32 dividend, u32 divisor)
{
    u32 remainder;
    return udivmod32(dividend, divisor, &remainder);
}

u64 __aeabi_uidivmod(u32 dividend, u32 divisor)
{
    u32 remainder;
    u32 quotient = udivmod32(dividend, divisor, &remainder);
    return pair(quotient, remainder);
}

// Signed division rounds toward zero, and the remainder has the dividend's sign (as in C and C#).
s32 __aeabi_idiv(s32 dividend, s32 divisor)
{
    u32 remainder;
    u32 quotient = udivmod32(magnitude32(dividend), magnitude32(divisor), &remainder);
    return (dividend ^ divisor) < 0 ? (s32)(0u - quotient) : (s32)quotient;
}

u64 __aeabi_idivmod(s32 dividend, s32 divisor)
{
    u32 remainder;
    u32 quotient = udivmod32(magnitude32(dividend), magnitude32(divisor), &remainder);
    if ((dividend ^ divisor) < 0)
    {
        quotient = 0u - quotient;
    }

    if (dividend < 0)
    {
        remainder = 0u - remainder;
    }

    return pair(quotient, remainder);
}

// 64-bit values as two 32-bit halves.
typedef struct
{
    u32 low;
    u32 high;
} halves;

static halves split(u64 value)
{
    halves result = {(u32)value, (u32)(value >> 32)};
    return result;
}

static halves negate(halves value)
{
    halves result = {0u - value.low, 0u - value.high - (value.low != 0 ? 1u : 0u)};
    return result;
}

// Unsigned 64-bit long division over 32-bit halves: one quotient bit per step, from the dividend's highest bit.
static void udivmod64(halves dividend, halves divisor, halves *quotient, halves *remainder)
{
    if ((divisor.low | divisor.high) == 0)
    {
        chipsharp_divide_by_zero();
    }

    if (dividend.high == 0 && divisor.high == 0)
    {
        u32 rest;
        quotient->low = udivmod32(dividend.low, divisor.low, &rest);
        quotient->high = 0;
        remainder->low = rest;
        remainder->high = 0;
        return;
    }

    u32 q_low = 0;
    u32 q_high = 0;
    u32 r_low = 0;
    u32 r_high = 0;
    int bits = dividend.high != 0 ? 64 - leading_zeros(dividend.high) : 32 - leading_zeros(dividend.low);
    for (int bit = bits - 1; bit >= 0; bit--)
    {
        // remainder = remainder * 2 + the dividend's next bit; it can need 65 bits when the divisor needs 64
        u32 carry = r_high >> 31;
        u32 next = bit >= 32 ? (dividend.high >> (bit - 32)) & 1u : (dividend.low >> bit) & 1u;
        r_high = (r_high << 1) | (r_low >> 31);
        r_low = (r_low << 1) | next;

        q_high = (q_high << 1) | (q_low >> 31);
        q_low <<= 1;

        if (carry != 0 || r_high > divisor.high || (r_high == divisor.high && r_low >= divisor.low))
        {
            u32 borrow = r_low < divisor.low ? 1u : 0u;
            r_low -= divisor.low;
            r_high = r_high - divisor.high - borrow;
            q_low |= 1;
        }
    }

    quotient->low = q_low;
    quotient->high = q_high;
    remainder->low = r_low;
    remainder->high = r_high;
}

// Called by __aeabi_uldivmod (armv6m_divmod.S): returns the quotient, stores the remainder.
u64 chipsharp_udivmod64(u64 dividend, u64 divisor, u64 *remainder)
{
    halves quotient;
    halves rest;
    udivmod64(split(dividend), split(divisor), &quotient, &rest);
    *remainder = pair(rest.low, rest.high);
    return pair(quotient.low, quotient.high);
}

// Called by __aeabi_ldivmod (armv6m_divmod.S): rounds toward zero; the remainder has the dividend's sign.
u64 chipsharp_divmod64(u64 dividend, u64 divisor, u64 *remainder)
{
    halves a = split(dividend);
    halves b = split(divisor);
    u32 dividend_negative = a.high >> 31;
    u32 divisor_negative = b.high >> 31;
    if (dividend_negative)
    {
        a = negate(a);
    }

    if (divisor_negative)
    {
        b = negate(b);
    }

    halves quotient;
    halves rest;
    udivmod64(a, b, &quotient, &rest);
    if (dividend_negative != divisor_negative)
    {
        quotient = negate(quotient);
    }

    if (dividend_negative)
    {
        rest = negate(rest);
    }

    *remainder = pair(rest.low, rest.high);
    return pair(quotient.low, quotient.high);
}

// The full 64-bit product of two 32-bit values, from four 16 x 16 bit products (MULS keeps only the low 32 bits).
static halves multiply32(u32 a, u32 b)
{
    u32 a0 = a & 0xFFFFu;
    u32 a1 = a >> 16;
    u32 b0 = b & 0xFFFFu;
    u32 b1 = b >> 16;
    u32 p00 = a0 * b0;
    u32 p01 = a0 * b1;
    u32 p10 = a1 * b0;
    u32 p11 = a1 * b1;
    u32 middle = (p00 >> 16) + (p01 & 0xFFFFu) + (p10 & 0xFFFFu); // at most 3 * 0xFFFF: no overflow
    halves result = {(middle << 16) | (p00 & 0xFFFFu), p11 + (p01 >> 16) + (p10 >> 16) + (middle >> 16)};
    return result;
}

// The low 64 bits of a 64 x 64 bit product (the same for signed and unsigned values).
u64 __aeabi_lmul(u64 a, u64 b)
{
    halves x = split(a);
    halves y = split(b);
    halves product = multiply32(x.low, y.low);
    product.high += x.low * y.high + x.high * y.low;
    return pair(product.low, product.high);
}

// 64-bit shifts; the RTABI allows shift amounts from 0 to 63.
u64 __aeabi_llsl(u64 value, int shift)
{
    halves v = split(value);
    if (shift >= 32)
    {
        return pair(0, v.low << (shift - 32));
    }

    if (shift == 0)
    {
        return value;
    }

    return pair(v.low << shift, (v.high << shift) | (v.low >> (32 - shift)));
}

u64 __aeabi_llsr(u64 value, int shift)
{
    halves v = split(value);
    if (shift >= 32)
    {
        return pair(v.high >> (shift - 32), 0);
    }

    if (shift == 0)
    {
        return value;
    }

    return pair((v.low >> shift) | (v.high << (32 - shift)), v.high >> shift);
}

u64 __aeabi_lasr(u64 value, int shift)
{
    halves v = split(value);
    s32 high = (s32)v.high;
    if (shift >= 32)
    {
        return pair((u32)(high >> (shift - 32)), (u32)(high >> 31));
    }

    if (shift == 0)
    {
        return value;
    }

    return pair((v.low >> shift) | (v.high << (32 - shift)), (u32)(high >> shift));
}

// Memory: byte by byte, which is small and always correct for any alignment.
void __aeabi_memcpy(void *target, const void *source, size_t count)
{
    unsigned char *to = (unsigned char *)target;
    const unsigned char *from = (const unsigned char *)source;
    while (count-- > 0)
    {
        *to++ = *from++;
    }
}

void __aeabi_memmove(void *target, const void *source, size_t count)
{
    unsigned char *to = (unsigned char *)target;
    const unsigned char *from = (const unsigned char *)source;
    if (to < from)
    {
        while (count-- > 0)
        {
            *to++ = *from++;
        }
    }
    else
    {
        while (count-- > 0)
        {
            to[count] = from[count];
        }
    }
}

// The RTABI's memset takes the count before the value.
void __aeabi_memset(void *target, size_t count, int value)
{
    unsigned char *to = (unsigned char *)target;
    while (count-- > 0)
    {
        *to++ = (unsigned char)value;
    }
}

void __aeabi_memclr(void *target, size_t count)
{
    __aeabi_memset(target, count, 0);
}

void __aeabi_memcpy4(void *target, const void *source, size_t count) __attribute__((alias("__aeabi_memcpy")));
void __aeabi_memcpy8(void *target, const void *source, size_t count) __attribute__((alias("__aeabi_memcpy")));
void __aeabi_memmove4(void *target, const void *source, size_t count) __attribute__((alias("__aeabi_memmove")));
void __aeabi_memmove8(void *target, const void *source, size_t count) __attribute__((alias("__aeabi_memmove")));
void __aeabi_memset4(void *target, size_t count, int value) __attribute__((alias("__aeabi_memset")));
void __aeabi_memset8(void *target, size_t count, int value) __attribute__((alias("__aeabi_memset")));
void __aeabi_memclr4(void *target, size_t count) __attribute__((alias("__aeabi_memclr")));
void __aeabi_memclr8(void *target, size_t count) __attribute__((alias("__aeabi_memclr")));

void *memcpy(void *target, const void *source, size_t count)
{
    __aeabi_memcpy(target, source, count);
    return target;
}

void *memmove(void *target, const void *source, size_t count)
{
    __aeabi_memmove(target, source, count);
    return target;
}

void *memset(void *target, int value, size_t count)
{
    __aeabi_memset(target, count, value);
    return target;
}

int memcmp(const void *a, const void *b, size_t count)
{
    const unsigned char *x = (const unsigned char *)a;
    const unsigned char *y = (const unsigned char *)b;
    for (size_t i = 0; i < count; i++)
    {
        if (x[i] != y[i])
        {
            return x[i] < y[i] ? -1 : 1;
        }
    }

    return 0;
}
