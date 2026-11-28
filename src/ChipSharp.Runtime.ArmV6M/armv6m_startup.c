// ChipSharp runtime for ARMv6-M chips (Cortex-M0 and M0+): start-up.
//
// Freestanding: no C library, and no header but ChipSharp's hardware contract (chipsharp_hardware.h), whose start and
// stops this file has. Any Cortex-M0/M0+ chip can use this file; what one chip needs on top (clocks, peripherals taken
// out of reset) goes in chipsharp_chip_init, which the chip's runtime defines.
//
//   chipsharp_reset       the reset handler (vector 1): copies .data from flash to RAM, zeroes .bss, runs
//                         chipsharp_chip_init and the program (chipsharp_entry), then sleeps forever
//   chipsharp_exit_code   what chipsharp_entry returned, for a debugger to read
//   chipsharp_halt        the contract's stop of a program that has ended, for the runtime's own code: as the reset
//                         handler's end, chipsharp_before_halt (weak, empty unless the chip's runtime has one), then
//                         sleep forever (interrupts still wake the core and are handled)
//   ChipSharp_Runtime_LocalUtcOffset  the offset of local time from UTC that a date's z and K formats write: 0, a chip
//                         has no time zones (ChipSharp.Library's Runtime.LocalUtcOffset)
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

__attribute__((noreturn)) void chipsharp_reset(void)
{
    // word by word: the linker script aligns both sections to 4 bytes
    unsigned int *source = __chipsharp_data_load;
    for (unsigned int *target = __chipsharp_data_start; target < __chipsharp_data_end; target++)
    {
        *target = *source++;
    }

    for (unsigned int *target = __chipsharp_bss_start; target < __chipsharp_bss_end; target++)
    {
        *target = 0;
    }

    chipsharp_chip_init();
    chipsharp_exit_code = chipsharp_entry();

    // the program has ended: nothing is left to do but sleep (interrupts still wake the core and are handled)
    for (;;)
    {
        __asm__ volatile("wfi");
    }
}
