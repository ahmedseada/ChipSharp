// ChipSharp runtime for ARMv6-M chips (Cortex-M0 and M0+): what happens when a program does something .NET throws an
// exception for, on a chip whose runtime does not say it another way.
//
// ChipSharp has no exceptions yet, so the program stops the way a division by zero does (chipsharp_divide_by_zero):
// at a breakpoint, so that a debugger stops there; without one the breakpoint becomes a HardFault, whose handler sleeps
// forever. A chip with a console says what happened first: its runtime defines these functions again (the RP2040's in
// rp2040_failures.c, over UART0), and since these are weak, the linker takes the chip's.
//
//   ChipSharp_Runtime_IndexOutOfRange    an array index outside the array (IndexOutOfRangeException)
//   ChipSharp_Runtime_TimeSpan...        a TimeSpan too long, or NaN given to one (ChipSharp.Library's System.TimeSpan)
//   ChipSharp_Runtime_NegateOverflow     the negative of the smallest value, e.g. -TimeSpan.MinValue
//
// The ones besides IndexOutOfRange are the methods of ChipSharp.Library.Runtime, which says when each happens.

__attribute__((noreturn)) static void fail(void)
{
    for (;;)
    {
        __asm__ volatile("bkpt #0\n\twfi");
    }
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_IndexOutOfRange(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanOverflow(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanOutOfRange(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanNaN(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanNaNFactor(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanNaNDivisor(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanDurationOverflow(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_NegateOverflow(void) { fail(); }
