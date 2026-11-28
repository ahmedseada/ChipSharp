// ChipSharp runtime for AVR chips (ATmega, ATtiny, AVR Dx): the program's start, and how it stops.
//
// Freestanding: no C library, and no header but ChipSharp's hardware contract (chipsharp_hardware.h), whose start and
// stops this file has. The reset handler (avr_startup.S) has set up r1, the stack, .data and .bss and jumps to
// chipsharp_start:
//
//   chipsharp_start          runs chipsharp_chip_init (empty unless the chip's runtime defines it), turns interrupts
//                            on (no interrupt comes before the program or the chip's runtime enables one), runs the
//                            program (chipsharp_entry) and then stops for good: chipsharp_halt
//   chipsharp_exit_code      what chipsharp_entry returned, for a debugger or a simulator to read
//   chipsharp_halt           the program has ended: interrupts off, then SLEEP forever
//   chipsharp_halt_failure   the program failed (avr_failures.c, an interrupt nobody handles): interrupts off, BREAK,
//                            then SLEEP forever
//   chipsharp_before_halt    what both do first: empty unless the chip's runtime defines it, e.g. a console that sends
//                            what it still holds (polling: interrupts may be off already, in an interrupt handler)
//   ChipSharp_Runtime_LocalUtcOffset  the offset of local time from UTC that a date's z and K formats write: 0, a chip
//                            has no time zones (ChipSharp.Library's Runtime.LocalUtcOffset)
//
// How a program stops, for a simulator to tell (as the Pico's: a breakpoint is a failure, a core asleep for good the
// end): BREAK (opcode 0x9598) means it failed, and a debugger stops there; SLEEP (0x9588) with interrupts off (SREG's
// I bit clear) means it has finished, as nothing can run any more. On the chip without a debugger BREAK does nothing,
// and SLEEP does nothing either while no sleep mode is chosen (SMCR's SE bit clear, as after reset): the loop around it
// keeps the chip there. A chip's runtime that wants it to sleep for real sets the sleep mode in chipsharp_before_halt.

#include "chipsharp_hardware.h"

#if __SIZEOF_DOUBLE__ != 8
#error "Compile ChipSharp's AVR runtime with -mdouble=64: C# double is IEEE's 64-bit one, clang's AVR double 32-bit"
#endif

// Written by the code generator: runs the program's Main and returns its result (0 for a void Main). A C# int, 32 bits:
// a long in AVR's C, whose int has 16.
long chipsharp_entry(void);

volatile long chipsharp_exit_code;

// A chip keeps no time zone: its local time is UTC, an offset of 0 ticks.
__attribute__((weak)) long long ChipSharp_Runtime_LocalUtcOffset(void)
{
    return 0;
}

// The chip's own set-up, before the program runs. A chip runtime replaces this empty one.
__attribute__((weak)) void chipsharp_chip_init(void)
{
}

// What the chip's runtime does before the program stops. A chip runtime replaces this empty one.
__attribute__((weak)) void chipsharp_before_halt(void)
{
}

__attribute__((noreturn)) void chipsharp_halt(void)
{
    chipsharp_before_halt();
    __asm__ volatile("cli" ::: "memory");
    for (;;)
    {
        __asm__ volatile("sleep");
    }
}

__attribute__((noreturn)) void chipsharp_halt_failure(void)
{
    chipsharp_before_halt();
    __asm__ volatile("cli\n\tbreak" ::: "memory");
    for (;;)
    {
        __asm__ volatile("sleep");
    }
}

__attribute__((noreturn)) void chipsharp_start(void)
{
    chipsharp_chip_init();
    __asm__ volatile("sei" ::: "memory");
    chipsharp_exit_code = chipsharp_entry();
    chipsharp_halt();
}
