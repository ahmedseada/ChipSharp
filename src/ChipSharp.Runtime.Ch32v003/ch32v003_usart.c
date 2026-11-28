// ChipSharp runtime for the WCH CH32V003: its serial port USART1, TX on PD5 (pin 29), RX on PD6 (pin 30), the pins of
// its default mapping (AFIO_PCFR1's USART1_RM 0: DS Table 2-2), as the console (Console.Write and WriteLine, the failure
// messages, Console.Read), ChipSharp.Hardware.Uart's port 0 and the uart event. On WCH's evaluation board a WCH-LinkE's
// RX on PD5 (and TX on PD6) brings it to the LinkE's own USB serial port.
//
// 8 data bits, no parity, one stop bit (CTLR1's M and PCE 0, CTLR2's STOP 00), at HCLK / BRR: BRR is USARTDIV in
// sixteenths, 48 MHz / 16 / USARTDIV the speed (RM 12), so BRR = 48 MHz / baud, rounded to the nearest: 733 to 3000000
// baud (BRR from 65535 down to 16). 115200 baud is 417, 115108 baud, 0.08% slow; 9600 is 5000, exact.
//
//   chipsharp_console_write            Console.Write's bytes (chipsharp_console.c makes them), sent before it returns:
//   chipsharp_failure_write            the failures' messages too; USART1 starts at 115200 baud on the first, unless
//                                      Uart.Open set it up, whose speed the console then keeps
//   ChipSharp_Runtime_UartOpen         Uart.Open(0, 29, 30, baud): the port at a speed; it receives from then on
//   ChipSharp_Runtime_UartWriteByte    Uart.Write and WriteByte: one byte, once the port has room
//   ChipSharp_Runtime_UartReadByte     Uart.Read and ReadByte: the next byte received, or -1; never waits
//   ChipSharp_Runtime_UartAvailable    Uart.Available: how many bytes wait in the buffer
//   ChipSharp_Runtime_UartWatch, ...   Uart.Watch and Unwatch: the uart event, the program's [Interrupt(Event.Uart)]
//                                      method's entry chipsharp_isr_irq40 (ch32v003.json's "interrupts.uart.irq")
//   ChipSharp_Runtime_ConsoleRead      Console.Read: the next char of USART1's input, UTF-8 decoded as .NET decodes it;
//                                      waits for it, the core asleep (wfi) until an interrupt comes
//
// Receiving: from Open (or the first Console.Read, which starts receiving at the console's speed), USART1's interrupt
// (vector 32: RXNE with RXNEIE) moves each byte into a buffer of BUFFER_SIZE bytes, so that bytes are not lost while
// the program is busy (USART1 itself holds one); when it is full, the bytes that come after are dropped. 16 bytes, 1.4
// ms of input at 115200 baud, of the chip's 2 KB of RAM; a program that sends only links none of it. After the bytes
// are in, a watched port runs the uart event's method, in the same interrupt.
//
// The port's registers are used only once its clock is on (RCC_APB2PCENR.USART1EN, set when it starts): before, they
// would read nothing.

#include "chipsharp_hardware.h"
#include "ch32v003_chip.h"

#define HCLK_HZ 48000000u
#define BAUD_MIN 733
#define BAUD_MAX 3000000
#define BRR_115200 417u

#define TX_PIN CH32V003_USART1_TX_PIN
#define RX_PIN CH32V003_USART1_RX_PIN

// Bytes received and not read yet; a power of two that divides 256, as head and tail count in bytes.
#define BUFFER_SIZE 16u

// Bytes received, counted since receiving started: the interrupt adds at head, the program takes at tail, each wrapping
// round the buffer; head - tail are waiting.
static volatile u8 head;
static volatile u8 tail;
static volatile u8 watched;
static u8 bytes[BUFFER_SIZE];

// ---- Starting and sending ------------------------------------------------------------------------------------------

// True once the port receives (Uart.Open or Console.Read).
static u32 receiving(void)
{
    return CH32_USART_STARTED() && (USART1_CTLR1 & USART1_CTLR1_RE_MASK) != 0;
}

