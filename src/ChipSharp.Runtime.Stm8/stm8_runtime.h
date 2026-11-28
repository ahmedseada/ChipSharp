// ChipSharp runtime for STM8 chips (STM8S, STM8L, STM8AF), built with SDCC: what the architecture's C files and a chip
// runtime's share.
//
// Fixed-width types: SDCC's int has 16 bits, its long 32, its long long 64. The STM8 keeps a value's high byte first (big
// endian), as SDCC does: the runtime never reads a value of several bytes a byte at a time through memory, nor a byte of
// it through a pointer to the whole; it takes its bytes with shifts, which mean the same on every chip.
//
//   chipsharp_interrupts_off    interrupts off (sim: level 3, as in a handler); returns CC as it was
//   chipsharp_interrupts_back   CC as it was (its I1:I0, the level), for what no handler may see half done

#ifndef STM8_RUNTIME_H
#define STM8_RUNTIME_H

#include "chipsharp_hardware.h"

typedef unsigned char u8;
typedef signed char i8;
typedef unsigned int u16;
typedef int i16;
typedef unsigned long u32;
typedef long i32;
typedef unsigned long long u64;
typedef long long i64;

u8 chipsharp_interrupts_off(void) CHIPSHARP_REENTRANT;
void chipsharp_interrupts_back(u8 cc) CHIPSHARP_REENTRANT;

#endif
