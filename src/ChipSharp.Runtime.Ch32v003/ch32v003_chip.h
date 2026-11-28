// ChipSharp runtime for the WCH CH32V003: what the runtime's C files share (the chip's registers, interrupts and pins,
// the time on SysTick, the hooks of the interrupts the runtime takes, the few helpers every file uses). Only the
// runtime's own files include it; the hardware contract's functions are in ch32v003_runtime.c (start-up, SysTick, the
// delays, the clock, registers, what runs before the program stops), ch32v003_pins.c (pins, pulls, the pin event),
// ch32v003_timer.c (the timer event), ch32v003_usart.c (USART1: the console both ways, Uart, the uart event) and
// ch32v003_failures.c (what .NET would throw, said on the console). The facts and their sources are in
// targets/chip/ch32v003.json: WCH's CH32V003 Reference Manual (RM, V1.5), its datasheet (DS, V1.8) and the QingKe V2
// Microprocessor Manual (QKV2, V1.1).
//
// Interrupts. The PFIC (RM 6.3, QKV2 3) takes every interrupt to the riscv32 runtime's trap entry, which dispatches by
// its vector number to chipsharp_isr_irq<n>. The runtime's (each enabled in the PFIC only once something uses it):
//
//   12  SysTick: its compare matched (the end of a delay, the timer event's time, or the count's guard; below)
//       ch32v003_runtime.c, which runs the timer event's method, chipsharp_isr_irq39, when its time has come
//   20  EXTI7_0: a watched pin's edge, the program's [Interrupt(Event.Pin)] method itself (the target's "interrupts.pin")
//   32  USART1: a byte received, which the runtime puts in the buffer; then, on a watched port, the uart event's method,
//       chipsharp_isr_irq40
//
// The timer and uart events are numbered past the PFIC's 39 (targets/chip/ch32v003.json's "interrupts", as the Uno's
// and the STM8S103's): their interrupts are the runtime's, which find out whether the event happened and then call the
// method's entry, an ordinary function. Handlers do not nest (INTSYSCR 0, ch32v003_boot.c): each runs to its end with
// interrupts off (the trap clears mstatus.MIE, MRET puts it back).
//
// Time. SysTick (STK, RM 6.5.4, QKV2 4) counts up from 0 at HCLK / 8, 6 MHz (6 counts a microsecond), in 32 bits, which
// go round every 715.8 s; ch32_ticks makes them 64 by counting the rounds each time it reads the count, which it does
// at least every 2^31 counts (358 s): the compare (STK_CMPLR, one for everything) is never armed further ahead than
// that, so its interrupt reads the count in time even when nothing else does. The compare is armed for the nearest of a
// delay's end, the timer event's time and that guard (ch32_arm).

#ifndef CH32V003_CHIP_H
#define CH32V003_CHIP_H

#include "chipsharp_hardware.h"
#include "ch32v003_registers.h"
#include "ch32v003_interrupts.h"
#include "ch32v003_pins.h"

typedef unsigned char u8;
typedef unsigned int u32;
typedef unsigned long long u64;

// SysTick's counts: HCLK (48 MHz) / 8
#define CH32_TICKS_PER_MICROSECOND 6u

// A time that never comes: a deadline nobody waits for
#define CH32_NEVER 0xffffffffffffffffull

// The deadlines ch32_arm arms the compare for: a delay's end, the timer event's next time
#define CH32_DELAY 0
#define CH32_TIMER 1

// The pins a program may use: the package's (ch32v003_pins.h) but PD1, the debug wire (SWIO), and PD7, NRST, which the
// chip's reset leaves to them (DS Table 2-1; RM 7): a number no usable pin has is ignored, as a pin the chip lacks
#define CH32_USABLE_PINS (CH32V003_PINS & ~((1ul << CH32V003_PIN_PD1) | (1ul << CH32V003_PIN_PD7)))
#define CH32_IS_USABLE(pin) ((pin) >= 0 && (pin) <= 31 && ((CH32_USABLE_PINS >> (pin)) & 1u) != 0)

// A pin's port registers: GPIOA at 0x40010800, GPIOC 0x40011000, GPIOD 0x40011400, 0x400 apart by the port's letter
// (pin >> 3: A 0, C 2, D 3), the same order as AFIO_EXTICR's port codes (00 PA, 10 PC, 11 PD; RM 7.3, AFIO_EXTICR)
#define CH32_PORT(pin) (0x40010800u + 0x400u * ((u32)(pin) >> 3))
#define CH32_CFGLR(pin) (*(volatile u32 *)(CH32_PORT(pin) + 0x00u))
#define CH32_INDR(pin) (*(volatile u32 *)(CH32_PORT(pin) + 0x08u))
#define CH32_OUTDR(pin) (*(volatile u32 *)(CH32_PORT(pin) + 0x0cu))
#define CH32_BSHR(pin) (*(volatile u32 *)(CH32_PORT(pin) + 0x10u))
#define CH32_BCR(pin) (*(volatile u32 *)(CH32_PORT(pin) + 0x14u))

