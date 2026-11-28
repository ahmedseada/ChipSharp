// ChipSharp runtime for STM8 chips: turning interrupts off and back on as they were, in assembly (SDCC's calling
// convention, __sdcccall(1): an 8-bit argument and result in A).
//
// The level the CPU runs at is CC's I1:I0: 10 (level 0) in the program's Main once start-up has run rim, 11 (level 3) in
// a handler of the chip's runtime (every interrupt has the priority 3 it has after reset). sim makes it 11, so no
// interrupt comes until CC is put back as it was: in Main they come again; in a handler, after its iret.

#include "stm8_runtime.h"

u8 chipsharp_interrupts_off(void) __naked
{
    __asm
        push cc
        pop a
        sim
        ret
    __endasm;
}

void chipsharp_interrupts_back(u8 cc) __naked
{
    (void)cc;
    __asm
        push a
        pop cc
        ret
    __endasm;
}
