// ChipSharp runtime for the Raspberry Pi RP2040: the events a program's [Interrupt] methods handle.
//
// Freestanding, like rp2040_runtime.c. The compiler gives each handler an entry that takes the place of the vector
// table's weak chipsharp_isr_irq<n>, where n comes from the target's "interrupts" table (rp2040.json): "timer" is
// IRQ 1 and "pin" IRQ 13. The entry calls this file's clear function for the event first, then the handler.
//
//   ChipSharp_Runtime_Timer...         ChipSharp.Hardware.Timing.Every..., After... and Stop: the timer event, on
//                                      timer alarm 1 (TIMER_IRQ_1); the delays use alarm 0
//   chipsharp_clear_timer_event        clears alarm 1's interrupt and, for Every, arms the next time
//   ChipSharp_Runtime_Pin...           ChipSharp.Hardware.Pin.Watch, WatchRising, WatchFalling and Unwatch: the pin
//                                      event, on the edge interrupts of GPIO 0-29 for core 0 (IO_IRQ_BANK0)
//   chipsharp_clear_pin_event          clears the edges that made the interrupt come
//
// Every interrupt has the same priority, so one handler never interrupts another: each runs to its end first.

#define REG(address) (*(volatile unsigned int *)(address))

// Every peripheral register has aliases that change only some bits in one write (datasheet 2.1.2).
#define ALIAS_SET 0x2000u
#define ALIAS_CLEAR 0x3000u

#define NVIC_ISER 0xe000e100u
#define NVIC_ICPR 0xe000e280u

#define TIMER_ALARM1 0x40054014u
#define TIMER_ARMED 0x40054020u
#define TIMER_TIMERAWL 0x40054028u
#define TIMER_INTR 0x40054034u
#define TIMER_INTE 0x40054038u
#define TIMER_INTF 0x4005403cu
#define TIMER_ALARM1_BIT (1u << 1)
#define TIMER_IRQ_1 1

// The longest time of the timer event, so that two times compare by the sign of their difference: 2147483 ms,
// about 35 minutes (and 2^31 - 1 microseconds).
#define MAX_MILLISECONDS 2147483u

// Four registers of 8 pins each, 4 bits per pin: level low, level high, edge low (bit 2), edge high (bit 3).
#define IO_BANK0_INTR(bank) (0x400140f0u + 4u * (bank))
#define IO_BANK0_PROC0_INTE(bank) (0x40014100u + 4u * (bank))
#define IO_BANK0_PROC0_INTS(bank) (0x40014120u + 4u * (bank))
#define EDGE_LOW 0x4u
#define EDGE_HIGH 0x8u
#define EVERY_EDGE 0xccccccccu
#define IO_IRQ_BANK0 13
#define PIN_COUNT 30
#define PIN_REGISTERS 4

// Turns interrupts off and returns how they were, for interrupts_back: around changes no handler may see half done.
static unsigned int interrupts_off(void)
{
    unsigned int primask;
    __asm__ volatile("mrs %0, primask\n\tcpsid i" : "=r"(primask)::"memory");
    return primask;
}

static void interrupts_back(unsigned int primask)
{
    __asm__ volatile("msr primask, %0" ::"r"(primask) : "memory");
}

// ---- The timer event ---------------------------------------------------------------------------------------------

static unsigned int timer_next;   // the time alarm 1 is armed for: the timer's low 32 bits, in microseconds
static unsigned int timer_period; // microseconds from one time to the next; 0 for once (After)

// Arms alarm 1 for timer_next. The alarm fires when the timer's low 32 bits equal it, so one armed for a time that
// has already passed would fire only when the timer wraps, 71 minutes later: that one fires at once instead.
static void arm_timer(void)
{
    REG(TIMER_ALARM1) = timer_next;
    if ((int)(REG(TIMER_TIMERAWL) - timer_next) >= 0 && (REG(TIMER_ARMED) & TIMER_ALARM1_BIT) != 0)
    {
        REG(TIMER_ARMED) = TIMER_ALARM1_BIT;            // write 1 to disarm
        REG(TIMER_INTF + ALIAS_SET) = TIMER_ALARM1_BIT; // forces the interrupt
    }
}

static void stop_timer(void)
{
    REG(TIMER_INTE + ALIAS_CLEAR) = TIMER_ALARM1_BIT;
    REG(TIMER_ARMED) = TIMER_ALARM1_BIT;
    REG(TIMER_INTF + ALIAS_CLEAR) = TIMER_ALARM1_BIT;
    REG(TIMER_INTR) = TIMER_ALARM1_BIT; // write 1 to clear
    REG(NVIC_ICPR) = 1u << TIMER_IRQ_1;
}

