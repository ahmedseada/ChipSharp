// ChipSharp runtime for the Espressif ESP32-C5: the serial port UART0 (ChipSharp.Hardware.Uart), the devkit's "USB Type-C
// to UART Port" (a USB-to-UART bridge on GPIO11 TX and GPIO12 RX, through which esptool flashes the board).
//
// Freestanding, like esp32c5_runtime.c. 8 data bits, no parity, one stop bit; the FIFOs (128 bytes each way). The
// console is not this port: it is the USB Serial/JTAG controller (esp32c5_usb.c), so a program has both.
//
//   ChipSharp_Runtime_UartOpen         Uart.Open: port 0 on GPIO11 and GPIO12 (the pins of its IO MUX function 0: the only
//                                      ones; the GPIO matrix could put it on others, which this runtime does not), at a
//                                      speed; it receives from then on
//   ChipSharp_Runtime_UartWriteByte    Uart.Write and WriteByte: one byte, once the send FIFO has room
//   ChipSharp_Runtime_UartReadByte     Uart.Read and ReadByte: the next byte received, or -1; never waits
//   ChipSharp_Runtime_UartAvailable    Uart.Available: how many bytes wait in the port's buffer
//   ChipSharp_Runtime_UartWatch, ...   Uart.Watch and Unwatch: the uart event of [Interrupt(Event.Uart)] methods
//
// The speed. The function clock is the crystal (PCR_UART0_SCLK_CONF, 48 MHz), over an integer (the SCLK divider, 1 to
// 256) chosen so that the baud divider, 12 bits and a fraction of sixteenths, fits: 100 to 3000000 baud (a divider of 16,
// the receiver samples 16 times a bit). The speed is that divider rounded to a sixteenth: 115200 baud is 416 and 11/16,
// 0.01 % off.
//
// Receiving: from Open on, UART0's interrupt (the source UART0, routed to CLIC id 20) moves the bytes from the FIFO into
// the port's buffer of BUFFER_SIZE bytes, so bytes are not lost while the program is busy (on 16 bytes in the FIFO, or
// when the line has been quiet for 10 bits' time); when the buffer is full, the bytes that come after are dropped.
//
// The uart event: while a program watches the port (Uart.Watch), the same source is routed to CLIC id 19 instead, the
// program's [Interrupt(Event.Uart)] method's, whose clear function (chipsharp_clear_uart_event, the target's
// "interrupts.uart.clear") first does what id 20's handler does, so the method finds the bytes in the buffer. Bytes that
// were already there when Watch was called run it at once: the software interrupt FROM_CPU_INTR0 (INTPRI), routed to id
// 19 too, which the clear function ends.
//
// A port that does not exist, pins it cannot use, a speed it cannot run at, or a port used before Open stop the program
// (chipsharp_hardware_failure, esp32c5_failures.c) with a message that says what to do.

#include "chipsharp_hardware.h"
#include "esp32c5_chip.h"

#define PORT_COUNT 1
#define TX_PIN ESP32C5_U0TXD_PIN
#define RX_PIN ESP32C5_U0RXD_PIN

// UART0's function clock (the crystal) and the speeds it can make
// (the crystal's frequency is read when the port opens: PCR_SYSCLK_CONF.CLK_XTAL_FREQ, in MHz; 48 on the devkit)
#define BAUD_MIN 100
#define BAUD_MAX 3000000
#define MAX_BAUD_DIVIDER 4095u

// The interrupts: 16 bytes in the FIFO, or 10 bits of quiet with bytes in it
#define RECEIVE_THRESHOLD 16u
#define RECEIVE_TIMEOUT_BITS 10u
#define RECEIVE_INTERRUPTS (UART0_INT_RAW_RXFIFO_FULL_INT_RAW_MASK | UART0_INT_RAW_RXFIFO_TOUT_INT_RAW_MASK \
    | UART0_INT_RAW_RXFIFO_OVF_INT_RAW_MASK)

// CONF0: 8 data bits, 1 stop bit (its reset value), and the two FIFOs' resets
#define CONF0_8N1 (UART0_CONF0_SYNC_BIT_NUM(3) | UART0_CONF0_SYNC_STOP_BIT_NUM(1))
#define CONF0_RXFIFO_RST (1u << 22)
#define CONF0_TXFIFO_RST (1u << 23)

// Bytes received and not read yet; a power of two.
#define BUFFER_SIZE 256u

static struct
{
    // Bytes received, counted since Open: the interrupt adds at head, the program takes at tail, each wrapping round the
    // buffer; head - tail are waiting.
    volatile unsigned int head;
    volatile unsigned int tail;
    volatile unsigned int watched;
    unsigned int open;
    unsigned char bytes[BUFFER_SIZE];
} port0;

