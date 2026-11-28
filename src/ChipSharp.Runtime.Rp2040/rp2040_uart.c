// ChipSharp runtime for the Raspberry Pi RP2040: the serial ports UART0 and UART1 (ChipSharp.Hardware.Uart), and the
// console's input (Console.Read and Console.ReadLine, on UART0).
//
// Freestanding, like rp2040_runtime.c. 8 data bits, no parity, one stop bit; the FIFOs (32 bytes each way) on.
//
//   ChipSharp_Runtime_UartOpen         Uart.Open: a port on two of its pins, at a speed; it receives from then on
//   ChipSharp_Runtime_UartWriteByte    Uart.Write and WriteByte: one byte, once the send FIFO has room
//   ChipSharp_Runtime_UartReadByte     Uart.Read and ReadByte: the next byte received, or -1; never waits
//   ChipSharp_Runtime_UartAvailable    Uart.Available: how many bytes wait in the port's buffer
//   ChipSharp_Runtime_UartWatch, ...   Uart.Watch and Unwatch: the uart event of [Interrupt(Event.Uart)] methods
//   ChipSharp_Runtime_ConsoleRead      Console.Read (and ReadLine, which reads with it): the next char of UART0's
//                                      input, UTF-8 decoded as .NET decodes it; waits for it
//
// Receiving: from Open on, each port's receive interrupt (UART0_IRQ is IRQ 20, UART1_IRQ IRQ 21; on 16 bytes in the
// FIFO, or when the line has been quiet for 32 bits' time) moves the bytes from the FIFO into the port's buffer of
// BUFFER_SIZE bytes, so bytes are not lost while the program is busy; when the buffer is full, the bytes that come
// after are dropped. The handlers are the runtime's own, needed only by programs that receive, so they are not in the
// vector table in flash (which names every handler a program might have, so the linker would keep them, and their
// buffers, in every program): the first Open copies the table into RAM, points VTOR at the copy and puts them there,
// as pico-sdk's irq_set_exclusive_handler does. The program's own handlers are in the copy too.
//
// The uart event: after the bytes are in the buffer, a port that Uart.Watch watches makes IRQ 26, a spare interrupt
// with no peripheral of its own (pico-sdk's first "user IRQ"), pending; the target's "interrupts" table gives the
// program's [Interrupt(Event.Uart)] method that interrupt. It has the same priority, so it runs right after.
//
// A port that does not exist, a pin it cannot use, a speed it cannot run at, or a port used before Open stop the
// program (chipsharp_hardware_failure, rp2040_failures.c) with a message that says what to do.

#include "chipsharp_hardware.h"

#define REG(address) (*(volatile unsigned int *)(address))

// Every peripheral register has aliases that change only some bits in one write (datasheet 2.1.2).
#define ALIAS_SET 0x2000u
#define ALIAS_CLEAR 0x3000u

#define CLK_PERI_HZ 125000000u

#define RESETS_RESET 0x4000c000u
#define RESETS_RESET_DONE 0x4000c008u
#define RESET_UART0 (1u << 22)

#define IO_BANK0_GPIO_CTRL(pin) (0x40014004u + 8u * (unsigned int)(pin))
#define PADS_BANK0_GPIO(pin) (0x4001c004u + 4u * (unsigned int)(pin))
#define PAD_INPUT_ENABLE (1u << 6)
#define PAD_OUTPUT_DISABLE (1u << 7)
#define FUNCTION_UART 2u
#define FUNCTION_NONE 0x1fu
#define PIN_COUNT 30

#define UART_BASE(port) (0x40034000u + 0x4000u * (unsigned int)(port))
#define UART_DR 0x00u
#define UART_FR 0x18u
#define UART_IBRD 0x24u
#define UART_FBRD 0x28u
#define UART_LCR_H 0x2cu
#define UART_CR 0x30u
#define UART_IMSC 0x38u
#define UART_ICR 0x44u
#define UART_FR_BUSY (1u << 3)
#define UART_FR_RXFE (1u << 4)
#define UART_FR_TXFF (1u << 5)
#define UART_LCR_H_8_BITS (3u << 5)
#define UART_LCR_H_FIFO_ENABLE (1u << 4)
#define UART_CR_ENABLE ((1u << 0) | (1u << 8) | (1u << 9)) // UARTEN, TXE, RXE
#define UART_RECEIVE_INTERRUPTS ((1u << 4) | (1u << 6))  // RXIM (the FIFO is half full), RTIM (the line is quiet)
#define UART_IRQ(port) (20u + (unsigned int)(port))
#define PORT_COUNT 2