// USART1 at brr, sending on PD5 (an alternate-function push-pull output, as USART1's TX must be to leave the chip), and
// receiving on PD6 (an input with its pull-up, the line's idle level when nothing is wired to it) with its interrupt
// when receive is set. What it was still sending goes out first, at the speed it had.
static void start(u32 brr, u32 receive)
{
    if (CH32_USART_STARTED())
    {
        while ((USART1_STATR & USART1_STATR_TC_MASK) == 0)
        {
        }
    }
    else
    {
        u32 enabled = ch32_interrupts_off();
        RCC_APB2PCENR |= RCC_APB2PCENR_USART1EN_MASK;
        ch32_interrupts_back(enabled);
    }

    USART1_CTLR2 = 0;
    USART1_CTLR3 = 0;
    USART1_BRR = brr;
    USART1_CTLR1 = USART1_CTLR1_UE_MASK | USART1_CTLR1_TE_MASK
        | (receive != 0 ? USART1_CTLR1_RE_MASK | USART1_CTLR1_RXNEIE_MASK : 0u);

    u32 shift = 4u * (TX_PIN & 7u);
    u32 enabled = ch32_interrupts_off();
    u32 config = (CH32_CFGLR(TX_PIN) & ~(0xfu << shift)) | (CH32_CFG_ALTERNATE << shift);
    if (receive != 0)
    {
        CH32_BSHR(RX_PIN) = 1u << (RX_PIN & 7u);
        shift = 4u * (RX_PIN & 7u);
        config = (config & ~(0xfu << shift)) | (CH32_CFG_PULL << shift);
    }

    CH32_CFGLR(TX_PIN) = config; // the same port, D
    ch32_interrupts_back(enabled);
    if (receive != 0)
    {
        ch32_enable(CH32V003_IRQ_USART1);
    }
}

// count bytes, once the port has room for each: at 115200 baud from the first unless it was set up.
static void send(const char *text, chipsharp_count count)
{
    if (!CH32_USART_STARTED())
    {
        start(BRR_115200, 0);
    }

    for (chipsharp_count i = 0; i < count; i++)
    {
        while ((USART1_STATR & USART1_STATR_TXE_MASK) == 0)
        {
        }

        USART1_DATAR = (u8)text[i];
    }
}

void chipsharp_console_write(const char *text, chipsharp_count count)
{
    send(text, count);
}

void chipsharp_failure_write(const char *text, chipsharp_count count)
{
    send(text, count);
}

// ---- Receiving -----------------------------------------------------------------------------------------------------

// USART1's hook: a byte is in (RXNE; after an overrun, ORE too, the byte that came first). Reading STATR, then DATAR,
// clears both. A byte with a framing error is kept as it came, as on the Pico. A wait (Console.Read) is woken.
static void received(void)
{
    while ((USART1_STATR & (USART1_STATR_RXNE_MASK | USART1_STATR_ORE_MASK)) != 0)
    {
        u8 value = (u8)USART1_DATAR;
        if ((u8)(head - tail) < BUFFER_SIZE)
        {
            bytes[head % BUFFER_SIZE] = value;
            head++;
        }
    }

    ch32_wake();
    if (watched && head != tail)
    {
        UART_EVENT();
    }
}

// USART1's interrupt (vector 32): to the hook the receiving put in; one that comes without it (it cannot) reads the
// byte, so that it does not come again and again.
void chipsharp_isr_irq32(void)
{
    void (*hook)(void) = ch32_usart_hook;
    if (hook != 0)
    {
        hook();
    }
    else
    {
        (void)USART1_STATR;
        (void)USART1_DATAR;
    }
}

// The entry of the program's [Interrupt(Event.Uart)] method; this empty one when it has none.
__attribute__((weak)) void UART_EVENT(void)
{
}

// Receiving at brr, into an empty buffer.
static void start_receiving(u32 brr)
{
    u32 enabled = ch32_interrupts_off();
    ch32_usart_hook = received;
    head = 0;
    tail = 0;
    ch32_interrupts_back(enabled);
    start(brr, 1);
}

// ---- Uart ------------------------------------------------------------------------------------------------------------

static void check_port(chipsharp_i32 port)
{
    if (port != 0)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The CH32V003 has one serial port, 0 (its USART1, "
            "on PD5 and PD6); there is no port {0}. (Parameter 'port')", port, 0, 0, 0);
    }
}

void ChipSharp_Runtime_UartOpen(chipsharp_i32 port, chipsharp_i32 tx, chipsharp_i32 rx, chipsharp_i32 baud)
{
    check_port(port);
    if (tx != TX_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: Pin {0} cannot be serial port 0's TX: on the CH32V003 it "
            "is pin 29 (PD5). (Parameter 'tx')", tx, 0, 0, 0);
    }

    if (rx != RX_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: Pin {0} cannot be serial port 0's RX: on the CH32V003 it "
            "is pin 30 (PD6). (Parameter 'rx')", rx, 0, 0, 0);
    }

    if (baud < BAUD_MIN || baud > BAUD_MAX)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: Serial port 0 cannot run at {0} baud: the "
            "CH32V003 at 48 MHz runs USART1 at 733 to 3000000 baud. (Parameter 'baud')", baud, 0, 0, 0);
    }

    start_receiving((HCLK_HZ + (u32)baud / 2u) / (u32)baud);
}

