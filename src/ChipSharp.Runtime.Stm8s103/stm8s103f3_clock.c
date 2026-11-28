// ChipSharp runtime for the STM8S103F3: the clock (DateTime.Now, Clock.Start) and the uptime (Stopwatch,
// Environment.TickCount), from TIM1's 48 bits of microseconds since start-up (stm8s103f3_runtime.c); in modules (the STM8
// runtime cuts the file at its "//@module" lines), so a program that times with Stopwatch has none of the date's.
//
// The software clock, in DateTime's ticks of 100 ns: the date it was given (Clock.Start), or 1 January 2000 until it is
// given one, and the microseconds then. Read and written with interrupts off, so that a handler that starts the clock
// never leaves half of it for Main to read. Ten ticks a microsecond: a multiplication for the date (the STM8 runtime's
// helper, stm8_multiply.c, some 100 microseconds), two shifts and an addition for Stopwatch, whose time a program may
// watch in a busy loop (a few microseconds).

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

// In the module "now": microseconds since start-up, all 48 bits.
u64 chipsharp_clock_now(void) CHIPSHARP_REENTRANT;

//@module now
u64 chipsharp_clock_now(void)
{
    u32 counted;
    u16 count;
    chipsharp_clock_read(&counted, &count);
    return (u64)counted << 16 | count;
}

//@module date
static i64 clock_ticks = 630822816000000000ll;
static u64 clock_start;

// DateTime.UtcNow, DateTime.Now: the chip has no time zones, so both are the clock's date and the time since.
chipsharp_i64 ChipSharp_Runtime_ClockUtcTicks(void)
{
    u8 cc = chipsharp_interrupts_off();
    i64 ticks = clock_ticks;
    u64 since = chipsharp_clock_now() - clock_start;
    chipsharp_interrupts_back(cc);
    return ticks + (i64)(since * 10u);
}

chipsharp_i64 ChipSharp_Runtime_ClockLocalTicks(void)
{
    return ChipSharp_Runtime_ClockUtcTicks();
}

// Clock.Start(date): the clock counts from date's ticks from now on.
void ChipSharp_Runtime_ClockStart(chipsharp_i64 ticks)
{
    u8 cc = chipsharp_interrupts_off();
    clock_ticks = ticks;
    clock_start = chipsharp_clock_now();
    chipsharp_interrupts_back(cc);
}

//@module timestamp
// Stopwatch.GetTimestamp, in microseconds since start-up; Stopwatch.Frequency is 1,000,000, as on the Pico and the Uno.
chipsharp_i64 ChipSharp_Runtime_ClockTimestamp(void)
{
    return (i64)chipsharp_clock_now();
}

chipsharp_i64 ChipSharp_Runtime_ClockFrequency(void)
{
    return 1000000;
}

// Stopwatch.GetElapsedTime: 10 ticks of 100 ns a microsecond, exactly what .NET's double arithmetic gives.
chipsharp_i64 ChipSharp_Runtime_ClockElapsedTicks(chipsharp_i64 start, chipsharp_i64 end)
{
    i64 difference = end - start;
    return (difference << 3) + (difference << 1);
}

//@module tick_count
// Environment.TickCount64: milliseconds since start-up.
chipsharp_i64 ChipSharp_Runtime_ClockTickCount64(void)
{
    return (i64)(chipsharp_clock_now() / 1000u);
}
