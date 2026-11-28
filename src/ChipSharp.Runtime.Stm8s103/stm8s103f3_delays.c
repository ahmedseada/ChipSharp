// ChipSharp runtime for the STM8S103F3: ChipSharp.Hardware.Timing's delays, which wait watching TIM1's count of
// microseconds (stm8s103f3_runtime.c), a microsecond more than they are given: TIM1 may be about to count on when it is
// read. That, and the call, is all they take more.
//
// TIM1's count alone, read in a tight loop, a millisecond (or at most 16 ms) at a time: far less than a round of TIM1
// (65.536 ms), so that the difference of two readings is the time between them even with interrupt handlers running in
// between; each step starts where the one before ended, so the steps add up exactly, with no multiplication. Each delay
// is a module of its own (the STM8 runtime cuts the file at its "//@module" lines).

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

// In the module "wait": waits until count microseconds have passed since TIM1's count was from; returns from + count
// (as TIM1 goes round).
u16 chipsharp_delay_wait(u16 from, u16 count) CHIPSHARP_REENTRANT;

//@module wait
u16 chipsharp_delay_wait(u16 from, u16 count)
{
    while ((u16)(chipsharp_clock_count() - from) < count)
    {
    }

    return from + count;
}

//@module microseconds
void ChipSharp_Runtime_DelayMicroseconds(chipsharp_i32 microseconds)
{
    u16 from = chipsharp_clock_count();
    if (microseconds <= 0)
    {
        return;
    }

    u32 left = (u32)microseconds + 1u;
    for (; left > 0x4000u; left -= 0x4000u)
    {
        from = chipsharp_delay_wait(from, 0x4000u);
    }

    chipsharp_delay_wait(from, (u16)left);
}

//@module milliseconds
void ChipSharp_Runtime_DelayMilliseconds(chipsharp_i32 milliseconds)
{
    u16 from = chipsharp_clock_count();
    if (milliseconds <= 0)
    {
        return;
    }

    from = chipsharp_delay_wait(from, 1);
    for (u32 left = (u32)milliseconds; left > 0; left--)
    {
        from = chipsharp_delay_wait(from, 1000);
    }
}
