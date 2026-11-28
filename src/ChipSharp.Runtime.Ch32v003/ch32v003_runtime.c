// ChipSharp runtime for the WCH CH32V003: what this chip needs on top of the riscv32e runtime, the part every program
// has: its set-up, SysTick (the time the delays, the clock and the timer event count), the delays, the clock, the
// registers, and what runs before the program stops.
//
// Freestanding: no C library, and no header but ChipSharp's hardware contract (chipsharp_hardware.h), which this file
// and the others of the ch32v003 runtime implement, and the chip's registers, interrupts and pins as ChipSharp.Chips
// writes them from WCH's SVD and chips/ch32v003.json; ch32v003_chip.h has what the files share and how the interrupts
// are taken. The facts and their sources are in targets/chip/ch32v003.json.
//
//   chipsharp_chip_init                start-up (chipsharp_start runs it before the program, interrupts off; the clock
//                                      is at 48 MHz already, ch32v003_boot.c): the clocks of the ports A, C, D and AFIO
//                                      on (RCC_APB2PCENR: a peripheral's registers do nothing while its clock is off),
//                                      SysTick counting from 0, its interrupt on, then interrupts on
//   ch32_ticks, ch32_arm, ...          SysTick's 64-bit count and its one compare, which the delays, the timer event
//                                      (ch32v003_timer.c) and the count's own guard share (ch32v003_chip.h)
//   chipsharp_isr_irq12                SysTick's interrupt: the timer event when its time has come, a delay's end
//   ChipSharp_Runtime_Delay...         ChipSharp.Hardware.Timing: the core sleeps (wfi) until the compare's interrupt
//   ChipSharp_Runtime_Clock...         the clock (DateTime.Now, Clock.Start) and the uptime (Stopwatch,
//                                      Environment.TickCount64): SysTick's count, 6 a microsecond
//   ChipSharp_Runtime_Register...      ChipSharp.Hardware.Registers: 32-bit reads and writes of any address
//   chipsharp_before_halt              what runs before the program stops (it ended, or failed): what USART1 still
//                                      sends goes out, then interrupts off, every one of the PFIC's too: the program's
//                                      end is the chip's, its events end with it (as on the Uno), and the wfi of
//                                      chipsharp_halt sleeps with nothing left to wake the core
//
// SysTick (RM 6.5.4, QKV2 4): STK_CTLR's STE (on), STIE (its interrupt, vector 12), STCLK 0 (HCLK / 8: 48 MHz / 8,
// 6 MHz), STRE 0 (it counts on past the compare, round at 2^32), counting up from STK_CNTL's 0; STK_SR.CNTIF is set
// when the count reaches STK_CMPLR, and a write of 0 clears it. 6 counts a microsecond: a delay ends, and the timer
// event comes, within a few microseconds of its time (the handler's entry, the trap entry's ten registers).

#include "chipsharp_hardware.h"
#include "ch32v003_chip.h"

// The furthest ahead the compare is armed: half of SysTick's round, so that its interrupt reads the count (and counts
// its rounds) at least twice a round even when nothing else reads it
#define GUARD 0x80000000ull

// The PFIC's interrupts a program may have turned on (RM 6.5.2): 12, 14 and 16-31 in IRER1, 32-38 in IRER2
#define IRER1_ALL 0xffff5000u
#define IRER2_ALL 0x7fu

// The CSR mepc, where an interrupt returns to (to restart a wait)
#define CSR_MEPC "0x341"

u64 ch32_deadlines[2];
void (*volatile ch32_timer_hook)(void);
void (*volatile ch32_usart_hook)(void);
volatile u32 ch32_woken;

// SysTick's rounds of 2^32 counts, and the count when it was last read: a count below it has gone round
static u32 rounds;
static u32 last;

// ---- Start-up ----------------------------------------------------------------------------------------------------

void chipsharp_chip_init(void)
{
    RCC_APB2PCENR |= RCC_APB2PCENR_AFIOEN_MASK | RCC_APB2PCENR_IOPAEN_MASK | RCC_APB2PCENR_IOPCEN_MASK
        | RCC_APB2PCENR_IOPDEN_MASK;

    ch32_deadlines[CH32_DELAY] = CH32_NEVER;
    ch32_deadlines[CH32_TIMER] = CH32_NEVER;
    PFIC_STK_CTLR = 0;
    PFIC_STK_CNTL = 0;
    PFIC_STK_CMPLR = (u32)GUARD;
    PFIC_STK_SR = 0;
    PFIC_STK_CTLR = PFIC_STK_CTLR_STE_MASK | PFIC_STK_CTLR_STIE_MASK;
    ch32_enable(CH32V003_IRQ_SysTick);
    __asm__ volatile("csrsi mstatus, 8" : : : "memory");
}

