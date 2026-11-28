// ChipSharp runtime for 32-bit RISC-V chips (RV32, machine mode): the program's start, and how it stops.
//
// Freestanding: no C library, and no header but ChipSharp's hardware contract (chipsharp_hardware.h), whose start and
// stops this file has. The reset handler (riscv32_startup.S) has set up gp, the stack and the trap entry, and run the
// chip's first steps (chipsharp_chip_boot); then:
//
//   chipsharp_start          copies .data's starting values to it (unless they are where .data is: a chip whose program
//                            runs from the RAM it was loaded into, as the ESP32-C5's does), zeroes .bss, runs
//                            chipsharp_chip_init (empty unless the chip's runtime defines it; it turns interrupts on
//                            when it has set up what may interrupt), runs the program (chipsharp_entry) and then stops
//                            for good: chipsharp_halt
//   chipsharp_exit_code      what chipsharp_entry returned, for a debugger or a simulator to read
//   chipsharp_halt           the program has ended: chipsharp_before_halt, then WFI forever (interrupts still wake the
//                            core and are handled, as on a Cortex-M: a program's events may run on)
//   chipsharp_before_halt    what chipsharp_halt and chipsharp_halt_failure (riscv32_failures.c) do first: empty unless
//                            the chip's runtime defines it, e.g. a console that sends what it still holds
//   ChipSharp_Runtime_LocalUtcOffset  the offset of local time from UTC that a date's z and K formats write: 0, a chip
//                            has no time zones (ChipSharp.Library's Runtime.LocalUtcOffset)
//
// How a program stops, for a simulator to tell (as the Pico's): EBREAK means it failed (riscv32_failures.c), and a
// debugger stops there; WFI with no interrupt enabled that could wake it (mstatus.MIE clear, or nothing enabled in the
// interrupt controller) means it has finished.
//
// The linker script the toolchain writes defines the __chipsharp_* symbols this file uses.

#include "chipsharp_hardware.h"

int chipsharp_entry(void);

extern unsigned int __chipsharp_data_start[];
extern unsigned int __chipsharp_data_end[];
extern unsigned int __chipsharp_data_load[];
extern unsigned int __chipsharp_bss_start[];
extern unsigned int __chipsharp_bss_end[];

volatile int chipsharp_exit_code;

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
    for (;;)
    {
        __asm__ volatile("wfi");
    }
}

__attribute__((noreturn)) void chipsharp_start(void)
{
    // word by word: the linker script aligns both sections to 4 bytes
    unsigned int *source = __chipsharp_data_load;
    if (source != __chipsharp_data_start)
    {
        for (unsigned int *target = __chipsharp_data_start; target < __chipsharp_data_end; target++)
        {
            *target = *source++;
        }
    }

    for (unsigned int *target = __chipsharp_bss_start; target < __chipsharp_bss_end; target++)
    {
        *target = 0;
    }

    chipsharp_chip_init();
    chipsharp_exit_code = chipsharp_entry();
    chipsharp_halt();
}
