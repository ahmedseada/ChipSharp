// ChipSharp runtime for the ATmega328P: its serial port USART0 as ChipSharp.Hardware.Uart's port 0 (TX on D1, RX on D0:
// the Uno's USB serial port), and the console's input (Console.Read, on the same port).
//
// Freestanding, like atmega328p_runtime.c, which sets USART0 up (chipsharp_usart0_start) and sends. 8 data bits, no
// parity, one stop bit; the speed with U2X0, 16 MHz / (8 * (UBRR0 + 1)), the divisor rounded down as the Arduino core's
// Serial.begin rounds it: 489 to 2000000 baud.
//
//   ChipSharp_Runtime_UartOpen         Uart.Open(0, 1, 0, baud): the port at a speed; it receives from then on
//   ChipSharp_Runtime_UartWriteByte    Uart.Write and WriteByte: one byte, once the port has room
//   ChipSharp_Runtime_UartReadByte     Uart.Read and ReadByte: the next byte received, or -1; never waits
//   ChipSharp_Runtime_UartAvailable    Uart.Available: how many bytes wait in the buffer
//   ChipSharp_Runtime_UartWatch, ...   Uart.Watch and Unwatch: the uart event, the program's [Interrupt(Event.Uart)]
//                                      method's entry chipsharp_isr_irq28 (atmega328p.json's "interrupts.uart.irq")
//   ChipSharp_Runtime_ConsoleRead      Console.Read: the next char of USART0's input, UTF-8 decoded as .NET decodes it;
//                                      waits for it, asleep (idle) until an interrupt comes
//
// Receiving: from Open (or the first Console.Read, which opens the port at 115200 baud unless the program did), USART0's
// receive interrupt (USART_RX, __vector_18, atmega328p_interrupts.S) moves each byte into a buffer of BUFFER_SIZE bytes,
// so that bytes are not lost while the program is busy (USART0 itself holds two); when it is full, the bytes that come
// after are dropped. 64 bytes, as the Arduino core's: 5.5 ms of input at 115200 baud, of the chip's 2 KB of RAM, in a
// program that receives only. After the bytes are in, a watched port runs the uart event's method, in the same interrupt.
//
// A port the chip does not have, a pin it cannot use, a speed it cannot run at, or a port used before Open stop the
// program (as .NET's exceptions, on USART0 itself) with a message that says what to do.

#include "chipsharp_hardware.h"

typedef unsigned char u8;
typedef unsigned int u16;
typedef long i32;
typedef unsigned long u32;

#define REG(address) (*(volatile u8 *)(u16)(address))

#define SREG 0x5f
#define SREG_I 0x80u
#define SMCR 0x53
#define SMCR_IDLE 0x01u // SE, sleep mode idle: the CPU stops, the timers and USART0 go on

#define UCSR0A 0xc0
#define UCSR0A_RXC0 0x80u
#define UDR0 0xc6

#define UBRR_115200 16u

#define BAUD_MIN 489
#define BAUD_MAX 2000000

#define HOOK_RECEIVE 2

// Bytes received and not read yet; a power of two that divides 256, as head and tail count in bytes.
#define BUFFER_SIZE 64u

// In atmega328p_runtime.c.
extern void (*volatile chipsharp_interrupt_hooks[4])(u8 source);
extern u8 chipsharp_usart0_started;
void chipsharp_usart0_start(u16 divisor, u8 receive);
void chipsharp_usart0_write(const char *bytes, unsigned int count);

// In avr_runtime.c: stops the program as a failure (after chipsharp_before_halt, which sends what is still going out).
__attribute__((noreturn)) void chipsharp_halt_failure(void);

// The entry of the program's [Interrupt(Event.Uart)] method; this empty one when it has none.
__attribute__((weak)) void chipsharp_isr_irq28(void)
{
}

// Bytes received, counted since Open: the interrupt adds at head, the program takes at tail, each wrapping round the
// buffer; head - tail are waiting.
static volatile u8 head;
static volatile u8 tail;
static volatile u8 watched;
static u8 open;
static u8 bytes[BUFFER_SIZE];