// ---- SysTick ------------------------------------------------------------------------------------------------------

u64 ch32_ticks(void)
{
    u32 enabled = ch32_interrupts_off();
    u32 count = PFIC_STK_CNTL;
    if (count < last)
    {
        rounds++;
    }

    last = count;
    u64 ticks = ((u64)rounds << 32) | count;
    ch32_interrupts_back(enabled);
    return ticks;
}

// ticks / 6 = (ticks / 2) / 3, and n / 3 is n * 5/16 * (1 + 1/16)(1 + 1/256)(1 + 2^-16)(1 + 2^-32), which is
// n / 3 * (1 - 2^-64): shifts and adds, each of which drops a bit or two, so the quotient is a few short at most and
// the remainder tells by how many.
u64 ch32_microseconds(u64 ticks)
{
    u64 n = ticks >> 1;
    u64 quotient = (n >> 2) + (n >> 4);
    quotient += quotient >> 4;
    quotient += quotient >> 8;
    quotient += quotient >> 16;
    quotient += quotient >> 32;
    u64 remainder = n - (quotient + (quotient << 1));
    while (remainder >= 3u)
    {
        remainder -= 3u;
        quotient++;
    }

    return quotient;
}

// A delay's end that has passed is its wait's to see (the interrupt that came for it woke the wait); the timer event's
// time that has passed is the handler's, which runs the event.
void ch32_arm(void)
{
    u64 now = ch32_ticks();
    u64 at = now + GUARD;
    u64 delay = ch32_deadlines[CH32_DELAY];
    if (delay > now && delay < at)
    {
        at = delay;
    }

    if (ch32_deadlines[CH32_TIMER] < at)
    {
        at = ch32_deadlines[CH32_TIMER];
    }

    // the compare matches when the count comes to it: one the count has passed already would match only a round
    // later, so the interrupt is made to come now
    PFIC_STK_CMPLR = (u32)at;
    if (ch32_ticks() >= at)
    {
        ch32_pend(CH32V003_IRQ_SysTick);
    }
}

// SysTick's interrupt (vector 12): CNTIF cleared, the timer event when its time has come (its module put its hook in
// when the program started it), a wait woken (a delay looks at the count again), the compare at the next deadline.
void chipsharp_isr_irq12(void)
{
    PFIC_STK_SR = 0;
    void (*hook)(void) = ch32_timer_hook;
    if (hook != 0 && ch32_ticks() >= ch32_deadlines[CH32_TIMER])
    {
        hook();
    }

    ch32_wake();
    ch32_arm();
}

// ---- Waiting for an interrupt ------------------------------------------------------------------------------------

// The check and the wfi are these two instructions and a branch, in assembly so that nothing is put between them; a
// handler that interrupts them before the wfi sets mepc back to the check (ch32_wake). As the ESP32-C5's runtime does.
__asm__(
    "    .section .text.ch32_sleep, \"ax\", @progbits\n"
    "    .global ch32_sleep\n"
    "    .type ch32_sleep, @function\n"
    "    .global ch32_sleep_check\n"
    "    .global ch32_sleep_wfi\n"
    "    .balign 4\n"
    "ch32_sleep:\n"
    "ch32_sleep_check:\n"
    "    lw t0, 0(a0)\n"
    "    bnez t0, 1f\n"
    "ch32_sleep_wfi:\n"
    "    wfi\n"
    "1:  ret\n"
    "    .size ch32_sleep, . - ch32_sleep\n");

extern char ch32_sleep_check[];
extern char ch32_sleep_wfi[];

void ch32_wake(void)
{
    ch32_woken = 1;
    u32 pc;
    __asm__ volatile("csrr %0, " CSR_MEPC : "=r"(pc));
    if (pc >= (u32)(__UINTPTR_TYPE__)ch32_sleep_check && pc <= (u32)(__UINTPTR_TYPE__)ch32_sleep_wfi)
    {
        __asm__ volatile("csrw " CSR_MEPC ", %0" : : "r"((u32)(__UINTPTR_TYPE__)ch32_sleep_check));
    }
}

// ---- Delays ------------------------------------------------------------------------------------------------------

