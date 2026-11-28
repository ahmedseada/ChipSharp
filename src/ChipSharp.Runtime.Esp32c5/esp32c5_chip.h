// ChipSharp runtime for the Espressif ESP32-C5: what the runtime's C files share (the chip's registers, how an interrupt
// is routed to the CPU, the few helpers every file uses). Only the runtime's own files include it; the hardware
// contract's functions are in esp32c5_runtime.c (start-up, pins, delays, clock, registers), esp32c5_interrupts.c (the
// timer and pin events), esp32c5_usb.c (the console), esp32c5_uart.c (UART0 and the uart event), esp32c5_failures.c.
//
// Interrupts. A peripheral's interrupt is a source of the interrupt matrix (esp32c5_interrupts.h, ESP-IDF's
// soc/interrupts.h), whose map register (base + 4 * source) holds the CLIC interrupt id of a CPU line to raise: 16 to
// 47 for the 32 lines (0 is none; TRM 11.5.3). The CLIC (TRM 2.8.2, ESP-IDF's soc/clic_reg.h) then takes the id when it
// is enabled (its IE byte), above the threshold, and interrupts are on (mstatus.MIE); the handler is the trap entry's
// dispatch (riscv32_vectors.c): chipsharp_isr_irq<id>. Every id the runtime uses is level-triggered and not vectored (as
// the CLIC resets them): the line stays high until the handler clears the source. The runtime's ids (these are the
// target's "interrupts" table, targets/chip/esp32c5.json, for the three events a program's [Interrupt] methods handle):
//
//   16  alarm 0 of SYSTIMER (source SYSTIMER_TARGET0, 61): the end of a delay           esp32c5_runtime.c
//   17  alarm 1 (SYSTIMER_TARGET1, 62): the timer event                                 esp32c5_interrupts.c
//   18  GPIO (31): the pin event                                                        esp32c5_interrupts.c
//   19  the uart event: UART0 (47) while a program watches it, and the software interrupt FROM_CPU_INTR0 (23)
//       that runs it for bytes that were already there                                   esp32c5_uart.c
//   20  UART0 (47) while nothing watches it: the runtime's handler fills the receive buffer   esp32c5_uart.c
//   21  USB Serial/JTAG (54): a packet came from the host, which wakes Console.Read     esp32c5_usb.c
//
// The handlers run one at a time (the trap turns MIE off, MRET turns it back on), all at the same level.

#ifndef ESP32C5_CHIP_H
#define ESP32C5_CHIP_H

#include "chipsharp_hardware.h"
#include "esp32c5_registers.h"
#include "esp32c5_interrupts.h"
#include "esp32c5_pins.h"

// SYSTIMER's ticks: 16 a microsecond (CNT_CLK, the crystal over 3; TRM 14.1)
#define C5_TICKS_PER_MICROSECOND 16u

// The interrupt ids of the runtime (above)
#define C5_ID_DELAY 16u
#define C5_ID_TIMER_EVENT 17u
#define C5_ID_PIN_EVENT 18u
#define C5_ID_UART_EVENT 19u
#define C5_ID_UART_RECEIVE 20u
#define C5_ID_USB_RECEIVE 21u

// The CLIC: one word of four bytes per interrupt id at 0x20801000 + 4 * id: pending (IP, bit 0), enable (IE, bit 0),
// attributes (SHV bit 0, TRIG bits 2-1, MODE bits 7-6) and control (CTL: the level and priority); mintthresh (CSR 0x347)
// is the level an interrupt must be above.
#define C5_CLIC_BASE 0x20800000u
#define C5_CLIC_BYTE(id, byte) (*(volatile unsigned char *)(C5_CLIC_BASE + 0x1000u + 4u * (unsigned int)(id) + (byte)))
#define C5_CLIC_IE 1u
#define C5_CLIC_ATTR 2u
#define C5_CLIC_LEVEL_MACHINE 0xc0u // MODE 3 (machine), TRIG 0 (level, positive), SHV 0 (not vectored)