// The speeds the divisor can make from clk_peri: 16 * 65535 clocks a bit (with the fraction) down to 16.
#define BAUD_MIN 120
#define BAUD_MAX 7812500

// The uart event's interrupt: rp2040.json's "interrupts.uart.irq".
#define UART_EVENT_IRQ 26u

#define NVIC_ISER 0xe000e100u
#define NVIC_ISPR 0xe000e200u
#define VTOR 0xe000ed08u
#define VECTOR_COUNT 48u // 16 for the core, 32 interrupts

// Bytes received and not read yet, per port; a power of two.
#define BUFFER_SIZE 256u

__attribute__((noreturn)) void chipsharp_hardware_failure(const char *text, int n0, int n1, int n2, int n3);

// In rp2040_runtime.c: set once UART0 is set up, so that the console keeps the pins and speed Open gave it.
extern unsigned int chipsharp_uart0_started;

typedef void (*vector)(void);

struct port
{
    // Bytes received, counted since Open: the interrupt adds at head, the program takes at tail, each wrapping round
    // the buffer; head - tail are waiting.
    volatile unsigned int head;
    volatile unsigned int tail;
    volatile unsigned int watched;
    unsigned int open;
    int tx; // the pins, while it is open
    int rx;
    unsigned char bytes[BUFFER_SIZE];
};

static struct port ports[PORT_COUNT];

// The vector table, in RAM, once the first port is open; aligned as VTOR needs (its bits 31-8).
static vector vectors_in_ram[VECTOR_COUNT] __attribute__((aligned(256)));

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

// ---- Receiving ---------------------------------------------------------------------------------------------------

static void receive(int index)
{
    struct port *port = &ports[index];
    unsigned int base = UART_BASE(index);
    while ((REG(base + UART_FR) & UART_FR_RXFE) == 0)
    {
        unsigned int value = REG(base + UART_DR) & 0xffu; // (bits 8-11 say what went wrong with it, if anything)
        if (port->head - port->tail < BUFFER_SIZE)
        {
            port->bytes[port->head % BUFFER_SIZE] = (unsigned char)value;
            port->head++;
        }
    }

    REG(base + UART_ICR) = UART_RECEIVE_INTERRUPTS; // write 1 to clear (an empty FIFO clears them too)
    if (port->watched)
    {
        REG(NVIC_ISPR) = 1u << UART_EVENT_IRQ;
    }
}

static void received_on_uart0(void)
{
    receive(0);
}

static void received_on_uart1(void)
{
    receive(1);
}

// Makes handler the one of interrupt irq, in the vector table in RAM (copied from the one VTOR points at, the first
// time). Interrupts are off.
static void handle(unsigned int irq, vector handler)
{
    if (REG(VTOR) != (unsigned int)vectors_in_ram)
    {
        const vector *table = (const vector *)REG(VTOR);
        for (unsigned int i = 0; i < VECTOR_COUNT; i++)
        {
            vectors_in_ram[i] = table[i];
        }

        REG(VTOR) = (unsigned int)vectors_in_ram;
        __asm__ volatile("dsb" ::: "memory");
    }

    vectors_in_ram[16u + irq] = handler;
}

// ---- Open --------------------------------------------------------------------------------------------------------

static void select_function(int pin, unsigned int function)
{
    unsigned int pad = REG(PADS_BANK0_GPIO(pin));
    REG(PADS_BANK0_GPIO(pin)) = (pad & ~PAD_OUTPUT_DISABLE) | PAD_INPUT_ENABLE;
    REG(IO_BANK0_GPIO_CTRL(pin)) = function;
}

// The UART a pin can be the TX (0) or RX (1) of, its function 2 (datasheet 2.19.2): GPIO 0-3 are UART0's TX, RX, CTS
// and RTS, GPIO 4-7 UART1's, 8-11 UART1's, 12-15 UART0's, and so on in fours (16-19 UART0, 20-23 and 24-27 UART1,
// 28-29 UART0).
static int can_be(int pin, int port, unsigned int role)
{
    return pin >= 0 && pin < PIN_COUNT && ((unsigned int)pin & 3u) == role
        && (int)((((unsigned int)pin + 4u) >> 3) & 1u) == port;
}

