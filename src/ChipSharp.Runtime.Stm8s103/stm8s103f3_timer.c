// ChipSharp runtime for the STM8S103F3: the timer event, Timing.Every..., After... and Stop, which run the program's
// [Interrupt(Event.Timer)] method (its entry chipsharp_isr_irq31, stm8s103f3.json's "interrupts.timer.irq").
//
// TIM1 counts the clock's microseconds (stm8s103f3_runtime.c); its compare 1 (no pin: CC1E off) matches at the low 16
// bits of the event's time, once each round of TIM1, and the handler runs the method when the clock has come to the
// time, then sets the next one (a period later, so that the events never drift; at once when the time has passed, as
// when a method ran longer than its period). Times are the low 32 bits of the microseconds, which go round after 71
// minutes: a difference of two is the time between them while it is below 35 minutes, and the longest period, 2147483
// ms, is less. The event comes a few microseconds after its time: the handler's entry, and the comparison of the time.

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

#define LONGEST_MILLISECONDS 2147483

// Microseconds since start-up, their low 32 bits (they go round after 71 minutes): the event's time.
static u32 now(void)
{
    u32 counted;
    u16 count;
    chipsharp_clock_read(&counted, &count);
    return counted << 16 | count;
}

static volatile u32 due;
static volatile u32 period; // 0: once (After)
static volatile u8 running;

// Compare 1 at the due time, and its interrupt on. With interrupts off: a time that has come already (or comes before
// the compare could match it) is requested at once (CC1G).
static void arm(void)
{
    u32 at = due;
    TIM1_CCR1H = (u8)(at >> 8); // the high byte first: TIM1 takes both when the low one is written
    TIM1_CCR1L = (u8)at;
    TIM1_SR1 = (u8)~TIM1_SR1_CC1IF_MASK;
    TIM1_IER |= TIM1_IER_CC1IE_MASK;
    if ((i32)(now() - at) >= 0)
    {
        TIM1_EGR = TIM1_EGR_CC1G_MASK;
    }
}

// TIM1_CAP_COM's hook: the compare matched (in this round, or one before the event's).
static void matched(void)
{
    TIM1_SR1 = (u8)~TIM1_SR1_CC1IF_MASK;
    if (!running || (i32)(now() - due) < 0)
    {
        return;
    }

    if (period == 0)
    {
        running = 0;
        TIM1_IER &= (u8)~TIM1_IER_CC1IE_MASK;
    }
    else
    {
        due += period;
        arm();
    }

    TIMER_EVENT();
}

static void start(u32 microseconds, u8 repeat)
{
    u8 cc = chipsharp_interrupts_off();
    chipsharp_timer_hook = matched;
    running = 1;
    period = repeat ? microseconds : 0;
    due = now() + microseconds;
    arm();
    chipsharp_interrupts_back(cc);
}

void ChipSharp_Runtime_TimerStop(void)
{
    u8 cc = chipsharp_interrupts_off();
    running = 0;
    TIM1_IER &= (u8)~TIM1_IER_CC1IE_MASK;
    TIM1_SR1 = (u8)~TIM1_SR1_CC1IF_MASK;
    chipsharp_interrupts_back(cc);
}

static u32 milliseconds(chipsharp_i32 value)
{
    return (u32)(value > LONGEST_MILLISECONDS ? LONGEST_MILLISECONDS : value) * 1000u;
}

// Every: 0 or less stops the timer.
void ChipSharp_Runtime_TimerEveryMilliseconds(chipsharp_i32 value)
{
    if (value <= 0)
    {
        ChipSharp_Runtime_TimerStop();
        return;
    }

    start(milliseconds(value), 1);
}

void ChipSharp_Runtime_TimerEveryMicroseconds(chipsharp_i32 value)
{
    if (value <= 0)
    {
        ChipSharp_Runtime_TimerStop();
        return;
    }

    start((u32)value, 1);
}

// After: 0 or less runs the method at once (in its interrupt, as soon as interrupts are on).
void ChipSharp_Runtime_TimerAfterMilliseconds(chipsharp_i32 value)
{
    start(value > 0 ? milliseconds(value) : 0u, 0);
}

void ChipSharp_Runtime_TimerAfterMicroseconds(chipsharp_i32 value)
{
    start(value > 0 ? (u32)value : 0u, 0);
}
