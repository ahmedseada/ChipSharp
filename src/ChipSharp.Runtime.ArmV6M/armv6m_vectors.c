// ChipSharp runtime for ARMv6-M chips (Cortex-M0 and M0+): the vector table and default exception handlers.
//
// The table has the 16 entries every Cortex-M has, then 32 interrupts (the most ARMv6-M allows). It lives in the
// section .vectors, which the linker script places where the chip looks for it. A chip runtime handles an
// interrupt by defining its handler, e.g. chipsharp_isr_irq0; every handler nobody defines sleeps forever, so a
// debugger (or the simulator) finds the core stopped in that exception.

void chipsharp_reset(void);

// The top of the stack: the end of RAM, from the linker script.
extern unsigned int __chipsharp_stack_top[];

void chipsharp_unhandled_exception(void)
{
    for (;;)
    {
        __asm__ volatile("wfi");
    }
}

#define CHIPSHARP_HANDLER(name) void name(void) __attribute__((weak, alias("chipsharp_unhandled_exception")))

CHIPSHARP_HANDLER(chipsharp_isr_nmi);
CHIPSHARP_HANDLER(chipsharp_isr_hardfault);
CHIPSHARP_HANDLER(chipsharp_isr_svcall);
CHIPSHARP_HANDLER(chipsharp_isr_pendsv);
CHIPSHARP_HANDLER(chipsharp_isr_systick);
CHIPSHARP_HANDLER(chipsharp_isr_irq0);
CHIPSHARP_HANDLER(chipsharp_isr_irq1);
CHIPSHARP_HANDLER(chipsharp_isr_irq2);
CHIPSHARP_HANDLER(chipsharp_isr_irq3);
CHIPSHARP_HANDLER(chipsharp_isr_irq4);
CHIPSHARP_HANDLER(chipsharp_isr_irq5);
CHIPSHARP_HANDLER(chipsharp_isr_irq6);
CHIPSHARP_HANDLER(chipsharp_isr_irq7);
CHIPSHARP_HANDLER(chipsharp_isr_irq8);
CHIPSHARP_HANDLER(chipsharp_isr_irq9);
CHIPSHARP_HANDLER(chipsharp_isr_irq10);
CHIPSHARP_HANDLER(chipsharp_isr_irq11);
CHIPSHARP_HANDLER(chipsharp_isr_irq12);
CHIPSHARP_HANDLER(chipsharp_isr_irq13);
CHIPSHARP_HANDLER(chipsharp_isr_irq14);
CHIPSHARP_HANDLER(chipsharp_isr_irq15);
CHIPSHARP_HANDLER(chipsharp_isr_irq16);
CHIPSHARP_HANDLER(chipsharp_isr_irq17);
CHIPSHARP_HANDLER(chipsharp_isr_irq18);
CHIPSHARP_HANDLER(chipsharp_isr_irq19);
CHIPSHARP_HANDLER(chipsharp_isr_irq20);
CHIPSHARP_HANDLER(chipsharp_isr_irq21);
CHIPSHARP_HANDLER(chipsharp_isr_irq22);
CHIPSHARP_HANDLER(chipsharp_isr_irq23);
CHIPSHARP_HANDLER(chipsharp_isr_irq24);
CHIPSHARP_HANDLER(chipsharp_isr_irq25);
CHIPSHARP_HANDLER(chipsharp_isr_irq26);
CHIPSHARP_HANDLER(chipsharp_isr_irq27);
CHIPSHARP_HANDLER(chipsharp_isr_irq28);
CHIPSHARP_HANDLER(chipsharp_isr_irq29);
CHIPSHARP_HANDLER(chipsharp_isr_irq30);
CHIPSHARP_HANDLER(chipsharp_isr_irq31);

typedef void (*chipsharp_vector)(void);

__attribute__((section(".vectors"), used)) const chipsharp_vector chipsharp_vectors[48] = {
    (chipsharp_vector)__chipsharp_stack_top, // 0: the initial stack pointer
    chipsharp_reset,                         // 1: reset
    chipsharp_isr_nmi,                       // 2
    chipsharp_isr_hardfault,                 // 3
    0, 0, 0, 0, 0, 0, 0,                     // 4-10: reserved on ARMv6-M
    chipsharp_isr_svcall,                    // 11
    0, 0,                                    // 12-13: reserved
    chipsharp_isr_pendsv,                    // 14
    chipsharp_isr_systick,                   // 15
    chipsharp_isr_irq0, chipsharp_isr_irq1, chipsharp_isr_irq2, chipsharp_isr_irq3,
    chipsharp_isr_irq4, chipsharp_isr_irq5, chipsharp_isr_irq6, chipsharp_isr_irq7,
    chipsharp_isr_irq8, chipsharp_isr_irq9, chipsharp_isr_irq10, chipsharp_isr_irq11,
    chipsharp_isr_irq12, chipsharp_isr_irq13, chipsharp_isr_irq14, chipsharp_isr_irq15,
    chipsharp_isr_irq16, chipsharp_isr_irq17, chipsharp_isr_irq18, chipsharp_isr_irq19,
    chipsharp_isr_irq20, chipsharp_isr_irq21, chipsharp_isr_irq22, chipsharp_isr_irq23,
    chipsharp_isr_irq24, chipsharp_isr_irq25, chipsharp_isr_irq26, chipsharp_isr_irq27,
    chipsharp_isr_irq28, chipsharp_isr_irq29, chipsharp_isr_irq30, chipsharp_isr_irq31,
};