// The interrupt matrix's map register of a source: base + 4 * source (ESP-IDF's interrupt_matrix_reg.h; the SVD leaves
// out the maps of sources 42 and 43, so its names from MSPI on are off: INTERRUPT_CORE0_INTR_MAP0 is the base)
#define C5_MAP(source) (*(volatile uint32_t *)(INTERRUPT_CORE0_INTR_MAP0_ADDRESS + 4u * (unsigned int)(source)))

// The pins' registers, by GPIO number: the IO MUX's, the GPIO matrix's output selection and the pin's configuration
// (every pin's registers are the first pin's, 4 bytes apart)
#define C5_IO_MUX(pin) (*(volatile uint32_t *)(IO_MUX_GPIO0_ADDRESS + 4u * (unsigned int)(pin)))
#define C5_GPIO_OUT_SEL(pin) (*(volatile uint32_t *)(GPIO_FUNC0_OUT_SEL_CFG_ADDRESS + 4u * (unsigned int)(pin)))
#define C5_GPIO_PIN(pin) (*(volatile uint32_t *)(GPIO_PIN0_ADDRESS + 4u * (unsigned int)(pin)))

// GPIO_FUNCn_OUT_SEL_CFG's selection of the simple GPIO output: bit n of GPIO_OUT, driven where GPIO_ENABLE's bit n is set
#define C5_SIMPLE_GPIO_OUTPUT 256u

// The pins GPIO0 to GPIO28
#define C5_PIN_COUNT 29

// Interrupts off, returning whether they were on (mstatus.MIE, bit 3), and back as they were.
static inline unsigned int c5_interrupts_off(void)
{
    unsigned int mstatus;
    __asm__ volatile("csrrci %0, mstatus, 8" : "=r"(mstatus) : : "memory");
    return mstatus & 8u;
}

static inline void c5_interrupts_back(unsigned int enabled)
{
    if (enabled != 0)
    {
        __asm__ volatile("csrsi mstatus, 8" : : : "memory");
    }
}

// Routes an interrupt source to the CPU line of CLIC id (0: none), and enables the id (level-triggered, not vectored).
static inline void c5_route(unsigned int source, unsigned int id)
{
    C5_MAP(source) = id;
}

static inline void c5_clic_enable(unsigned int id)
{
    C5_CLIC_BYTE(id, C5_CLIC_ATTR) = C5_CLIC_LEVEL_MACHINE;
    C5_CLIC_BYTE(id, C5_CLIC_IE) = 1;
}

// SYSTIMER_CONF sets the bits that let an alarm's comparator work (TARGETn_WORK_EN, once and for all: a comparator that starts
// to work takes the target it has), with interrupts off: the register is shared by both alarms and unit 0.
static inline void c5_alarm_working(unsigned int mask)
{
    unsigned int enabled = c5_interrupts_off();
    SYSTIMER_CONF |= mask;
    c5_interrupts_back(enabled);
}

// esp32c5_runtime.c: SYSTIMER's unit 0, microseconds' worth of 16 ticks, since the chip was reset (all 52 bits, read
// with interrupts off)
unsigned long long chipsharp_c5_ticks(void);

// esp32c5_runtime.c: waiting for an interrupt without the race of "check, then wfi" (the interrupt comes in between and
// the core sleeps for one that may never come). chipsharp_c5_sleep returns at once when *woken is not 0, else sleeps
// (wfi) until an interrupt. A handler that ends the wait calls chipsharp_c5_wake on the same flag: it sets it, and when
// it interrupted chipsharp_c5_sleep before its wfi, makes the core start it again (mepc back to its check), which then
// sees the flag. Interrupts must be on, or nothing wakes the core (the simulator and the chip alike); with them off the
// caller polls instead.
void chipsharp_c5_sleep(volatile unsigned int *woken);
void chipsharp_c5_wake(volatile unsigned int *woken);

// esp32c5_usb.c: the console's bytes, sent in packets
void chipsharp_c5_usb_write(const char *bytes, unsigned int count);

#endif