static void check_open(chipsharp_i32 port)
{
    check_port(port);
    if (!receiving())
    {
        chipsharp_hardware_failure("System.InvalidOperationException: Serial port {0} is not open: call Uart.Open({0}, "
            "29, 30, baud) first.", port, 0, 0, 0);
    }
}

void ChipSharp_Runtime_UartWriteByte(chipsharp_i32 port, chipsharp_i32 value)
{
    check_open(port);
    char byte = (char)value;
    send(&byte, 1);
}

// The next byte in the buffer, or -1. Interrupts are off for the moment it takes, as the program's main code and its
// [Interrupt(Event.Uart)] method may both read.
static chipsharp_i32 take(void)
{
    u32 enabled = ch32_interrupts_off();
    chipsharp_i32 value = -1;
    if (head != tail)
    {
        value = bytes[tail % BUFFER_SIZE];
        tail++;
    }

    ch32_interrupts_back(enabled);
    return value;
}

chipsharp_i32 ChipSharp_Runtime_UartReadByte(chipsharp_i32 port)
{
    check_open(port);
    return take();
}

chipsharp_i32 ChipSharp_Runtime_UartAvailable(chipsharp_i32 port)
{
    check_open(port);
    return (u8)(head - tail);
}

// ---- The uart event --------------------------------------------------------------------------------------------------

// Bytes already waiting run the method at once: USART1's interrupt is made pending, as if a byte had come.
void ChipSharp_Runtime_UartWatch(chipsharp_i32 port)
{
    check_open(port);
    u32 enabled = ch32_interrupts_off();
    watched = 1;
    if (head != tail)
    {
        ch32_pend(CH32V003_IRQ_USART1);
    }

    ch32_interrupts_back(enabled);
}

void ChipSharp_Runtime_UartUnwatch(chipsharp_i32 port)
{
    check_open(port);
    watched = 0;
}

// ---- Console.Read ----------------------------------------------------------------------------------------------------

// A byte read too far (not part of the char before it), which the next char starts with: 0x100 | the byte; 0 for none.
static unsigned short console_next;

// The second half (a low surrogate) of a char above U+FFFF, which the next Console.Read returns; 0 for none.
static unsigned short console_low_surrogate;

// The next byte of USART1's input, waiting for it: asleep (wfi) until an interrupt comes, as one does when a byte
// arrives (ch32_sleep: one that comes between the look at the buffer and the wfi wakes it at once).
static u8 console_byte(void)
{
    if (console_next != 0)
    {
        u8 value = (u8)console_next;
        console_next = 0;
        return value;
    }

    for (;;)
    {
        ch32_woken = 0;
        chipsharp_i32 value = take();
        if (value >= 0)
        {
            return (u8)value;
        }

        ch32_sleep(&ch32_woken);
    }
}

// Console.Read: the next char of the console's input, as .NET reads UTF-8: a char of one to three bytes, or the two
// halves of one of four bytes (a surrogate pair), and U+FFFD for each byte that is not part of a well-formed one. On the
// first call, USART1 starts receiving on PD6 at the console's speed (115200 baud, unless Uart.Open set another). It
// never ends: a serial line has no end of input.
chipsharp_i32 ChipSharp_Runtime_ConsoleRead(void)
{
    if (!receiving())
    {
        start_receiving(CH32_USART_STARTED() ? USART1_BRR : BRR_115200);
    }

    if (console_low_surrogate != 0)
    {
        unsigned short low = console_low_surrogate;
        console_low_surrogate = 0;
        return low;
    }

    u8 first = console_byte();
    u32 count;
    u32 value;
    u8 low = 0x80u; // the range the next byte must be in (the second one's is narrower after some firsts)
    u8 high = 0xbfu;
    if (first < 0x80u)
    {
        return first;
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

    for (u32 i = 0; i < count; i++)
    {
        u8 next = console_byte();
        if (next < low || next > high)
        {
            console_next = (unsigned short)(0x100u | next);
            return 0xfffd;
        }

        value = (value << 6) | (next & 0x3fu);
        low = 0x80u;
        high = 0xbfu;
    }

    if (value >= 0x10000u)
    {
        value -= 0x10000u;
        console_low_surrogate = (unsigned short)(0xdc00u | (value & 0x3ffu));
        return (chipsharp_i32)(0xd800u | (value >> 10));
    }

    return (chipsharp_i32)value;
}