// ---- Receiving ---------------------------------------------------------------------------------------------------

// Takes what the FIFO holds into the buffer, and clears the interrupts. Interrupts are off (this is a handler, or called
// with them off).
static void receive(void)
{
    for (;;)
    {
        while ((UART0_STATUS & UART0_STATUS_RXFIFO_CNT_MASK) != 0)
        {
            unsigned int value = UART0_FIFO & 0xffu;
            if (port0.head - port0.tail < BUFFER_SIZE)
            {
                port0.bytes[port0.head % BUFFER_SIZE] = (unsigned char)value;
                port0.head++;
            }
        }

        // a clear while the FIFO still held enough raises the interrupt again at once: clear, then look once more
        UART0_INT_CLR = RECEIVE_INTERRUPTS;
        if ((UART0_STATUS & UART0_STATUS_RXFIFO_CNT_MASK) == 0)
        {
            return;
        }
    }
}

// CLIC id 20: UART0, while no program watches it
void chipsharp_isr_irq20(void)
{
    receive();
}

// The interrupt entry of [Interrupt(Event.Uart)] calls this before the method (the target's "interrupts.uart.clear").
void chipsharp_clear_uart_event(void)
{
    INTPRI_CPU_INTR_FROM_CPU_0 = 0;
    receive();
}

// ---- Open --------------------------------------------------------------------------------------------------------

static void check_port(chipsharp_i32 port)
{
    if (port < 0 || port >= PORT_COUNT)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The ESP32-C5 has one UART for programs, UART0; "
            "there is no UART{0}. (Parameter 'port')", port, 0, 0, 0);
    }
}

// Waits for UART0's register updates (the _SYNC registers take effect when REG_UPDATE is written, which clears itself).
static void update(void)
{
    UART0_REG_UPDATE = UART0_REG_UPDATE_REG_UPDATE_MASK;
    while ((UART0_REG_UPDATE & UART0_REG_UPDATE_REG_UPDATE_MASK) != 0)
    {
    }
}

// Connects the pad to UART0's own function (IO MUX function 0 of GPIO11 and GPIO12), with its input on.
static void select_uart(chipsharp_i32 pin)
{
    C5_IO_MUX(pin) = (C5_IO_MUX(pin) & ~IO_MUX_GPIO0_MCU_SEL_MASK) | IO_MUX_GPIO0_MCU_SEL(0) | IO_MUX_GPIO0_FUN_IE_MASK;
}

void ChipSharp_Runtime_UartOpen(chipsharp_i32 port, chipsharp_i32 tx, chipsharp_i32 rx, chipsharp_i32 baud)
{
    check_port(port);
    if (tx != TX_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: GPIO {0} cannot be UART0's TX: on the ESP32-C5 it is GPIO "
            "11. (Parameter 'tx')", tx, 0, 0, 0);
    }

    if (rx != RX_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: GPIO {0} cannot be UART0's RX: on the ESP32-C5 it is GPIO "
            "12. (Parameter 'rx')", rx, 0, 0, 0);
    }

    if (baud < BAUD_MIN || baud > BAUD_MAX)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: UART{0} cannot run at {1} baud: the ESP32-C5's "
            "UART runs at 100 to 3000000 baud. (Parameter 'baud')", port, baud, 0, 0);
    }

    // what the port is still sending goes out first, at the old speed; then the port starts afresh
    PCR_UART0_CONF |= PCR_UART0_CONF_UART0_CLK_EN_MASK;
    while ((UART0_STATUS & UART0_STATUS_TXFIFO_CNT_MASK) != 0 || (UART0_FSM_STATUS & UART0_FSM_STATUS_ST_UTX_OUT_MASK) != 0)
    {
    }

    // the speed: the crystal over a whole number, so that the baud divider (in sixteenths) fits its 12 bits
    unsigned int function_clock_hz = ((PCR_SYSCLK_CONF & PCR_SYSCLK_CONF_CLK_XTAL_FREQ_MASK) >> PCR_SYSCLK_CONF_CLK_XTAL_FREQ_POS) * 1000000u;
    if (function_clock_hz == 0)
    {
        function_clock_hz = 48000000u;
    }

    unsigned int sclk_divider = (function_clock_hz / (unsigned int)baud + MAX_BAUD_DIVIDER - 1u) / MAX_BAUD_DIVIDER;
    if (sclk_divider < 1u)
    {
        sclk_divider = 1u;
    }

    unsigned int sixteenths = (unsigned int)(((unsigned long long)function_clock_hz * 16u / sclk_divider
        + (unsigned int)baud / 2u) / (unsigned int)baud);
    PCR_UART0_SCLK_CONF = PCR_UART0_SCLK_CONF_UART0_SCLK_SEL(0) | PCR_UART0_SCLK_CONF_UART0_SCLK_EN_MASK
        | PCR_UART0_SCLK_CONF_UART0_SCLK_DIV_NUM(sclk_divider - 1u);
    UART0_CLKDIV_SYNC = UART0_CLKDIV_SYNC_CLKDIV(sixteenths >> 4) | UART0_CLKDIV_SYNC_CLKDIV_FRAG(sixteenths & 15u);
    UART0_CONF0_SYNC = CONF0_8N1 | CONF0_RXFIFO_RST | CONF0_TXFIFO_RST;
    update();
    UART0_CONF0_SYNC = CONF0_8N1;
    UART0_CONF1 = (UART0_CONF1 & ~UART0_CONF1_RXFIFO_FULL_THRHD_MASK) | UART0_CONF1_RXFIFO_FULL_THRHD(RECEIVE_THRESHOLD);
    UART0_TOUT_CONF_SYNC = UART0_TOUT_CONF_SYNC_RX_TOUT_EN_MASK | UART0_TOUT_CONF_SYNC_RX_TOUT_THRHD(RECEIVE_TIMEOUT_BITS);
    update();

    unsigned int enabled = c5_interrupts_off();
    select_uart(tx);
    select_uart(rx);
    port0.open = 1;
    port0.head = 0;
    port0.tail = 0;
    UART0_INT_CLR = 0xfffffu;
    UART0_INT_ENA = RECEIVE_INTERRUPTS;
    c5_route(ESP32C5_IRQ_UART0, port0.watched ? C5_ID_UART_EVENT : C5_ID_UART_RECEIVE);
    c5_clic_enable(C5_ID_UART_RECEIVE);
    c5_interrupts_back(enabled);
}

