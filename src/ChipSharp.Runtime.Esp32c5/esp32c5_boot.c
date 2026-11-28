// ChipSharp runtime for the Espressif ESP32-C5: the chip's first steps, before the variables are set up.
//
// The ROM has loaded the program into SRAM and jumped to chipsharp_reset (riscv32_startup.S), which has set up gp and
// the stack and calls chipsharp_chip_boot before it zeroes .bss: no variable may be used here, only the stack. The
// registers come from Espressif's SVD (esp32c5_registers.h, which ChipSharp.Chips writes from chips/esp32c5.json); the
// facts and their sources are in the target file, targets/chip/esp32c5.json.
//
//   1. The watchdogs a boot from flash leaves running would reset the chip: the ROM turns on the RTC watchdog's (RWDT,
//      LP_WDT) and timer group 0's (MWDT0) "flash boot protection" (ESP-IDF's bootloader_config_wdt), and the super
//      watchdog (SWD) runs from power-up. Each is stopped as ESP-IDF's bootloader and HAL do it: the WPROTECT register
//      unlocked with its key, WDT_EN and WDT_FLASHBOOT_MOD_EN cleared (a timer group's config register then copied over
//      with WDT_CONF_UPDATE_EN: it is updated asynchronously), WPROTECT locked again; the super watchdog, which has no
//      off, feeds itself (SWD_AUTO_FEED_EN, bootloader_super_wdt_auto_feed). Timer group 1's is stopped too, whatever
//      a program before this one left it as.
//   2. The trap entry goes in CLIC mode: mtvec = chipsharp_trap | 3 (ESP-IDF's rv_utils.h, MTVEC_MODE_CSR), the entry
//      64-byte aligned (link.vectorAlign). Every exception and every interrupt not vectored (SHV clear in its CLIC
//      INT_CTRL register, all of ChipSharp's) comes there, its id in mcause's bits 11-0; mtvt is not used.
//
// The CPU's clock stays as the ROM left it (the crystal's); interrupts stay off: the chip's runtime sets its clocks
// and turns interrupts on in chipsharp_chip_init. No ROM function is called, here or later.

#include "esp32c5_registers.h"

// riscv32_trap.S
extern char chipsharp_trap[];

static void stop_timer_group_watchdog(volatile uint32_t *protect, volatile uint32_t *config)
{
    *protect = TIMG_WDT_WKEY_VALUE;
    *config = (*config & ~(TIMG0_WDTCONFIG0_WDT_EN_MASK | TIMG0_WDTCONFIG0_WDT_FLASHBOOT_MOD_EN_MASK))
        | TIMG0_WDTCONFIG0_WDT_CONF_UPDATE_EN_MASK;
    *protect = 0;
}

void chipsharp_chip_boot(void)
{
    // the RTC watchdog
    LP_WDT_WPROTECT = LP_WDT_WKEY_VALUE;
    LP_WDT_CONFIG0 &= ~(LP_WDT_CONFIG0_WDT_EN_MASK | LP_WDT_CONFIG0_WDT_FLASHBOOT_MOD_EN_MASK);
    LP_WDT_WPROTECT = 0;

    // the super watchdog
    LP_WDT_SWD_WPROTECT = LP_WDT_SWD_WKEY_VALUE;
    LP_WDT_SWD_CONFIG |= LP_WDT_SWD_CONFIG_SWD_AUTO_FEED_EN_MASK;
    LP_WDT_SWD_WPROTECT = 0;

    // the timer groups' (TIMG1 is derived from TIMG0: the same fields, at its own base)
    stop_timer_group_watchdog(&TIMG0_WDTWPROTECT, &TIMG0_WDTCONFIG0);
    stop_timer_group_watchdog(&TIMG1_WDTWPROTECT, &TIMG1_WDTCONFIG0);

    // traps in CLIC mode
    __asm__ volatile("csrw mtvec, %0" : : "r"((uint32_t)(uintptr_t)chipsharp_trap | 3u) : "memory");
}
