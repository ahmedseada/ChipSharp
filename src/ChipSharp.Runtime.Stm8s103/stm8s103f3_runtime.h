// ChipSharp runtime for the STM8S103F3: what the chip runtime's C files share. The registers, interrupts and pins are
// the headers ChipSharp.Chips writes from chips/stm8s103f3.json (stm8s103f3_registers.h, _interrupts.h, _pins.h).

#ifndef STM8S103F3_RUNTIME_H
#define STM8S103F3_RUNTIME_H

#include "stm8_runtime.h"
#include "stm8s103f3_registers.h"
#include "stm8s103f3_interrupts.h"
#include "stm8s103f3_pins.h"

// fMASTER: the HSI's 16 MHz, undivided from start-up on
#define MASTER_HZ 16000000ul

// The entries of the program's [Interrupt] methods, by the numbers of the target's "interrupts" table
// (targets/chip/stm8s103f3.json): past the 30 interrupts of the chip's vector table, as the Uno's
#define PIN_EVENT chipsharp_isr_irq30
#define TIMER_EVENT chipsharp_isr_irq31
#define UART_EVENT chipsharp_isr_irq32
void PIN_EVENT(void);
void TIMER_EVENT(void);
void UART_EVENT(void);

// ---- In stm8s103f3_runtime.c: the start-up, the clock's timer, the interrupts the runtime takes -------------------

// The handlers of the runtime's own interrupts whose code is in a module a program may not have: the timer event's
// (TIM1's compare 1), the pin event's (EXTI, the port's number given), UART1's receiving. The module that uses one puts
// its handler here when the program first uses it; the vector table, in flash, names the entries in every program.
extern void (*volatile chipsharp_timer_hook)(void);
extern void (*volatile chipsharp_pin_hook)(u8 port);
extern void (*volatile chipsharp_uart_hook)(void);

// Set once a byte has gone to UART1, until its speed is set again: chipsharp_before_halt then waits for the last to go.
extern volatile u8 chipsharp_uart1_sent;

// The clock now, with interrupts off: TIM1's rounds (of 65.536 ms) and its count of microseconds.
void chipsharp_clock_read(u32 *rounds, u16 *count) CHIPSHARP_REENTRANT;

// TIM1's count alone, in a few instructions: what the delays and the buses' time limits watch.
u16 chipsharp_clock_count(void) CHIPSHARP_REENTRANT;

// ---- In stm8s103f3_pins.c -------------------------------------------------------------------------------------------

// The registers of the port of pin (ODR; IDR, DDR, CR1 and CR2 follow it) and its bit's mask; 0 for a number that is no
// pin of the chip.
volatile u8 *chipsharp_pin_port(i32 pin, u8 *mask) CHIPSHARP_REENTRANT;

#define PORT_ODR 0
#define PORT_IDR 1
#define PORT_DDR 2
#define PORT_CR1 3
#define PORT_CR2 4

// ---- In stm8s103f3_console.c: UART1's sending -----------------------------------------------------------------------

// Set once UART1 is set up (by the console, Uart.Open or Console.Read), whose speed the console then keeps.
extern u8 chipsharp_uart1_started;

// Sets UART1 up: its divider of fMASTER, 8N1, sending on PD5, and receiving on PD6 when receive is set (with the
// receive interrupt, stm8s103f3_uart.c's). What it was still sending goes out first, at the speed it had.
void chipsharp_uart1_start(u16 divider, u8 receive) CHIPSHARP_REENTRANT;

// count bytes to UART1, once it has room for each; at 115200 baud from the first one unless it was set up.
void chipsharp_uart1_write(const char *bytes, chipsharp_count count) CHIPSHARP_REENTRANT;

// 16 MHz / 139: 115108 baud, 0.08% slow
#define DIVIDER_115200 139u

#endif
