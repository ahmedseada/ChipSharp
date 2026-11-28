// ChipSharp runtime for the ST STM8S103F3 at 16 MHz (the blue STM8S103F3P6 board's chip): what this chip needs on top of
// the STM8 runtime (src/ChipSharp.Runtime.Stm8: start-up, the stops, the failures). This file is the part every program
// has: the chip's set-up, the clock's timer and the handlers of the interrupts the runtime takes.
//
// Freestanding: no C library, and no header but ChipSharp's hardware contract (chipsharp_hardware.h), which this file
// and the others of the stm8s103f3 runtime implement, and the chip's registers, interrupts and pins as ChipSharp.Chips
// writes them from chips/stm8s103f3.json (RM0016 and the datasheet). A C# int is 32 bits, a long in SDCC's C, whose int
// has 16; the chip keeps a value's high byte first, which nothing here depends on.
//
//   chipsharp_chip_init       start-up (main, stm8_startup.c, runs it before the program, interrupts still off): the
//                             master clock to the HSI's 16 MHz undivided (2 MHz after reset), and TIM1 counting the clock
//   chipsharp_before_halt     what runs before the program stops (it ended, or failed): what UART1 is still sending goes
//                             out, and no pin's interrupt is left on, so that nothing wakes the chip from its halt
//   chipsharp_clock_read, ... the clock, for the delays, the clock's functions, the timer event, the buses' time limits
//   the interrupt handlers    TIM1's update (the clock's rounds), and the entries of the interrupts a program's code
//                             starts: TIM1's compare 1 (the timer event), EXTI of ports A-D (the pin event), UART1's
//                             receiving; each goes to the hook its module put in, if any
//
// The clock: TIM1, prescaled to 1 MHz (16 MHz / 16), counts microseconds from 0 to 0xffff and round again (every 65.536
// ms); its update interrupt counts the rounds in 32 bits: 48 bits of microseconds, which go round after 8.9 years. TIM1's
// compare 1 is the timer event's (stm8s103f3_timer.c), without a pin. TIM2 makes the PWM outputs (stm8s103f3_pwm.c);
// TIM4 is the program's.

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

void (*volatile chipsharp_timer_hook)(void);
void (*volatile chipsharp_pin_hook)(u8 port);
void (*volatile chipsharp_uart_hook)(void);
volatile u8 chipsharp_uart1_sent;

// The rounds TIM1 has counted: its update interrupt adds one each time it goes from 0xffff to 0.
static volatile u32 rounds;

void chipsharp_chip_init(void)
{
    CLK_CKDIVR = CLK_CKDIVR_HSIDIV_DIV1 | CLK_CKDIVR_CPUDIV_DIV1;

    // TIM1: up from 0 to 0xffff (ARR, as after reset), a count every 16 clocks; the update event (UG) loads the
    // prescaler without setting UIF (URS: only an overflow does)
    TIM1_PSCRH = 0;
    TIM1_PSCRL = 15;
    TIM1_CR1 = TIM1_CR1_URS_MASK;
    TIM1_EGR = TIM1_EGR_UG_MASK;
    TIM1_SR1 = 0;
    TIM1_IER = TIM1_IER_UIE_MASK;
    TIM1_CR1 = TIM1_CR1_URS_MASK | TIM1_CR1_CEN_MASK;
}

STM8S103F3_HANDLER(TIM1_UPD_OVF)
{
    TIM1_SR1 = (u8)~TIM1_SR1_UIF_MASK; // rc_w0: a 0 clears the flag, a 1 leaves the others
    rounds++;
}

// TIM1's 16-bit count is read high byte first, which holds the low byte for the read after it. A round TIM1 has just
// finished, whose interrupt has not come yet (interrupts are off, or this runs in a handler), is counted here: the
// update flag is set, and the count is low.
void chipsharp_clock_read(u32 *rounds_now, u16 *count_now)
{
    u8 cc = chipsharp_interrupts_off();
    u8 high = TIM1_CNTRH;
    u8 low = TIM1_CNTRL;
    u32 counted = rounds;
    if ((TIM1_SR1 & TIM1_SR1_UIF_MASK) != 0 && high < 0x80u)
    {
        counted++;
    }

    chipsharp_interrupts_back(cc);
    *rounds_now = counted;
    *count_now = (u16)((u16)high << 8 | low);
}

u16 chipsharp_clock_count(void)
{
    u8 cc = chipsharp_interrupts_off();
    u8 high = TIM1_CNTRH;
    u8 low = TIM1_CNTRL;
    chipsharp_interrupts_back(cc);
    return (u16)((u16)high << 8 | low);
}

// Polling UART1 (interrupts may be off already): the last byte has gone out once TC is set. Then every port's inputs
// lose their interrupt (CR2: an output's bit there is its fast mode, which stays).
void chipsharp_before_halt(void)
{
    if (chipsharp_uart1_sent)
    {
        while ((UART1_SR & UART1_SR_TC_MASK) == 0)
        {
        }
    }

    PA_CR2 &= PA_DDR;
    PB_CR2 &= PB_DDR;
    PC_CR2 &= PC_DDR;
    PD_CR2 &= PD_DDR;
}

// ---- The interrupts of the program's events ------------------------------------------------------------------------

// Each comes from a module that has put its hook in first; one that comes without (it cannot) is cleared, so that it
// does not come again and again.
STM8S103F3_HANDLER(TIM1_CAP_COM)
{
    void (*hook)(void) = chipsharp_timer_hook;
    if (hook != 0)
    {
        hook();
    }
    else
    {
        TIM1_IER &= (u8)~TIM1_IER_CC1IE_MASK;
        TIM1_SR1 = (u8)~TIM1_SR1_CC1IF_MASK;
    }
}

static void pin_changed(u8 port)
{
    void (*hook)(u8) = chipsharp_pin_hook;
    if (hook != 0)
    {
        hook(port);
    }
}

STM8S103F3_HANDLER(EXTI_PORTA)
{
    pin_changed(0);
}

STM8S103F3_HANDLER(EXTI_PORTB)
{
    pin_changed(1);
}

STM8S103F3_HANDLER(EXTI_PORTC)
{
    pin_changed(2);
}

STM8S103F3_HANDLER(EXTI_PORTD)
{
    pin_changed(3);
}

STM8S103F3_HANDLER(UART1_RX)
{
    void (*hook)(void) = chipsharp_uart_hook;
    if (hook != 0)
    {
        hook();
    }
    else
    {
        // reading SR, then DR, clears RXNE and an overrun
        (void)UART1_SR;
        (void)UART1_DR;
    }
}
