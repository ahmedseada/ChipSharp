// ChipSharp runtime for the Espressif ESP32-C5: the events a program's [Interrupt] methods handle.
//
// Freestanding, like esp32c5_runtime.c. The compiler gives each handler an entry that takes the place of the trap
// dispatch's weak chipsharp_isr_irq<n>, where n comes from the target's "interrupts" table (esp32c5.json): "timer" is
// CLIC id 17 and "pin" id 18 (esp32c5_chip.h has the whole routing). The entry calls this file's clear function for the
// event first, then the handler.
//
//   ChipSharp_Runtime_Timer...         ChipSharp.Hardware.Timing.Every..., After... and Stop: the timer event, on
//                                      SYSTIMER's alarm 1 (the source SYSTIMER_TARGET1, routed to id 17); the delays use
//                                      alarm 0
//   chipsharp_clear_timer_event        clears alarm 1's interrupt and, for Every, arms the next time
//   ChipSharp_Runtime_Pin...           ChipSharp.Hardware.Pin.Watch, WatchRising, WatchFalling and Unwatch: the pin event,
//                                      on the edge interrupts of GPIO0-GPIO28 for the CPU (the GPIO source, routed to
//                                      id 18)
//   chipsharp_clear_pin_event          clears the edges that made the interrupt come
//
// Every interrupt has the same level, and a handler runs with interrupts off, so one never interrupts another: each runs
// to its end first.

#include "chipsharp_hardware.h"
#include "esp32c5_chip.h"

// The longest time of the timer event, so that two times compare by the sign of their difference: 2147483 ms, about 35
// minutes (and 2^31 - 1 microseconds), as on the other chips.
#define MAX_MILLISECONDS 2147483u

// ---- The timer event ---------------------------------------------------------------------------------------------

static unsigned long long timer_next;   // the count alarm 1 is armed for, in SYSTIMER ticks
static unsigned long long timer_period; // ticks from one time to the next; 0 for once (After)
static unsigned int timer_started;      // the alarm's interrupt is routed and enabled

// Loads alarm 1's target: it fires when the count is at or past it (one already past fires at once). Interrupts are off
// (SYSTIMER_CONF is shared with the delays' alarm), or this is the alarm's own handler.
static void arm_timer(void)
{
    SYSTIMER_TARGET1_HI = (unsigned int)(timer_next >> 32);
    SYSTIMER_TARGET1_LO = (unsigned int)timer_next;
    SYSTIMER_CONF |= SYSTIMER_CONF_TARGET1_WORK_EN_MASK; // a comparator that starts to work takes the target it has
    SYSTIMER_COMP1_LOAD = SYSTIMER_COMP1_LOAD_TIMER_COMP1_LOAD_MASK;
}

// The interrupt is off, and what it raised cleared. Interrupts are off.
static void stop_timer(void)
{
    SYSTIMER_INT_ENA &= ~SYSTIMER_INT_ENA_TARGET1_INT_ENA_MASK;
    SYSTIMER_INT_CLR = SYSTIMER_INT_CLR_TARGET1_INT_CLR_MASK;
}

// The event comes first after ticks, then every period ticks (0: only once).
static void start_timer(unsigned long long ticks, unsigned long long period)
{
    unsigned int enabled = c5_interrupts_off();
    stop_timer();
    if (!timer_started)
    {
        c5_route(ESP32C5_IRQ_SYSTIMER_TARGET1, C5_ID_TIMER_EVENT);
        c5_clic_enable(C5_ID_TIMER_EVENT);
        timer_started = 1;
    }

    timer_period = period;
    timer_next = chipsharp_c5_ticks() + ticks;
    arm_timer();
    SYSTIMER_INT_CLR = SYSTIMER_INT_CLR_TARGET1_INT_CLR_MASK;
    SYSTIMER_INT_ENA |= SYSTIMER_INT_ENA_TARGET1_INT_ENA_MASK;
    c5_interrupts_back(enabled);
}

