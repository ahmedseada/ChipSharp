// ChipSharp runtime for the ATmega328P: the events a program's [Interrupt] methods handle, but the uart's
// (atmega328p_uart.c).
//
// Freestanding, like atmega328p_runtime.c. The chip's interrupts come through atmega328p_interrupts.S to the handlers
// here, which this file puts in chipsharp_interrupt_hooks when the program starts the event; each finds out whether the
// event happened and then calls the program's method: its entry, chipsharp_isr_irq<n>, n from the target's "interrupts"
// table (atmega328p.json), an ordinary function. No "clear" function: the handlers here clear what needs it first.
//
//   ChipSharp_Runtime_Pin...           Pin.Watch, WatchRising, WatchFalling and Unwatch: the pin event, chipsharp_isr_irq26.
//                                      D2 and D3 on INT0 and INT1, whose edges the chip itself picks and keeps until the
//                                      handler runs; every other pin (D0, D1, D4-D13, A0-A5) on the pin change interrupt of
//                                      its port (PCINT0: port B, PCINT1: port C, PCINT2: port D), which comes for any change
//                                      of the port's watched pins: the handler compares the levels with the ones it saw
//                                      last and runs the method for the edges watched. A pulse shorter than the time the
//                                      interrupt takes to come (a few microseconds) can pass unseen on these
//   ChipSharp_Runtime_Timer...         Timing.Every..., After... and Stop: the timer event, chipsharp_isr_irq27, on Timer1's
//                                      compare B: the clock's Timer1 (atmega328p_runtime.c), so that the event keeps time
//                                      with the clock and the delays. The compare matches once a round of Timer1 (32.768
//                                      ms); the handler runs the method when the time has come, and sets the next match
//
// Times are microseconds, the clock's low 32 bits (chipsharp_clock_microseconds), compared by the sign of their
// difference: the longest time of the timer event is 2147483 ms, about 35 minutes, as on the Pico.

typedef unsigned char u8;
typedef unsigned int u16;
typedef long i32;
typedef unsigned long u32;

#define REG(address) (*(volatile u8 *)(u16)(address))

#define SREG 0x5f

#define EIFR 0x3c   // INTF0, INTF1: an edge came (a 1 written clears it)
#define EIMSK 0x3d  // INT0, INT1: on
#define EICRA 0x69  // ISC01:ISC00 for INT0, ISC11:ISC10 for INT1: 1 any change, 2 falling, 3 rising
#define PCICR 0x68  // PCIE0-2: the pin change interrupts of ports B, C, D on
#define PCMSK0 0x6b // the pins of port B whose changes make PCINT0 come; PCMSK1 (port C) and PCMSK2 (port D) follow

#define TIFR1 0x36
#define TIFR1_OCF1B 0x04u
#define TIMSK1 0x6f
#define TIMSK1_OCIE1B 0x04u
#define OCR1BL 0x8a
#define OCR1BH 0x8b

#define PORT_B 0x23u
#define PORT_C 0x26u

#define EDGE_RISING 1u
#define EDGE_FALLING 2u

#define HOOK_PINS 0
#define HOOK_TIMER 1

#define MAX_MILLISECONDS 2147483u

// Sooner than this, in microseconds, the timer event's time is now: compare B is set to match a little later, in
// SOON_COUNTS of Timer1, as a match it is set to after Timer1 has passed it would come only a round later.
#define SOON 4
#define SOON_COUNTS 8u

// In atmega328p_runtime.c.
extern void (*volatile chipsharp_interrupt_hooks[4])(u8 source);
u8 chipsharp_pin_port(i32 pin, u8 *mask);
void chipsharp_clock_read(u32 *rounds, u16 *count);
u32 chipsharp_clock_microseconds(void);

// The entries of the program's [Interrupt(Event.Pin)] and [Interrupt(Event.Timer)] methods; these empty ones when it
// has none (ChipSharp does not let a program start an event it has no method for).
__attribute__((weak)) void chipsharp_isr_irq26(void)
{
}

__attribute__((weak)) void chipsharp_isr_irq27(void)
{
}

static u8 interrupts_off(void)
{
    u8 sreg = REG(SREG);
    __asm__ volatile("cli" ::: "memory");
    return sreg;
}

static void interrupts_back(u8 sreg)
{
    __asm__ volatile("" ::: "memory");
    REG(SREG) = sreg;
}

// ---- The pin event -----------------------------------------------------------------------------------------------

// For each port (B, C, D, in the order of PCINT0-2): its pins watched for rising and for falling edges (not D2 and D3:
// those are INT0's and INT1's), and its levels when its interrupt last came.
static u8 rising[3];
static u8 falling[3];
static u8 levels[3];

static u8 group_of(u8 port)
{
    return port == PORT_B ? 0 : port == PORT_C ? 1 : 2;
}

// INT0 and INT1 come for the edges they were set to; a pin change interrupt for any change of its port's watched pins.
static void pin_event(u8 source)
{
    if (source >= 2)
    {
        u8 group = (u8)(source - 2);
        u8 now = REG(PORT_B + 3u * group);
        u8 changed = now ^ levels[group];
        levels[group] = now;
        if ((changed & ((now & rising[group]) | ((u8)~now & falling[group]))) == 0)
        {
            return;
        }
    }

    chipsharp_isr_irq26();
}

