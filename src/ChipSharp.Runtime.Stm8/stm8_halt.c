// ChipSharp runtime for STM8 chips: how a program stops.
//
// How a program stops, for a simulator to tell (as on the other chips: a breakpoint is a failure, a CPU asleep for good
// the end): break means it failed, and a debugger (SWIM) stops there; halt with nothing left to wake the chip means it
// has finished. halt stops the CPU and every clock until an external interrupt (a pin's, EXTI) or the auto wake-up
// (AWU) comes; it turns interrupts on itself (I1:I0 to 10), so the chip runtime's chipsharp_before_halt turns off every
// interrupt that could wake it. Without a debugger break does nothing (it is a NOP), and the loop around halt keeps the
// chip there.
//
//   chipsharp_halt           the program has ended: chipsharp_before_halt, interrupts off, then halt forever
//   chipsharp_halt_failure   the program failed (stm8_failures.c, chipsharp_hardware_failure): chipsharp_before_halt,
//                            interrupts off, break, then halt forever

#include "chipsharp_hardware.h"

void chipsharp_halt(void)
{
    chipsharp_before_halt();
    __asm__("sim");
    for (;;)
    {
        __asm__("halt");
    }
}

void chipsharp_halt_failure(void)
{
    chipsharp_before_halt();
    __asm__("sim");
    __asm__("break");
    for (;;)
    {
        __asm__("halt");
    }
}
