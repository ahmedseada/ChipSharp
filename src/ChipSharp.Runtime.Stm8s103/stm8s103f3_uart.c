// ChipSharp runtime for the STM8S103F3: its serial port UART1 as ChipSharp.Hardware.Uart's port 0 (TX on PD5, pin 29; RX
// on PD6, pin 30), and the console's input (Console.Read, on the same port).
//
// stm8s103f3_console.c sets UART1 up (chipsharp_uart1_start) and sends. 8 data bits, no parity, one stop bit, at
// 16 MHz / the divider, the nearest: 245 to 1000000 baud.
//
//   ChipSharp_Runtime_UartOpen         Uart.Open(0, 29, 30, baud): the port at a speed; it receives from then on
//   ChipSharp_Runtime_UartWriteByte    Uart.Write and WriteByte: one byte, once the port has room
//   ChipSharp_Runtime_UartReadByte     Uart.Read and ReadByte: the next byte received, or -1; never waits
//   ChipSharp_Runtime_UartAvailable    Uart.Available: how many bytes wait in the buffer
//   ChipSharp_Runtime_UartWatch, ...   Uart.Watch and Unwatch: the uart event, the program's [Interrupt(Event.Uart)]
//                                      method's entry chipsharp_isr_irq32 (stm8s103f3.json's "interrupts.uart.irq")
//   ChipSharp_Runtime_ConsoleRead      Console.Read: the next char of UART1's input, UTF-8 decoded as .NET decodes it;
//                                      waits for it, the CPU asleep (wfi) until an interrupt comes
//
// Receiving: from Open (or the first Console.Read, which opens the port at 115200 baud unless the program did), UART1's
// receive interrupt moves each byte into a buffer of BUFFER_SIZE bytes, so that bytes are not lost while the program is
// busy (UART1 itself holds one); when it is full, the bytes that come after are dropped. 32 bytes, 2.8 ms of input at
// 115200 baud, of the chip's 1 KB of RAM. After the bytes are in, a watched port runs the uart event's method, in the
// same interrupt.

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

#define BAUD_MIN 245
#define BAUD_MAX 1000000

// Bytes received and not read yet; a power of two that divides 256, as head and tail count in bytes.
#define BUFFER_SIZE 32u

// Bytes received, counted since Open: the interrupt adds at head, the program takes at tail, each wrapping round the
// buffer; head - tail are waiting.
static volatile u8 head;
static volatile u8 tail;
static volatile u8 watched;
static u8 open;
static u8 bytes[BUFFER_SIZE];

static void check_port(chipsharp_i32 port)
{
    if (port != 0)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The STM8S103F3 has one serial port, 0 (its UART1, "
            "on PD5 and PD6); there is no port {0}. (Parameter 'port')", port, 0, 0, 0);
    }
}

// ---- Receiving -----------------------------------------------------------------------------------------------------

// UART1_RX's hook: a byte is in (RXNE; an overrun's byte, the one that came first, too). Reading SR, then DR, clears
// both. A byte with a framing error is kept as it came, as on the Pico.
static void received(void)
{
    while ((UART1_SR & (UART1_SR_RXNE_MASK | UART1_SR_OR_MASK)) != 0)
    {
        u8 value = UART1_DR;
        if ((u8)(head - tail) < BUFFER_SIZE)
        {
            bytes[head % BUFFER_SIZE] = value;
            head++;
        }
    }

    if (watched)
    {
        UART_EVENT();
    }
}

// Sets UART1 up at the divider's speed, receiving into an empty buffer.
static void start(u16 divider)
{
    u8 cc = chipsharp_interrupts_off();
    chipsharp_uart_hook = received;
    head = 0;
    tail = 0;
    open = 1;
    chipsharp_interrupts_back(cc);
    chipsharp_uart1_start(divider, 1);
}

