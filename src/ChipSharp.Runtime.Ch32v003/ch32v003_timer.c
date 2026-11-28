// ChipSharp runtime for the WCH CH32V003: the timer event, Timing.Every..., After... and Stop, which run the program's
// [Interrupt(Event.Timer)] method (its entry chipsharp_isr_irq39, ch32v003.json's "interrupts.timer.irq").
//
// On SysTick's compare, which the delays and the count's guard share (ch32v003_runtime.c): the event's time is the
// deadline CH32_TIMER, in SysTick's 64-bit counts (6 a microsecond), and the compare is armed for the nearest deadline.
// SysTick's handler calls this file's hook once the count has come to the event's time; the hook sets the next time (a
// period later, so that the events never drift; one that has passed already, as when a method ran longer than its
// period, comes at once, so Every runs the method as many times as the time says) and runs the method. The event comes
// a few microseconds after its time: the trap's entry, the handler's look at the count.

#include "chipsharp_hardware.h"
#include "ch32v003_chip.h"

// The longest time of the timer event: 2147483 ms, about 35 minutes (2^31 - 1 microseconds), as on the other chips.
#define MAX_MILLISECONDS 2147483u

// Counts from one time to the next; 0 for once (After)
static u64 period;

// The entry of the program's [Interrupt(Event.Timer)] method; this empty one when it has none (ChipSharp does not let a
// program start an event it has no method for).
__attribute__((weak)) void TIMER_EVENT(void)
{
}

// SysTick's hook: the event's time has come.
static void due(void)
{
    if (period == 0)
    {
        ch32_deadlines[CH32_TIMER] = CH32_NEVER;
    }
    else
    {
        ch32_deadlines[CH32_TIMER] += period;
    }

    TIMER_EVENT();
}

// The event comes first after ticks, then every period ticks (0: only once).
static void start(u64 ticks, u64 every)
{
    u32 enabled = ch32_interrupts_off();
    ch32_timer_hook = due;
    period = every;
    ch32_deadlines[CH32_TIMER] = ch32_ticks() + ticks;
    ch32_arm();
    ch32_interrupts_back(enabled);
}

void ChipSharp_Runtime_TimerStop(void)
{
    u32 enabled = ch32_interrupts_off();
    ch32_deadlines[CH32_TIMER] = CH32_NEVER;
    ch32_arm();
    ch32_interrupts_back(enabled);
}

static u64 milliseconds_as_ticks(chipsharp_i32 milliseconds)
{
    if (milliseconds <= 0)
    {
        return 0;
    }

    u32 capped = (u32)milliseconds > MAX_MILLISECONDS ? MAX_MILLISECONDS : (u32)milliseconds;
    return (u64)capped * (1000u * CH32_TICKS_PER_MICROSECOND);
}

static u64 microseconds_as_ticks(chipsharp_i32 microseconds)
{
    return microseconds > 0 ? CH32_TIMES_6((u64)(u32)microseconds) : 0;
}

// Every: 0 or less stops the timer.
static void every(u64 ticks)
{
    if (ticks == 0)
    {
        ChipSharp_Runtime_TimerStop();
    }
    else
    {
        start(ticks, ticks);
    }
}

void ChipSharp_Runtime_TimerEveryMilliseconds(chipsharp_i32 milliseconds)
{
    every(milliseconds_as_ticks(milliseconds));
}

void ChipSharp_Runtime_TimerEveryMicroseconds(chipsharp_i32 microseconds)
{
    every(microseconds_as_ticks(microseconds));
}

// After: 0 or less runs the method at once (in SysTick's interrupt, as soon as interrupts are on).
void ChipSharp_Runtime_TimerAfterMilliseconds(chipsharp_i32 milliseconds)
{
    start(milliseconds_as_ticks(milliseconds), 0);
}

void ChipSharp_Runtime_TimerAfterMicroseconds(chipsharp_i32 microseconds)
{
    start(microseconds_as_ticks(microseconds), 0);
}