// Waits until the count reaches end: asleep (wfi) until SysTick's interrupt, the compare armed for it. With interrupts
// off (the program never turns them off, but a stop in the start-up could) the count is polled.
static void wait_until(u64 end)
{
    u32 mstatus;
    __asm__ volatile("csrr %0, mstatus" : "=r"(mstatus));
    if ((mstatus & 8u) == 0)
    {
        while (ch32_ticks() < end)
        {
        }

        return;
    }

    u32 enabled = ch32_interrupts_off();
    ch32_deadlines[CH32_DELAY] = end;
    ch32_arm();
    ch32_interrupts_back(enabled);
    for (;;)
    {
        ch32_woken = 0;
        if (ch32_ticks() >= end)
        {
            break;
        }

        ch32_sleep(&ch32_woken);
    }

    enabled = ch32_interrupts_off();
    ch32_deadlines[CH32_DELAY] = CH32_NEVER;
    ch32_arm();
    ch32_interrupts_back(enabled);
}

void ChipSharp_Runtime_DelayMicroseconds(chipsharp_i32 microseconds)
{
    if (microseconds > 0)
    {
        wait_until(ch32_ticks() + CH32_TIMES_6((u64)(u32)microseconds));
    }
}

void ChipSharp_Runtime_DelayMilliseconds(chipsharp_i32 milliseconds)
{
    if (milliseconds > 0)
    {
        wait_until(ch32_ticks() + (u64)(u32)milliseconds * (1000u * CH32_TICKS_PER_MICROSECOND));
    }
}

// ---- The clock and the uptime ------------------------------------------------------------------------------------

// DateTime's ticks (100 ns) of 1 January 2000, the clock's date until Clock.Start gives it one
#define CLOCK_START 630822816000000000LL

// What Clock.Start moved the clock by, in ticks from CLOCK_START; read and written with interrupts off, so that a
// handler that starts the clock never leaves half of it for Main to read
static long long clock_offset;

// Microseconds since start-up: SysTick counted from 0 at chipsharp_chip_init.
static u64 uptime_microseconds(void)
{
    return ch32_microseconds(ch32_ticks());
}

// DateTime.UtcNow, DateTime.Now: the chip has no time zones, so both are the clock's date and the time since.
chipsharp_i64 ChipSharp_Runtime_ClockUtcTicks(void)
{
    u32 enabled = ch32_interrupts_off();
    long long offset = clock_offset;
    u64 microseconds = uptime_microseconds();
    ch32_interrupts_back(enabled);
    return CLOCK_START + offset + (long long)CH32_TIMES_10(microseconds);
}

chipsharp_i64 ChipSharp_Runtime_ClockLocalTicks(void)
{
    return ChipSharp_Runtime_ClockUtcTicks();
}

// Clock.Start(date): the clock counts from date's ticks from now on.
void ChipSharp_Runtime_ClockStart(chipsharp_i64 ticks)
{
    u32 enabled = ch32_interrupts_off();
    clock_offset = ticks - CLOCK_START - (long long)CH32_TIMES_10(uptime_microseconds());
    ch32_interrupts_back(enabled);
}

// Stopwatch.GetTimestamp, in microseconds since start-up; Stopwatch.Frequency is 1,000,000.
chipsharp_i64 ChipSharp_Runtime_ClockTimestamp(void)
{
    return (chipsharp_i64)uptime_microseconds();
}

chipsharp_i64 ChipSharp_Runtime_ClockFrequency(void)
{
    return 1000000;
}

// Stopwatch.GetElapsedTime: 10 ticks of 100 ns a microsecond, exactly what .NET's double arithmetic gives.
chipsharp_i64 ChipSharp_Runtime_ClockElapsedTicks(chipsharp_i64 start, chipsharp_i64 end)
{
    return (chipsharp_i64)CH32_TIMES_10((u64)end - (u64)start);
}

// Environment.TickCount64: milliseconds since start-up.
chipsharp_i64 ChipSharp_Runtime_ClockTickCount64(void)
{
    return (chipsharp_i64)(uptime_microseconds() / 1000u);
}

// ---- Registers ---------------------------------------------------------------------------------------------------

chipsharp_u32 ChipSharp_Runtime_RegisterRead(chipsharp_u32 address)
{
    return *(volatile chipsharp_u32 *)(__UINTPTR_TYPE__)address;
}

void ChipSharp_Runtime_RegisterWrite(chipsharp_u32 address, chipsharp_u32 value)
{
    *(volatile chipsharp_u32 *)(__UINTPTR_TYPE__)address = value;
}

// ---- The program's end -------------------------------------------------------------------------------------------

// Polling USART1 (interrupts may be off already): the last byte has gone out once TC is set (1 after reset, too). Its
// registers only once its clock is on (they read nothing before).
void chipsharp_before_halt(void)
{
    if (CH32_USART_STARTED())
    {
        while ((USART1_STATR & USART1_STATR_TC_MASK) == 0)
        {
        }
    }

    __asm__ volatile("csrci mstatus, 8" : : : "memory");
    PFIC_IRER1 = IRER1_ALL;
    PFIC_IRER2 = IRER2_ALL;
}
