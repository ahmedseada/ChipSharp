// ChipSharp runtime for the WCH CH32V003: the chip's first steps, before the variables are set up.
//
// The core has started at 0x00000000, the vector table's first entry (ch32v003_vector_table.S), which jumps to
// chipsharp_reset (riscv32_startup.S): gp, the stack, interrupts off, mtvec at chipsharp_trap in direct mode, then this,
// before .data is copied and .bss zeroed: no variable may be used here, only the stack. The registers come from WCH's
// SVD (ch32v003_registers.h, which ChipSharp.Chips writes from chips/ch32v003.json); the facts and their sources are in
// the target file, targets/chip/ch32v003.json.
//
//   1. INTSYSCR (CSR 0x804) to 0: no hardware prologue/epilogue (HPE), no nesting. The trap entry saves the registers
//      itself, and handlers do not nest (QKV2 3.2-3.4; both are off after reset, RM 6.5.3.1, but WCH's own start-up
//      turns them on, and a bootloader may have run before).
//   2. mtvec at the vector table (0x00000000, 1 KB aligned) | 3: MODE0, an interrupt's entry is the base + 4 * its
//      number; MODE1, the table holds the handlers' addresses (WCH's start-up does the same). Each entry but the first
//      holds chipsharp_trap's address, so every interrupt and exception comes to the trap entry, its number in mcause.
//   3. The system clock from 8 MHz (the HSI's 24 MHz, divided by 3 after reset) to 48 MHz, as WCH's
//      SetSysClockTo_48MHZ_HSI does it (system_ch32v00x.c): the flash 1 wait state first (FLASH_ACTLR.LATENCY 01, for
//      24 to 48 MHz: RM 16.3.1), then HCLK = SYSCLK (HPRE 0000), the PLL from the HSI (PLLSRC 0: HSI * 2), the PLL on
//      and locked (PLLON, PLLRDY), and the PLL as the system clock (SW 10, until SWS says 10). RM 3.3, 3.4.1-3.4.2.
//
// Interrupts stay off: the chip's runtime turns them on in chipsharp_chip_init once its handlers are in place.

#include "ch32v003_registers.h"

// ch32v003_vector_table.S: the table at the start of flash
extern char ch32v003_vector_table[];

void chipsharp_chip_boot(void)
{
    __asm__ volatile("csrw %0, zero" : : "i"(CSR_INTSYSCR) : "memory");
    __asm__ volatile("csrw mtvec, %0"
        : : "r"((uint32_t)(uintptr_t)ch32v003_vector_table | MTVEC_VECTORED | MTVEC_ADDRESSES) : "memory");

    FLASH_ACTLR = (FLASH_ACTLR & ~FLASH_ACTLR_LATENCY_MASK) | FLASH_ACTLR_LATENCY(FLASH_LATENCY_48MHZ);
    RCC_CFGR0 &= ~(RCC_CFGR0_HPRE_MASK | RCC_CFGR0_PLLSRC_MASK);
    RCC_CTLR |= RCC_CTLR_PLLON_MASK;
    while ((RCC_CTLR & RCC_CTLR_PLLRDY_MASK) == 0)
    {
    }

    RCC_CFGR0 = (RCC_CFGR0 & ~RCC_CFGR0_SW_MASK) | RCC_CFGR0_SW(RCC_SW_PLL);
    while ((RCC_CFGR0 & RCC_CFGR0_SWS_MASK) != RCC_CFGR0_SWS(RCC_SW_PLL))
    {
    }
}