static void check_pin(int pin, int port, unsigned int role)
{
    if (can_be(pin, port, role))
    {
        return;
    }

    if (role == 0)
    {
        chipsharp_hardware_failure(port == 0
            ? "System.ArgumentException: GPIO {0} cannot be UART0's TX: on the RP2040 it is GPIO 0, 12, 16 or 28. "
              "(Parameter 'tx')"
            : "System.ArgumentException: GPIO {0} cannot be UART1's TX: on the RP2040 it is GPIO 4, 8, 20 or 24. "
              "(Parameter 'tx')", pin, 0, 0, 0);
    }

    chipsharp_hardware_failure(port == 0
        ? "System.ArgumentException: GPIO {0} cannot be UART0's RX: on the RP2040 it is GPIO 1, 13, 17 or 29. "
          "(Parameter 'rx')"
        : "System.ArgumentException: GPIO {0} cannot be UART1's RX: on the RP2040 it is GPIO 5, 9, 21 or 25. "
          "(Parameter 'rx')", pin, 0, 0, 0);
}

static void check_port(int port)
{
    if (port < 0 || port >= PORT_COUNT)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The RP2040 has UART0 and UART1; there is no "
            "UART{0}. (Parameter 'port')", port, 0, 0, 0);
    }
}

void ChipSharp_Runtime_UartOpen(int port, int tx, int rx, int baud)
{
    check_port(port);
    check_pin(tx, port, 0);
    check_pin(rx, port, 1);
    if (baud < BAUD_MIN || baud > BAUD_MAX)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: UART{0} cannot run at {1} baud: the RP2040's "
            "UARTs run at 120 to 7812500 baud. (Parameter 'baud')", port, baud, 0, 0);
    }

    struct port *state = &ports[port];
    unsigned int base = UART_BASE(port);
    unsigned int reset = RESET_UART0 << port;

    // what the port is still sending goes out first, at the old speed; then the port starts afresh
    if ((REG(RESETS_RESET) & reset) == 0 && (REG(base + UART_CR) & 1u) != 0)
    {
        while (REG(base + UART_FR) & UART_FR_BUSY)
        {
        }
    }

    // the pins it was open on before, or the console's, are no longer the port's
    unsigned int primask = interrupts_off();
    if (state->open || (port == 0 && chipsharp_uart0_started))
    {
        REG(IO_BANK0_GPIO_CTRL(state->open ? state->tx : 0)) = FUNCTION_NONE;
        REG(IO_BANK0_GPIO_CTRL(state->open ? state->rx : 1)) = FUNCTION_NONE;
    }

    REG(RESETS_RESET + ALIAS_SET) = reset;
    REG(RESETS_RESET + ALIAS_CLEAR) = reset;
    while ((REG(RESETS_RESET_DONE) & reset) == 0)
    {
    }

    // the divisor in 1/128ths, rounded as pico-sdk's uart_set_baudrate does
    unsigned int divisor = 8u * CLK_PERI_HZ / (unsigned int)baud + 1u;
    REG(base + UART_IBRD) = divisor >> 7;
    REG(base + UART_FBRD) = (divisor & 0x7fu) >> 1;
    REG(base + UART_LCR_H) = UART_LCR_H_8_BITS | UART_LCR_H_FIFO_ENABLE; // also latches the divisors
    REG(base + UART_CR) = UART_CR_ENABLE;
    select_function(tx, FUNCTION_UART);
    select_function(rx, FUNCTION_UART);
    state->open = 1;
    state->tx = tx;
    state->rx = rx;
    state->head = 0;
    state->tail = 0;

    handle(UART_IRQ(port), port == 0 ? received_on_uart0 : received_on_uart1);
    REG(base + UART_IMSC) = UART_RECEIVE_INTERRUPTS;
    REG(NVIC_ISER) = 1u << UART_IRQ(port);
    if (port == 0)
    {
        chipsharp_uart0_started = 1;
    }

    interrupts_back(primask);
}

static struct port *open_port(int port)
{
    check_port(port);
    if (!ports[port].open)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: UART{0} is not open: call Uart.Open({0}, tx, rx, "
            "baud) first.", port, 0, 0, 0);
    }

    return &ports[port];
}

// ---- Sending and reading -----------------------------------------------------------------------------------------

void ChipSharp_Runtime_UartWriteByte(int port, int value)
{
    open_port(port);
    unsigned int base = UART_BASE(port);
    while (REG(base + UART_FR) & UART_FR_TXFF)
    {
    }

    REG(base + UART_DR) = (unsigned int)value & 0xffu;
}

