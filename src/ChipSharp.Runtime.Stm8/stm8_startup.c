// ChipSharp runtime for STM8 chips (STM8S, STM8L, STM8AF), built with SDCC: the program's start.
//
// SDCC writes the start-up in the file with main(), this one: the vector table at the start of flash (0x8000), whose
// reset entry runs its start-up code, which clears the variables (DATA), copies their starting values from flash
// (INITIALIZER to INITIALIZED) and jumps to main; the stack pointer is the chip's own after reset, the end of RAM (0x03ff
// on the STM8S103). The table names the interrupt handlers declared here, which stm8_vectors.h has: the chip runtime's
// (this one, of the architecture alone, declares none, and every entry is int 0x000000, which a simulator stops at).
//
//   main                   runs chipsharp_chip_init (the chip runtime's, interrupts still off: SDCC starts with
//                          I1:I0 at 11, as the chip does after reset), turns interrupts on (rim: level 0), runs the
//                          program (chipsharp_entry) and then stops for good: chipsharp_halt
//   chipsharp_exit_code    what chipsharp_entry returned, for a debugger or a simulator to read

#include "chipsharp_hardware.h"
#include "stm8_vectors.h"

volatile chipsharp_i32 chipsharp_exit_code;

void main(void)
{
    chipsharp_chip_init();
    __asm__("rim");
    chipsharp_exit_code = chipsharp_entry();
    chipsharp_halt();
}