static void open_port(chipsharp_i32 port)
{
    check_port(port);
    if (!port0.open)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: UART{0} is not open: call Uart.Open({0}, tx, rx, "
            "baud) first.", port, 0, 0, 0);
    }
}

// ---- Sending and reading -----------------------------------------------------------------------------------------

void ChipSharp_Runtime_UartWriteByte(chipsharp_i32 port, chipsharp_i32 value)
{
    open_port(port);
    while (((UART0_STATUS & UART0_STATUS_TXFIFO_CNT_MASK) >> UART0_STATUS_TXFIFO_CNT_POS) >= 127u)
    {
    }

    UART0_FIFO = (unsigned int)value & 0xffu;
}

// The next byte in the buffer, or -1. Interrupts are off for the moment it takes, as the program's main code and its
// [Interrupt(Event.Uart)] method may both read.
chipsharp_i32 ChipSharp_Runtime_UartReadByte(chipsharp_i32 port)
{
    open_port(port);
    unsigned int enabled = c5_interrupts_off();
    chipsharp_i32 value = -1;
    if (port0.head != port0.tail)
    {
        value = port0.bytes[port0.tail % BUFFER_SIZE];
        port0.tail++;
    }

    c5_interrupts_back(enabled);
    return value;
}

chipsharp_i32 ChipSharp_Runtime_UartAvailable(chipsharp_i32 port)
{
    open_port(port);
    return (chipsharp_i32)(port0.head - port0.tail);
}

// ---- The uart event ----------------------------------------------------------------------------------------------

void ChipSharp_Runtime_UartWatch(chipsharp_i32 port)
{
    open_port(port);
    unsigned int enabled = c5_interrupts_off();
    port0.watched = 1;
    c5_route(ESP32C5_IRQ_UART0, C5_ID_UART_EVENT);
    c5_route(ESP32C5_IRQ_FROM_CPU_INTR0, C5_ID_UART_EVENT);
    c5_clic_enable(C5_ID_UART_EVENT);
    if (port0.head != port0.tail)
    {
        INTPRI_CPU_INTR_FROM_CPU_0 = 1; // bytes are waiting: the method runs at once
    }

    c5_interrupts_back(enabled);
}

void ChipSharp_Runtime_UartUnwatch(chipsharp_i32 port)
{
    open_port(port);
    unsigned int enabled = c5_interrupts_off();
    port0.watched = 0;
    c5_route(ESP32C5_IRQ_UART0, C5_ID_UART_RECEIVE);
    c5_route(ESP32C5_IRQ_FROM_CPU_INTR0, 0);
    INTPRI_CPU_INTR_FROM_CPU_0 = 0;
    c5_interrupts_back(enabled);
}