// The interrupt entry calls this before the handler. A time missed while a handler ran too long comes at once (the target
// is already past), so that Every runs the handler as many times as the time says.
void chipsharp_clear_timer_event(void)
{
    SYSTIMER_INT_CLR = SYSTIMER_INT_CLR_TARGET1_INT_CLR_MASK;
    if (timer_period != 0)
    {
        timer_next += timer_period;
        arm_timer();
    }
    else
    {
        SYSTIMER_INT_ENA &= ~SYSTIMER_INT_ENA_TARGET1_INT_ENA_MASK;
    }
}

static unsigned long long milliseconds_as_ticks(chipsharp_i32 milliseconds)
{
    if (milliseconds <= 0)
    {
        return 0;
    }

    unsigned int capped = (unsigned int)milliseconds > MAX_MILLISECONDS ? MAX_MILLISECONDS : (unsigned int)milliseconds;
    return (unsigned long long)capped * 1000u * C5_TICKS_PER_MICROSECOND;
}

static unsigned long long microseconds_as_ticks(chipsharp_i32 microseconds)
{
    return microseconds > 0 ? (unsigned long long)(unsigned int)microseconds * C5_TICKS_PER_MICROSECOND : 0;
}

static void every(unsigned long long ticks)
{
    if (ticks == 0)
    {
        unsigned int enabled = c5_interrupts_off();
        stop_timer();
        c5_interrupts_back(enabled);
    }
    else
    {
        start_timer(ticks, ticks);
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

void ChipSharp_Runtime_TimerAfterMilliseconds(chipsharp_i32 milliseconds)
{
    start_timer(milliseconds_as_ticks(milliseconds), 0);
}

void ChipSharp_Runtime_TimerAfterMicroseconds(chipsharp_i32 microseconds)
{
    start_timer(microseconds_as_ticks(microseconds), 0);
}

void ChipSharp_Runtime_TimerStop(void)
{
    every(0);
}

// ---- The pin event -----------------------------------------------------------------------------------------------

static unsigned int pins_started; // the GPIO interrupt is routed and enabled

// From now on the changes of pin (GPIO_PINn's INT_TYPE: 1 rising edge, 2 falling edge, 3 both, 0 none) run the handler;
// changes from before are forgotten. The pin is an input first (its pad's input on), as for Pin.Read.
static void watch(chipsharp_i32 pin, unsigned int type)
{
    if (pin < 0 || pin >= C5_PIN_COUNT)
    {
        return;
    }

    unsigned int enabled = c5_interrupts_off();
    uint32_t config = C5_GPIO_PIN(pin) & ~(GPIO_PIN0_INT_TYPE_MASK | GPIO_PIN0_INT_ENA_MASK);
    if (type != 0)
    {
        C5_IO_MUX(pin) |= IO_MUX_GPIO0_FUN_IE_MASK;
        config |= GPIO_PIN0_INT_TYPE(type) | GPIO_PIN0_INT_ENA(1); // INT_ENA's bit 0 (13): the interrupt for the CPU
    }

    C5_GPIO_PIN(pin) = config;
    GPIO_STATUS_W1TC = 1u << pin;
    if (type != 0 && !pins_started)
    {
        c5_route(ESP32C5_IRQ_GPIO, C5_ID_PIN_EVENT);
        c5_clic_enable(C5_ID_PIN_EVENT);
        pins_started = 1;
    }

    c5_interrupts_back(enabled);
}

// The interrupt entry calls this before the handler: it clears the pins' status bits that made the interrupt (the edges;
// a pin's interrupt that is not on is not in GPIO_PROCPU_INT). An edge that comes after this runs the handler again.
void chipsharp_clear_pin_event(void)
{
    GPIO_STATUS_W1TC = GPIO_PROCPU_INT;
}

void ChipSharp_Runtime_PinWatch(chipsharp_i32 pin)
{
    watch(pin, 3);
}

void ChipSharp_Runtime_PinWatchRising(chipsharp_i32 pin)
{
    watch(pin, 1);
}

void ChipSharp_Runtime_PinWatchFalling(chipsharp_i32 pin)
{
    watch(pin, 2);
}

void ChipSharp_Runtime_PinUnwatch(chipsharp_i32 pin)
{
    watch(pin, 0);
}
