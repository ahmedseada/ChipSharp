// ChipSharp runtime for ARMv6-M chips (Cortex-M0 and M0+): start-up.
//
// Freestanding: no headers and no C library. Any Cortex-M0/M0+ chip can use this file; what one chip needs on top
// (clocks, peripherals taken out of reset) goes in chipsharp_chip_init, which the chip's runtime defines.
//
//   chipsharp_reset       the reset handler (vector 1): copies .data from flash to RAM, zeroes .bss, runs
//                         chipsharp_chip_init and the program (chipsharp_entry), then sleeps forever
//   chipsharp_exit_code   what chipsharp_entry returned, for a debugger to read
//
// The linker script the toolchain writes defines the __chipsharp_* symbols this file uses.

int chipsharp_entry(void);

extern unsigned int __chipsharp_data_start[];
extern unsigned int __chipsharp_data_end[];
extern unsigned int __chipsharp_data_load[];
extern unsigned int __chipsharp_bss_start[];
extern unsigned int __chipsharp_bss_end[];

volatile int chipsharp_exit_code;

// The chip's own set-up, before the program runs. A chip runtime replaces this empty one.
__attribute__((weak)) void chipsharp_chip_init(void)
{
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