static u8 interrupts_off(void)
{
    u8 sreg = REG(SREG);
    __asm__ volatile("cli" ::: "memory");
    return sreg;
}

static void interrupts_back(u8 sreg)
{
    __asm__ volatile("" ::: "memory");
    REG(SREG) = sreg;
}

// ---- Failures ----------------------------------------------------------------------------------------------------

typedef const __flash char flash_char;

// Text in flash, in a section of its own (LLVM puts every __flash constant of a file in one .progmem.data, which the
// linker would keep whole for any one of them).
#define TEXT_OF(name) #name
#define SECTION_NAME(number) ".progmem.data.chipsharp_uart" TEXT_OF(number)
#define FLASH(text)                                                                                                    \
    (__extension__({                                                                                                   \
        static const __flash char flash_text[] __attribute__((section(SECTION_NAME(__COUNTER__)))) = text;             \
        flash_text;                                                                                                    \
    }))

static void write_char(char c)
{
    chipsharp_usart0_write(&c, 1);
}

static void write_flash(flash_char *text)
{
    for (char c = *text; c != 0; c = *++text)
    {
        write_char(c);
    }
}

// "Unhandled exception. " and text, with number in place of its {0}, as .NET's exception would say it; then the stop.
__attribute__((noreturn)) static void fail(flash_char *text, i32 number)
{
    write_flash(FLASH("Unhandled exception. "));
    for (char c = *text; c != 0; c = *++text)
    {
        if (c == '{' && text[1] == '0' && text[2] == '}')
        {
            // the number as .NET writes it, a '-' first when negative, with no division
            static const __flash u32 powers[] = {1000000000u, 100000000u, 10000000u, 1000000u, 100000u, 10000u, 1000u,
                100u, 10u, 1u};
            u32 magnitude = number < 0 ? 0u - (u32)number : (u32)number;
            u8 started = 0;
            if (number < 0)
            {
                write_char('-');
            }

            for (u8 i = 0; i < 10; i++)
            {
                char digit = '0';
                while (magnitude >= powers[i])
                {
                    magnitude -= powers[i];
                    digit++;
                }

                if (digit != '0' || started || i == 9)
                {
                    write_char(digit);
                    started = 1;
                }
            }

            text += 2;
        }
        else
        {
            write_char(c);
        }
    }

    write_char('\r');
    write_char('\n');
    chipsharp_halt_failure();
}

static void check_port(i32 port)
{
    if (port != 0)
    {
        fail(FLASH("System.ArgumentOutOfRangeException: The ATmega328P has one serial port, UART0 (the Uno's D0 and "
            "D1); there is no UART{0}. (Parameter 'port')"), port);
    }
}

// ---- Receiving ---------------------------------------------------------------------------------------------------

// USART_RX: bytes are in. A byte with a framing error or one after an overrun is kept as it came, as on the Pico.
static void received(u8 source)
{
    (void)source;
    while ((REG(UCSR0A) & UCSR0A_RXC0) != 0)
    {
        u8 value = REG(UDR0);
        if ((u8)(head - tail) < BUFFER_SIZE)
        {
            bytes[head % BUFFER_SIZE] = value;
            head++;
        }
    }

    if (watched)
    {
        chipsharp_isr_irq28();
    }
}

// Sets USART0 up at the divisor's speed, receiving into an empty buffer.
static void start(u16 divisor)
{
    u8 sreg = interrupts_off();
    chipsharp_interrupt_hooks[HOOK_RECEIVE] = received;
    head = 0;
    tail = 0;
    open = 1;
    interrupts_back(sreg);
    chipsharp_usart0_start(divisor, 1);
}