// CFGLR's four bits of a pin (RM 7.3, GPIOx_CFGLR): MODE (1:0) 00 input, 01 output at up to 10 MHz; CNF (3:2) of an
// input 01 floating, 10 with a pull (OUTDR's bit: 1 up, 0 down), of an output 00 push-pull, 10 a peripheral's push-pull
#define CH32_CFG_OUTPUT 0x1u
#define CH32_CFG_FLOATING 0x4u
#define CH32_CFG_PULL 0x8u
#define CH32_CFG_ALTERNATE 0x9u

// The entries of the program's [Interrupt] methods the runtime calls, by the numbers of the target's "interrupts" table
#define TIMER_EVENT chipsharp_isr_irq39
#define UART_EVENT chipsharp_isr_irq40
void TIMER_EVENT(void);
void UART_EVENT(void);

// The PFIC's enables (RM 6.5.2: IENR1 for 12, 14 and 16-31, IENR2 for 32-38; a write of 1 enables, IRER's disables) and
// pending bits (IPSR: a 1 makes an interrupt pending, as if its peripheral had asked)
static inline void ch32_enable(u32 number)
{
    if (number < 32u)
    {
        PFIC_IENR1 = 1u << number;
    }
    else
    {
        PFIC_IENR2 = 1u << (number - 32u);
    }
}

static inline void ch32_pend(u32 number)
{
    if (number < 32u)
    {
        PFIC_IPSR1 = 1u << number;
    }
    else
    {
        PFIC_IPSR2 = 1u << (number - 32u);
    }
}

// Interrupts off, returning whether they were on (mstatus.MIE, bit 3), and back as they were.
static inline u32 ch32_interrupts_off(void)
{
    u32 mstatus;
    __asm__ volatile("csrrci %0, mstatus, 8" : "=r"(mstatus) : : "memory");
    return mstatus & 8u;
}

static inline void ch32_interrupts_back(u32 enabled)
{
    if (enabled != 0)
    {
        __asm__ volatile("csrsi mstatus, 8" : : : "memory");
    }
}

// x * (2^a + 2^b) as two shifts and an add, a handful of instructions: written plainly (x * 6, or the shifts and the
// add), LLVM makes a 64-bit multiplication of it, which RV32EC has no instruction for (a call of __muldi3, a loop), so
// the empty asm hides one shift from it. 6 is (2, 1): microseconds to SysTick's counts; 10 is (3, 1): to DateTime's.
static inline u64 ch32_times(u64 x, u32 a, u32 b)
{
    u64 shifted = x << a;
    u32 low = (u32)shifted;
    u32 high = (u32)(shifted >> 32);
    __asm__("" : "+r"(low), "+r"(high));
    return (((u64)high << 32) | low) + (x << b);
}

#define CH32_TIMES_6(x) ch32_times((x), 2u, 1u)
#define CH32_TIMES_10(x) ch32_times((x), 3u, 1u)

// ---- In ch32v003_runtime.c --------------------------------------------------------------------------------------------

// SysTick's counts since start-up, all 64 bits (read with interrupts off, and the rounds counted)
u64 ch32_ticks(void) CHIPSHARP_REENTRANT;

// Microseconds of a count: ticks / 6, with shifts and adds (RV32EC has no divide instruction)
u64 ch32_microseconds(u64 ticks) CHIPSHARP_REENTRANT;

// The tick each deadline waits for (CH32_DELAY, CH32_TIMER), CH32_NEVER for none; set with interrupts off, then ch32_arm
extern u64 ch32_deadlines[2];

// The compare at the nearest deadline still to come (or the guard); the timer event's time when it has passed (or one
// that passes while it is armed) makes SysTick's interrupt pending, so that its handler comes at once. Interrupts are off.
void ch32_arm(void) CHIPSHARP_REENTRANT;

// What the handlers of the runtime's interrupts call, once a module has put it in: the timer event's, when its time has
// come (SysTick); USART1's receiving
extern void (*volatile ch32_timer_hook)(void);
extern void (*volatile ch32_usart_hook)(void);

// Waiting for an interrupt without the race of "check, then wfi" (the interrupt comes in between, and the core sleeps for
// one that may never come): ch32_sleep returns at once when *woken is not 0, else sleeps (wfi) until an interrupt. A
// handler that may end a wait calls ch32_wake: it sets the flag, and when it interrupted ch32_sleep before its wfi,
// makes the core start it again (mepc back to its check), which then sees the flag. Interrupts must be on.
extern volatile u32 ch32_woken;
void ch32_sleep(volatile u32 *woken);
void ch32_wake(void);

// ---- In ch32v003_usart.c ----------------------------------------------------------------------------------------------

// USART1's clock (RCC_APB2PCENR.USART1EN) is on once the console, Uart.Open or Console.Read has started it
#define CH32_USART_STARTED() ((RCC_APB2PCENR & RCC_APB2PCENR_USART1EN_MASK) != 0)

#endif