// From now on the edges (EDGE_RISING, EDGE_FALLING, both or none) of pin run the method; changes from before are
// forgotten.
static void watch(i32 pin, u8 edges)
{
    u8 mask;
    u8 port = chipsharp_pin_port(pin, &mask);
    if (port == 0)
    {
        return;
    }

    u8 sreg = interrupts_off();
    chipsharp_interrupt_hooks[HOOK_PINS] = pin_event;
    if (pin == 2 || pin == 3)
    {
        u8 bit = pin == 2 ? 1u : 2u;   // in EIMSK and EIFR
        u8 shift = pin == 2 ? 0u : 2u; // in EICRA
        REG(EIMSK) &= (u8)~bit;
        if (edges != 0)
        {
            u8 sense = edges == EDGE_RISING ? 3u : edges == EDGE_FALLING ? 2u : 1u;
            REG(EICRA) = (REG(EICRA) & (u8)~(3u << shift)) | (u8)(sense << shift);
            REG(EIFR) = bit; // an edge from before, or one the change of sense made
            REG(EIMSK) |= bit;
        }
    }
    else
    {
        u8 group = group_of(port);
        rising[group] = edges & EDGE_RISING ? rising[group] | mask : rising[group] & (u8)~mask;
        falling[group] = edges & EDGE_FALLING ? falling[group] | mask : falling[group] & (u8)~mask;
        levels[group] = (levels[group] & (u8)~mask) | (REG(port) & mask);
        u8 watched = rising[group] | falling[group];
        REG(PCMSK0 + group) = watched;
        u8 enable = (u8)(1u << group);
        REG(PCICR) = watched != 0 ? REG(PCICR) | enable : REG(PCICR) & (u8)~enable;
    }

    interrupts_back(sreg);
}

void ChipSharp_Runtime_PinWatch(i32 pin)
{
    watch(pin, EDGE_RISING | EDGE_FALLING);
}

void ChipSharp_Runtime_PinWatchRising(i32 pin)
{
    watch(pin, EDGE_RISING);
}

void ChipSharp_Runtime_PinWatchFalling(i32 pin)
{
    watch(pin, EDGE_FALLING);
}

void ChipSharp_Runtime_PinUnwatch(i32 pin)
{
    watch(pin, 0);
}

// ---- The timer event ---------------------------------------------------------------------------------------------

static u8 timer_on;
static u32 timer_next;   // the time the event comes next, in microseconds
static u32 timer_period; // microseconds from one time to the next; 0 for once (After)

// Sets compare B to match at timer_next: Timer1's count then is the low 16 bits of twice it. A time sooner than SOON
// microseconds (or past) is set a little after now instead, so that the match is not one Timer1 has passed already.
static void arm(void)
{
    u32 rounds;
    u16 count;
    chipsharp_clock_read(&rounds, &count);
    u32 now = rounds << 15 | count >> 1;
    u16 match = (i32)(timer_next - now) < SOON ? count + SOON_COUNTS : (u16)(timer_next << 1);
    REG(OCR1BH) = (u8)(match >> 8); // the high byte first: it goes through Timer1's temporary register
    REG(OCR1BL) = (u8)match;
    REG(TIFR1) = TIFR1_OCF1B; // a match from before (write 1 to clear)
}

static void stop_timer(void)
{
    timer_on = 0;
    REG(TIMSK1) &= (u8)~TIMSK1_OCIE1B;
    REG(TIFR1) = TIFR1_OCF1B;
}

// Compare B matched: once a round of Timer1, and soon after a time set as SOON. When the time has come, the next one is
// set (Every: a time missed while a handler ran too long comes at once, so that the method runs as many times as the
// time says) or the timer stops (After), then the method runs.
static void timer_event(u8 source)
{
    (void)source;
    if (!timer_on)
    {
        return;
    }

    if ((i32)(chipsharp_clock_microseconds() - timer_next) < 0)
    {
        arm();
        return;
    }

    if (timer_period != 0)
    {
        timer_next += timer_period;
        arm();
    }
    else
    {
        stop_timer();
    }

    chipsharp_isr_irq27();
}

// The event comes first after microseconds, then every period microseconds (0: only once).
static void start_timer(u32 microseconds, u32 period)
{
    u8 sreg = interrupts_off();
    chipsharp_interrupt_hooks[HOOK_TIMER] = timer_event;
    timer_on = 1;
    timer_period = period;
    timer_next = chipsharp_clock_microseconds() + microseconds;
    arm();
    REG(TIMSK1) |= TIMSK1_OCIE1B;
    interrupts_back(sreg);
}

static u32 milliseconds_as_microseconds(i32 milliseconds)
{
    if (milliseconds <= 0)
    {
        return 0;
    }

    u32 capped = (u32)milliseconds > MAX_MILLISECONDS ? MAX_MILLISECONDS : (u32)milliseconds;
    return capped * 1000u;
}

static void every(u32 microseconds)
{
    if (microseconds == 0)
    {
        u8 sreg = interrupts_off();
        stop_timer();
        interrupts_back(sreg);
    }
    else
    {
        start_timer(microseconds, microseconds);
    }
}

void ChipSharp_Runtime_TimerEveryMilliseconds(i32 milliseconds)
{
    every(milliseconds_as_microseconds(milliseconds));
}

void ChipSharp_Runtime_TimerEveryMicroseconds(i32 microseconds)
{
    every(microseconds > 0 ? (u32)microseconds : 0u);
}

void ChipSharp_Runtime_TimerAfterMilliseconds(i32 milliseconds)
{
    start_timer(milliseconds_as_microseconds(milliseconds), 0);
}

void ChipSharp_Runtime_TimerAfterMicroseconds(i32 microseconds)
{
    start_timer(microseconds > 0 ? (u32)microseconds : 0u, 0);
}

void ChipSharp_Runtime_TimerStop(void)
{
    every(0);
}
