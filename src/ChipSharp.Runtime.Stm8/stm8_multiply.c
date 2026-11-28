// ChipSharp runtime for STM8 chips: SDCC's helper for a product of 64 bits, in place of SDCC's own.
//
// SDCC calls _mullonglong for every long long a * b (signed or not: the low 64 bits of the product are the same), an
// ordinary C function of (long long, long long). Its own, in its library, makes each of the 36 products of two bytes it
// adds up into a long long shifted by a variable count: some 28000 cycles, 1.75 ms at 16 MHz, for C#'s long and
// DateTime's ticks. The program's library is searched before SDCC's, so this one, of the same name, takes its place:
// the same product, byte by byte, each 8 x 8-bit product (the STM8's mul) added at its place with its carry, nine times
// faster (0.19 against 1.75 ms in chipsim, with the loop around it) and smaller.
//
// The STM8 keeps a value's high byte first (SDCC's long long too): byte n of a value, counted from the lowest, is at
// [7 - n] of its bytes. This file is the STM8's alone.

#include "stm8_runtime.h"

#define BYTE(n) (7u - (n))

long long _mullonglong(long long ll, long long lr)
{
    const u8 *a = (const u8 *)&ll;
    const u8 *b = (const u8 *)&lr;
    union
    {
        long long value;
        u8 bytes[8];
    } product;
    for (u8 i = 0; i < 8u; i++)
    {
        product.bytes[i] = 0;
    }

    for (u8 i = 0; i < 8u; i++)
    {
        u8 x = a[BYTE(i)];
        if (x == 0)
        {
            continue;
        }

        // at most 255 * 255 + 255 + 255: 16 bits hold the byte's sum and its carry
        u16 carry = 0;
        for (u8 j = 0; (u8)(i + j) < 8u; j++)
        {
            u16 sum = (u16)((u16)x * b[BYTE(j)]) + product.bytes[BYTE(i + j)] + carry;
            product.bytes[BYTE(i + j)] = (u8)sum;
            carry = sum >> 8;
        }
    }

    return product.value;
}
