// ChipSharp runtime for Padauk PDK14 chips (PFS154, PMS154C, PFS172, ...): the program's start.
//
// SDCC puts its start-up code in the file with main(), this one, which the toolchain links whole and first: at word 0
// the reset vector, which sets the stack pointer after the variables (the stack grows up from there), calls
// __sdcc_external_startup (the chip runtime's: the clock and the calibration, before anything else), zeroes the
// variables and sets their starting values (and runs what each runtime file adds to GSINIT), then jumps to main(); at
// word 0x10 the interrupt vector, a goto to the handler declared here (pdk14_interrupt.s), which SDCC writes because
// this file declares it.
//
//   main   runs chipsharp_chip_init (the chip runtime's, or the empty one of pdk14_chip_init.c), turns interrupts on
//          (none comes before the program or the runtime enables one), runs the program (chipsharp_entry), then stops
//          for good: chipsharp_halt (pdk14_stops.s)

#include "chipsharp_hardware.h"

void chipsharp_pdk14_interrupt(void) __interrupt(0);

void main(void)
{
    chipsharp_chip_init();
    // chipsharp_entry returns a C# int: four bytes, which SDCC returns through an address the caller pushes. They go on
    // the stack, not in a variable: the program has ended when it returns, and nothing keeps its result
    __asm
        engint
        push af
        push af
        mov.io a, sp
        add a, #-4
        push af
        call _chipsharp_entry
        mov.io a, sp
        add a, #-6
        mov.io sp, a
    __endasm;
    chipsharp_halt();
}