void ChipSharp_Runtime_UartOpen(i32 port, i32 tx, i32 rx, i32 baud)
{
    check_port(port);
    if (tx != 1)
    {
        fail(FLASH("System.ArgumentException: Pin {0} cannot be UART0's TX: on the Arduino Uno it is pin 1 (D1). "
            "(Parameter 'tx')"), tx);
    }

    if (rx != 0)
    {
        fail(FLASH("System.ArgumentException: Pin {0} cannot be UART0's RX: on the Arduino Uno it is pin 0 (D0). "
            "(Parameter 'rx')"), rx);
    }

    if (baud < BAUD_MIN || baud > BAUD_MAX)
    {
        fail(FLASH("System.ArgumentOutOfRangeException: UART0 cannot run at {0} baud: on the Uno's 16 MHz ATmega328P it "
            "runs at 489 to 2000000 baud. (Parameter 'baud')"), baud);
    }

    // 16 MHz / (8 * (divisor + 1)), rounded down as the Arduino core rounds it: (16 MHz / 4 / baud - 1) / 2
    start((u16)((4000000u / (u32)baud - 1u) >> 1));
}

static void check_open(i32 port)
{
    check_port(port);
    if (!open)
    {
        fail(FLASH("System.InvalidOperationException: UART{0} is not open: call Uart.Open({0}, 1, 0, baud) first."),
            port);
    }
}

// ---- Sending and reading -----------------------------------------------------------------------------------------

void ChipSharp_Runtime_UartWriteByte(i32 port, i32 value)
{
    check_open(port);
    write_char((char)value);
}

// The next byte in the buffer, or -1. Interrupts are off for the moment it takes, as the program's main code and its
// [Interrupt(Event.Uart)] method may both read.
static i32 take(void)
{
    u8 sreg = interrupts_off();
    i32 value = -1;
    if (head != tail)
    {
        value = bytes[tail % BUFFER_SIZE];
        tail++;
    }

    interrupts_back(sreg);
    return value;
}

i32 ChipSharp_Runtime_UartReadByte(i32 port)
{
    check_open(port);
    return take();
}

i32 ChipSharp_Runtime_UartAvailable(i32 port)
{
    check_open(port);
    return (u8)(head - tail);
}

// ---- The uart event ----------------------------------------------------------------------------------------------

// Bytes already waiting run the method at once, with interrupts off, as their interrupt would have.
void ChipSharp_Runtime_UartWatch(i32 port)
{
    check_open(port);
    u8 sreg = interrupts_off();
    watched = 1;
    if (head != tail)
    {
        chipsharp_isr_irq28();
    }

    interrupts_back(sreg);
}

void ChipSharp_Runtime_UartUnwatch(i32 port)
{
    check_open(port);
    watched = 0;
}

// ---- Console.Read ------------------------------------------------------------------------------------------------

// A byte read too far (not part of the char before it), which the next char starts with; -1 for none.
static i32 console_next = -1;

// The second half (a low surrogate) of a char above U+FFFF, which the next Console.Read returns; 0 for none.
static u16 console_low_surrogate;

// The next byte of USART0's input, waiting for it: asleep (idle) until an interrupt comes, as one does when a byte
// arrives. SEI's next instruction runs before any interrupt, so none comes between the look at the buffer and the SLEEP.
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
        u8 sreg = interrupts_off();
        if (head != tail)
        {
            u8 value = bytes[tail % BUFFER_SIZE];
            tail++;
            interrupts_back(sreg);
            return value;
        }

        if ((sreg & SREG_I) == 0)
        {
            // interrupts were off (a program never waits so: chipsharp check), so no interrupt brings bytes: they are
            // taken from USART0 here
            if ((REG(UCSR0A) & UCSR0A_RXC0) != 0)
            {
                received(0);
            }

            interrupts_back(sreg);
            continue;
        }

        REG(SMCR) = SMCR_IDLE;
        __asm__ volatile("sei\n\tsleep" ::: "memory");
        REG(SMCR) = 0;
    }
}

// Console.Read: the next char of the console's input, as .NET reads UTF-8: a char of one to three bytes, or the two
// halves of one of four bytes (a surrogate pair), and U+FFFD for each byte that is not part of a well-formed one. On the
// first call, USART0 starts receiving on D0 at 115200 baud (unless the program opened it with Uart.Open). It never ends:
// a serial line has no end of input.
i32 ChipSharp_Runtime_ConsoleRead(void)
{
    if (!open)
    {
        start(UBRR_115200);
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
        return (i32)(0xd800u | (value >> 10));
    }

    return (i32)value;
}