void ChipSharp_Runtime_UartOpen(chipsharp_i32 port, chipsharp_i32 tx, chipsharp_i32 rx, chipsharp_i32 baud)
{
    check_port(port);
    if (tx != STM8S103F3_UART1_TX_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: Pin {0} cannot be serial port 0's TX: on the STM8S103F3 it "
            "is pin 29 (PD5). (Parameter 'tx')", tx, 0, 0, 0);
    }

    if (rx != STM8S103F3_UART1_RX_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: Pin {0} cannot be serial port 0's RX: on the STM8S103F3 it "
            "is pin 30 (PD6). (Parameter 'rx')", rx, 0, 0, 0);
    }

    if (baud < BAUD_MIN || baud > BAUD_MAX)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: Serial port 0 cannot run at {0} baud: the "
            "STM8S103F3 at 16 MHz runs UART1 at 245 to 1000000 baud. (Parameter 'baud')", baud, 0, 0, 0);
    }

    // 16 MHz / baud, rounded to the nearest: at most 65306 (245 baud), at least 16
    start((u16)((MASTER_HZ + (u32)baud / 2u) / (u32)baud));
}

static void check_open(chipsharp_i32 port)
{
    check_port(port);
    if (!open)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: Serial port {0} is not open: call Uart.Open({0}, "
            "29, 30, baud) first.", port, 0, 0, 0);
    }
}

// ---- Sending and reading ---------------------------------------------------------------------------------------------

void ChipSharp_Runtime_UartWriteByte(chipsharp_i32 port, chipsharp_i32 value)
{
    check_open(port);
    char byte = (char)value;
    chipsharp_uart1_write(&byte, 1);
}

// The next byte in the buffer, or -1. Interrupts are off for the moment it takes, as the program's main code and its
// [Interrupt(Event.Uart)] method may both read.
static chipsharp_i32 take(void)
{
    u8 cc = chipsharp_interrupts_off();
    chipsharp_i32 value = -1;
    if (head != tail)
    {
        value = bytes[tail % BUFFER_SIZE];
        tail++;
    }

    chipsharp_interrupts_back(cc);
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

// Bytes already waiting run the method at once, with interrupts off, as their interrupt would have.
void ChipSharp_Runtime_UartWatch(chipsharp_i32 port)
{
    check_open(port);
    u8 cc = chipsharp_interrupts_off();
    watched = 1;
    if (head != tail)
    {
        UART_EVENT();
    }

    chipsharp_interrupts_back(cc);
}

void ChipSharp_Runtime_UartUnwatch(chipsharp_i32 port)
{
    check_open(port);
    watched = 0;
}

// ---- Console.Read ----------------------------------------------------------------------------------------------------

// A byte read too far (not part of the char before it), which the next char starts with; -1 for none.
static i16 console_next = -1;

// The second half (a low surrogate) of a char above U+FFFF, which the next Console.Read returns; 0 for none.
static u16 console_low_surrogate;

// The next byte of UART1's input, waiting for it: asleep (wfi) until an interrupt comes, as one does when a byte arrives.
// wfi turns interrupts on as the CPU stops, so one that came between the look at the buffer and the wfi wakes it at once.
static u8 console_byte(void)
{
    if (console_next >= 0)
    {
        u8 value = (u8)console_next;
        console_next = -1;
        return value;
    }

    for (;;)
    {
        u8 cc = chipsharp_interrupts_off();
        if (head != tail)
        {
            u8 value = bytes[tail % BUFFER_SIZE];
            tail++;
            chipsharp_interrupts_back(cc);
            return value;
        }

        __asm__("wfi");
        chipsharp_interrupts_back(cc);
    }
}

// Console.Read: the next char of the console's input, as .NET reads UTF-8: a char of one to three bytes, or the two
// halves of one of four bytes (a surrogate pair), and U+FFFD for each byte that is not part of a well-formed one. On the
// first call, UART1 starts receiving on PD6 at 115200 baud (unless the program opened it with Uart.Open). It never ends:
// a serial line has no end of input.
chipsharp_i32 ChipSharp_Runtime_ConsoleRead(void)
{
    if (!open)
    {
        start(DIVIDER_115200);
    }

    if (console_low_surrogate != 0)
    {
        u16 low = console_low_surrogate;
        console_low_surrogate = 0;
        return low;
    }

    u8 first = console_byte();
    u8 count;
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

    for (u8 i = 0; i < count; i++)
    {
        u8 next = console_byte();
        if (next < low || next > high)
        {
            console_next = next;
            return 0xfffd;
        }

        value = (value << 6) | (next & 0x3fu);
        low = 0x80u;
        high = 0xbfu;
    }

    if (value >= 0x10000u)
    {
        value -= 0x10000u;
        console_low_surrogate = (u16)(0xdc00u | (value & 0x3ffu));
        return (chipsharp_i32)(0xd800u | (value >> 10));
    }

    return (chipsharp_i32)value;
}