// The event comes first after microseconds, then every period microseconds (0: only once).
static void start_timer(unsigned int microseconds, unsigned int period)
{
    unsigned int primask = interrupts_off();
    stop_timer();
    timer_period = period;
    timer_next = REG(TIMER_TIMERAWL) + microseconds;
    REG(TIMER_INTE + ALIAS_SET) = TIMER_ALARM1_BIT;
    REG(NVIC_ISER) = 1u << TIMER_IRQ_1;
    arm_timer();
    interrupts_back(primask);
}

// The interrupt entry calls this before the handler. A time missed while a handler ran too long comes at once, so
// that Every runs the handler as many times as the time says.
void chipsharp_clear_timer_event(void)
{
    REG(TIMER_INTF + ALIAS_CLEAR) = TIMER_ALARM1_BIT;
    REG(TIMER_INTR) = TIMER_ALARM1_BIT; // write 1 to clear
    if (timer_period != 0)
    {
        timer_next += timer_period;
        arm_timer();
    }
}

static unsigned int milliseconds_as_microseconds(int milliseconds)
{
    if (milliseconds <= 0)
    {
        return 0;
    }

    unsigned int capped = (unsigned int)milliseconds > MAX_MILLISECONDS ? MAX_MILLISECONDS : (unsigned int)milliseconds;
    return capped * 1000u;
}

static void every(unsigned int microseconds)
{
    if (microseconds == 0)
    {
        unsigned int primask = interrupts_off();
        stop_timer();
        interrupts_back(primask);
    }
    else
    {
        start_timer(microseconds, microseconds);
    }
}

void ChipSharp_Runtime_TimerEveryMilliseconds(int milliseconds)
{
    every(milliseconds_as_microseconds(milliseconds));
}

void ChipSharp_Runtime_TimerEveryMicroseconds(int microseconds)
{
    every(microseconds > 0 ? (unsigned int)microseconds : 0u);
}

void ChipSharp_Runtime_TimerAfterMilliseconds(int milliseconds)
{
    start_timer(milliseconds_as_microseconds(milliseconds), 0);
}

void ChipSharp_Runtime_TimerAfterMicroseconds(int microseconds)
{
    start_timer(microseconds > 0 ? (unsigned int)microseconds : 0u, 0);
}

void ChipSharp_Runtime_TimerStop(void)
{
    every(0);
}

// ---- The pin event -----------------------------------------------------------------------------------------------

// From now on the changes in edges (EDGE_HIGH: low to high, EDGE_LOW: high to low, both or none) of pin run the
// handler; changes from before are forgotten.
static void watch(int pin, unsigned int edges)
{
    if (pin < 0 || pin >= PIN_COUNT)
    {
        return;
    }

    unsigned int bank = (unsigned int)pin / 8u;
    unsigned int shift = 4u * ((unsigned int)pin % 8u);
    unsigned int primask = interrupts_off();
    REG(IO_BANK0_PROC0_INTE(bank) + ALIAS_CLEAR) = 0xfu << shift;
    REG(IO_BANK0_INTR(bank)) = (EDGE_LOW | EDGE_HIGH) << shift; // write 1 to clear
    REG(NVIC_ICPR) = 1u << IO_IRQ_BANK0; // comes again at once if another pin's change is still waiting
    if (edges != 0)
    {
        REG(IO_BANK0_PROC0_INTE(bank) + ALIAS_SET) = edges << shift;
        REG(NVIC_ISER) = 1u << IO_IRQ_BANK0;
    }

    interrupts_back(primask);
}

// The interrupt entry calls this before the handler: it clears the edges the interrupt came for (the levels clear
// themselves). An edge that comes after this runs the handler again.
void chipsharp_clear_pin_event(void)
{
    for (unsigned int bank = 0; bank < PIN_REGISTERS; bank++)
    {
        REG(IO_BANK0_INTR(bank)) = REG(IO_BANK0_PROC0_INTS(bank)) & EVERY_EDGE; // write 1 to clear
    }
}

void ChipSharp_Runtime_PinWatch(int pin)
{
    watch(pin, EDGE_LOW | EDGE_HIGH);
}

void ChipSharp_Runtime_PinWatchRising(int pin)
{
    watch(pin, EDGE_HIGH);
}

void ChipSharp_Runtime_PinWatchFalling(int pin)
{
    watch(pin, EDGE_LOW);
}

void ChipSharp_Runtime_PinUnwatch(int pin)
{
    watch(pin, 0);
}