// The next byte in the buffer, or -1. Interrupts are off for the moment it takes, as the program's main code and its
// [Interrupt(Event.Uart)] method may both read.
static int take(struct port *port)
{
    unsigned int primask = interrupts_off();
    int value = -1;
    if (port->head != port->tail)
    {
        value = port->bytes[port->tail % BUFFER_SIZE];
        port->tail++;
    }

    interrupts_back(primask);
    return value;
}

int ChipSharp_Runtime_UartReadByte(int port)
{
    return take(open_port(port));
}

int ChipSharp_Runtime_UartAvailable(int port)
{
    struct port *state = open_port(port);
    return (int)(state->head - state->tail);
}

// ---- The uart event ----------------------------------------------------------------------------------------------

void ChipSharp_Runtime_UartWatch(int port)
{
    struct port *state = open_port(port);
    unsigned int primask = interrupts_off();
    state->watched = 1;
    REG(NVIC_ISER) = 1u << UART_EVENT_IRQ;
    if (state->head != state->tail)
    {
        REG(NVIC_ISPR) = 1u << UART_EVENT_IRQ; // bytes are waiting: the method runs at once
    }

    interrupts_back(primask);
}

void ChipSharp_Runtime_UartUnwatch(int port)
{
    open_port(port)->watched = 0;
}

// ---- Console.Read ------------------------------------------------------------------------------------------------

// A byte read too far (not part of the char before it), which the next char starts with; -1 for none.
static int console_next = -1;

// The second half (a low surrogate) of a char above U+FFFF, which the next Console.Read returns; 0 for none.
static unsigned int console_low_surrogate;

// The next byte of UART0's input, waiting for it (asleep until an interrupt comes, as one does when a byte arrives).
static unsigned int console_byte(void)
{
    if (console_next >= 0)
    {
        unsigned int value = (unsigned int)console_next;
        console_next = -1;
        return value;
    }

    for (;;)
    {
        int value = take(&ports[0]);
        if (value >= 0)
        {
            return (unsigned int)value;
        }

        // an interrupt's return sets the event register, so a byte that arrives before this WFE wakes it at once
        __asm__ volatile("wfe");
    }
}

// Console.Read: the next char of the console's input, as .NET reads UTF-8: a char of one to three bytes, or the two
// halves of one of four bytes (a surrogate pair), and U+FFFD for each byte that is not part of a well-formed one. On
// the first call, UART0 starts receiving where the console sends: on GPIO 0 and 1, at 115200 baud (unless the program
// opened it with Uart.Open). It never ends: a serial line has no end of input.
int ChipSharp_Runtime_ConsoleRead(void)
{
    if (!ports[0].open)
    {
        ChipSharp_Runtime_UartOpen(0, 0, 1, 115200);
    }

    if (console_low_surrogate != 0)
    {
        unsigned int low = console_low_surrogate;
        console_low_surrogate = 0;
        return (int)low;
    }

    unsigned int first = console_byte();
    unsigned int count;
    unsigned int value;
    unsigned int low = 0x80u; // the range the next byte must be in (the second one's is narrower after some firsts)
    unsigned int high = 0xbfu;
    if (first < 0x80u)
    {
        return (int)first;
    }
    else if (first >= 0xc2u && first <= 0xdfu)
    {
        count = 1;
        value = first & 0x1fu;
    }
    else if (first >= 0xe0u && first <= 0xefu)
    {
        count = 2;
        value = first & 0x0fu;
        low = first == 0xe0u ? 0xa0u : low;   // not an overlong form
        high = first == 0xedu ? 0x9fu : high; // not a surrogate
    }
    else if (first >= 0xf0u && first <= 0xf4u)
    {
        count = 3;
        value = first & 0x07u;
        low = first == 0xf0u ? 0x90u : low;   // not an overlong form
        high = first == 0xf4u ? 0x8fu : high; // not above U+10FFFF
    }
    else
    {
        return 0xfffd;
    }

    for (unsigned int i = 0; i < count; i++)
    {
        unsigned int next = console_byte();
        if (next < low || next > high)
        {
            console_next = (int)next;
            return 0xfffd;
        }

        value = (value << 6) | (next & 0x3fu);
        low = 0x80u;
        high = 0xbfu;
    }

    if (value >= 0x10000u)
    {
        value -= 0x10000u;
        console_low_surrogate = 0xdc00u | (value & 0x3ffu);
        return (int)(0xd800u | (value >> 10));
    }

    return (int)value;
}
