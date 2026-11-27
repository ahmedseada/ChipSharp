// ChipSharp runtime for the Microchip ATmega328P at 16 MHz (the Arduino Uno's chip, and the Nano's): what this chip needs
// on top of the AVR runtime (src/ChipSharp.Runtime.Avr: start-up, the vector table, the math helpers, the stops).
//
// Freestanding: no headers, no C library, no avr-libc. Register addresses and bits are from the ATmega328P datasheet, as
// data addresses (what LD and ST reach: the I/O registers at 0x20-0x5f, the extended ones at 0x60-0xff). A C# int is 32
// bits, a long in AVR's C, whose int has 16.
//
//   chipsharp_chip_init                start-up (chipsharp_start, avr_runtime.c, calls it before the program, with
//                                      interrupts still off): Timer1 starts counting the clock
//   ChipSharp_Runtime_Pin...           ChipSharp.Hardware.Pin by the Uno's (Arduino's) pin numbers: 0-7 are D0-D7 (port D,
//                                      bits 0-7), 8-13 are D8-D13 (port B, bits 0-5; the LED "L" is 13), 14-19 are A0-A5
//                                      (port C, bits 0-5); other numbers are ignored (and read as low). Input is high
//                                      impedance, as on the Pico, without the pull-up: a button needs a resistor, or the
//                                      pull-up turned on through Registers after Pin.Input (its bit of PORTB, PORTC or
//                                      PORTD set: 0x25, 0x28, 0x2b). While the console runs, USART0 has D1 (and D0 once it
//                                      receives)
//   ChipSharp_Runtime_Delay...         ChipSharp.Hardware.Timing: waits, watching Timer1, a few microseconds more than given
//   ChipSharp_Runtime_Clock...         the clock (DateTime.Now, Clock.Start) and the uptime (Stopwatch,
//                                      Environment.TickCount), from Timer1's count of half microseconds
//   ChipSharp_Runtime_Register...      ChipSharp.Hardware.Registers: the chip's registers are bytes, so a read or write is
//                                      one byte at that data address (the value's low 8 bits), e.g. 0x25 is PORTB
//   chipsharp_console_write            where Console.Write and Console.WriteLine write (their text comes from
//                                      chipsharp_console.c): USART0 on D1 (TX), 115200 baud, 8N1, set up on the first write
//   chipsharp_failure_write,           where the AVR runtime's failure messages go (avr_failures.c, .NET's "Unhandled
//   chipsharp_before_halt              exception. ..."), the same USART0; and what runs before the program stops: the bytes
//                                      still going out are sent
//   chipsharp_interrupt                where the runtime's interrupts come (atmega328p_interrupts.S), to the handler the
//                                      runtime file that uses each interrupt put in chipsharp_interrupt_hooks
//
// The clock: Timer1 counts 16 MHz / 8, two counts a microsecond, from 0 to 0xffff and round again (normal mode, every
// 32.768 ms), and its overflow interrupt (TIMER1_OVF, __vector_13, a few instructions in every program, as Arduino's)
// counts the rounds in 32 bits: 48 bits of half microseconds, which go round after 4.46 years. Timer1's compare B is the
// timer event's (atmega328p_events.c); Timer0 and Timer2 are left to the program.
//
// USART0: 115200 baud is 16 MHz / (8 * 17) with U2X0, 117647 baud really, 2.1% fast, as the Arduino core's
// Serial.begin(115200) has it (without U2X0, 16 MHz / (16 * 9) is 111111, 3.5% slow). A byte goes out when the one before
// has (polling UDRE0, no send buffer, which would take RAM): 87 microseconds each. Receiving is atmega328p_uart.c's.

typedef unsigned char u8;
typedef unsigned int u16;
typedef long i32;
typedef unsigned long u32;
typedef long long i64;
typedef unsigned long long u64;

#define REG(address) (*(volatile u8 *)(u16)(address))

#define SREG 0x5f

#define TIFR1 0x36
#define TIFR1_TOV1 0x01u
#define TIMSK1 0x6f
#define TIMSK1_TOIE1 0x01u
#define TCCR1A 0x80
#define TCCR1B 0x81
#define TCCR1B_CLOCK_8 0x02u // CS11: clk/8
#define TCNT1L 0x84
#define TCNT1H 0x85

#define UCSR0A 0xc0
#define UCSR0A_UDRE0 0x20u
#define UCSR0A_TXC0 0x40u
#define UCSR0A_U2X0 0x02u
#define UCSR0B 0xc1
#define UCSR0B_RXCIE0 0x80u
#define UCSR0B_RXEN0 0x10u
#define UCSR0B_TXEN0 0x08u
#define UCSR0C 0xc2
#define UCSR0C_8_BITS 0x06u // UCSZ01, UCSZ00: 8 data bits, no parity, one stop bit
#define UBRR0L 0xc4
#define UBRR0H 0xc5
#define UDR0 0xc6

// 16 MHz / (8 * (16 + 1)) with U2X0: 117647 baud, as near to 115200 as the divisor gets
#define UBRR_115200 16u

// The pins' registers: PINx (reads the pins; a 1 written toggles the output), DDRx (1: an output), PORTx (the level
// driven, or the pull-up of an input), for port B at 0x23, port C at 0x26, port D at 0x29.
#define PORT_B 0x23u
#define PORT_C 0x26u
#define PORT_D 0x29u
#define PIN_REGISTER 0u
#define DDR_REGISTER 1u
#define PORT_REGISTER 2u
#define PIN_COUNT 20

// Turns interrupts off and returns how they were (SREG), for interrupts_back: around what no handler may see half done.
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

// ---- The clock ---------------------------------------------------------------------------------------------------

// The rounds Timer1 has counted: TIMER1_OVF adds one each time it goes from 0xffff to 0.
static volatile u32 rounds;

__attribute__((signal)) void __vector_13(void)
{
    rounds++;
}

void chipsharp_chip_init(void)
{
    REG(TCCR1A) = 0; // normal mode: 0 to 0xffff, then 0 again
    REG(TCNT1H) = 0; // the high byte first: it goes through Timer1's temporary register
    REG(TCNT1L) = 0;
    REG(TIFR1) = TIFR1_TOV1; // write 1 to clear
    REG(TIMSK1) = TIMSK1_TOIE1;
    REG(TCCR1B) = TCCR1B_CLOCK_8;
}

// The clock now, with interrupts off: the rounds, and Timer1's count. Timer1's 16-bit registers are read through a
// temporary register, TCNT1L first (which latches TCNT1H), that any other read of them in between would change; and a round
// Timer1 has just finished, whose interrupt has not come yet (interrupts are off, or this runs in a handler), is counted
// here: the overflow flag is set, and the count is low.
void chipsharp_clock_read(u32 *rounds_now, u16 *count_now)
{
    u8 sreg = interrupts_off();
    u8 low = REG(TCNT1L);
    u8 high = REG(TCNT1H);
    u32 counted = rounds;
    if ((REG(TIFR1) & TIFR1_TOV1) != 0 && high < 0x80u)
    {
        counted++;
    }

    interrupts_back(sreg);
    *rounds_now = counted;
    *count_now = (u16)high << 8 | low;
}

// Half microseconds since start-up, all 48 bits.
static u64 half_microseconds(void)
{
    u32 counted;
    u16 count;
    chipsharp_clock_read(&counted, &count);
    return (u64)counted << 16 | count;
}

// Timer1's count alone, in a few instructions: what the delays watch.
static u16 timer1_count(void)
{
    u8 sreg = interrupts_off();
    u8 low = REG(TCNT1L);
    u8 high = REG(TCNT1H);
    interrupts_back(sreg);
    return (u16)high << 8 | low;
}

// Microseconds since start-up, their low 32 bits (they go round after 71 minutes): the timer event's time.
u32 chipsharp_clock_microseconds(void)
{
    u32 counted;
    u16 count;
    chipsharp_clock_read(&counted, &count);
    return counted << 15 | count >> 1;
}

// ---- Pins --------------------------------------------------------------------------------------------------------

// The port (the address of its PINx, see above) and the bit of Uno pin number pin: 0 for a number the Uno does not have.
// Inlined in each pin function, which a program may call in a tight loop (to make a signal itself, say).
__attribute__((always_inline)) static inline u8 pin_port(i32 pin, u8 *mask)
{
    if (pin < 0 || pin >= PIN_COUNT)
    {
        return 0;
    }

    u8 bit = (u8)pin;
    u8 port = PORT_D;
    if (bit >= 14)
    {
        port = PORT_C;
        bit -= 14;
    }
    else if (bit >= 8)
    {
        port = PORT_B;
        bit -= 8;
    }

    *mask = (u8)(1u << bit);
    return port;
}

// The same for atmega328p_events.c.
u8 chipsharp_pin_port(i32 pin, u8 *mask)
{
    return pin_port(pin, mask);
}

// Sets (high) or clears the bits of mask in a pin register, with interrupts off: a handler may change the same port.
static void change(u8 address, u8 mask, u8 high)
{
    u8 sreg = interrupts_off();
    REG(address) = high ? REG(address) | mask : REG(address) & (u8)~mask;
    interrupts_back(sreg);
}

// Pin.Output: the pin drives low until written (PORTx first, so that it never drives high on the way).
void ChipSharp_Runtime_PinOutput(i32 pin)
{
    u8 mask;
    u8 port = pin_port(pin, &mask);
    if (port == 0)
    {
        return;
    }

    change(port + PORT_REGISTER, mask, 0);
    change(port + DDR_REGISTER, mask, 1);
}

// Pin.Input: the pin stops driving, and its pull-up is off; Read reads it.
void ChipSharp_Runtime_PinInput(i32 pin)
{
    u8 mask;
    u8 port = pin_port(pin, &mask);
    if (port == 0)
    {
        return;
    }

    change(port + DDR_REGISTER, mask, 0);
    change(port + PORT_REGISTER, mask, 0);
}

// A bool arrives as an int; anything but 0 is high.
void ChipSharp_Runtime_PinWrite(i32 pin, i32 high)
{
    u8 mask;
    u8 port = pin_port(pin, &mask);
    if (port != 0)
    {
        change(port + PORT_REGISTER, mask, high != 0);
    }
}

// A 1 written to PINx toggles the bit of PORTx, in one write: nothing to keep from a handler.
void ChipSharp_Runtime_PinToggle(i32 pin)
{
    u8 mask;
    u8 port = pin_port(pin, &mask);
    if (port != 0)
    {
        REG(port + PIN_REGISTER) = mask;
    }
}

// Returns a bool as 0 or 1.
i32 ChipSharp_Runtime_PinRead(i32 pin)
{
    u8 mask;
    u8 port = pin_port(pin, &mask);
    return port != 0 && (REG(port + PIN_REGISTER) & mask) != 0;
}

// ---- Timing ------------------------------------------------------------------------------------------------------

// Waits until count half microseconds have passed since Timer1's count was from, and returns Timer1's count then (from
// + count, as Timer1 goes round). Timer1's count alone, read in a tight loop (a microsecond a turn), in steps of 8 ms:
// far less than a round of Timer1 (32.768 ms), so that the difference of two readings is the time between them even with
// interrupt handlers running in between; each step starts where the one before ended, so the steps add up exactly.
static u16 wait(u16 from, u32 count)
{
    for (;;)
    {
        u16 step = count > 0x4000u ? 0x4000u : (u16)count;
        while ((u16)(timer1_count() - from) < step)
        {
        }

        from += step;
        count -= step;
        if (count == 0)
        {
            return from;
        }
    }
}

// The delays count from Timer1's count when they are called, and wait one half microsecond more than they are given, as
// Timer1 may be about to count on when it is read: at least the time given, and a few microseconds more for the call.
void ChipSharp_Runtime_DelayMicroseconds(i32 microseconds)
{
    u16 from = timer1_count();
    if (microseconds > 0)
    {
        wait(from, ((u32)microseconds << 1) + 1u); // at most 2^32 - 1
    }
}

void ChipSharp_Runtime_DelayMilliseconds(i32 milliseconds)
{
    u16 from = timer1_count();
    u32 left = milliseconds > 0 ? (u32)milliseconds : 0u;
    u32 more = 1;
    while (left > 0)
    {
        u32 step = left > 1000000u ? 1000000u : left;
        from = wait(from, step * 2000u + more);
        more = 0;
        left -= step;
    }
}

// ---- The clock and the uptime ------------------------------------------------------------------------------------

// The software clock, in DateTime's ticks of 100 ns: the date it was given (Clock.Start), or 1 January 2000 until it is
// given one, and the clock's count then. Read and written with interrupts off, so that a handler that starts the clock
// never leaves half of it for Main to read.
static i64 clock_ticks = 630822816000000000LL;
static u64 clock_start;

// DateTime.UtcNow, DateTime.Now: the chip has no time zones, so both are the clock's date and the time since, five ticks
// of 100 ns a count.
i64 ChipSharp_Runtime_ClockUtcTicks(void)
{
    u8 sreg = interrupts_off();
    i64 ticks = clock_ticks;
    u64 since = half_microseconds() - clock_start;
    interrupts_back(sreg);
    return ticks + (i64)((since << 2) + since);
}

i64 ChipSharp_Runtime_ClockLocalTicks(void)
{
    return ChipSharp_Runtime_ClockUtcTicks();
}

// Clock.Start(date): the clock counts from date's ticks from now on.
void ChipSharp_Runtime_ClockStart(i64 ticks)
{
    u8 sreg = interrupts_off();
    clock_ticks = ticks;
    clock_start = half_microseconds();
    interrupts_back(sreg);
}

// Stopwatch.GetTimestamp, in microseconds since start-up; Stopwatch.Frequency is 1,000,000, as on the Pico.
i64 ChipSharp_Runtime_ClockTimestamp(void)
{
    return (i64)(half_microseconds() >> 1);
}

i64 ChipSharp_Runtime_ClockFrequency(void)
{
    return 1000000;
}

// Stopwatch.GetElapsedTime: 10 ticks of 100 ns a microsecond, exactly what .NET's double arithmetic gives.
i64 ChipSharp_Runtime_ClockElapsedTicks(i64 start, i64 end)
{
    i64 difference = end - start;
    return (difference << 3) + (difference << 1);
}

// Environment.TickCount64: milliseconds since start-up.
i64 ChipSharp_Runtime_ClockTickCount64(void)
{
    return (i64)(half_microseconds() / 2000u);
}

// ---- Registers ---------------------------------------------------------------------------------------------------

u32 ChipSharp_Runtime_RegisterRead(u32 address)
{
    return REG((u16)address);
}

void ChipSharp_Runtime_RegisterWrite(u32 address, u32 value)
{
    REG((u16)address) = (u8)value;
}

// ---- USART0: the console's output, and the failures' -------------------------------------------------------------

// Set once USART0 sends, also by Uart.Open(0, ...) and Console.Read (atmega328p_uart.c), whose speed the console then
// keeps; and once a byte has gone to it, after which chipsharp_before_halt waits for the last one to go out.
u8 chipsharp_usart0_started;
static u8 sent;

// Sets USART0 up: its speed (UBRR0, with U2X0), 8N1, sending on D1, and receiving on D0 when receive is set (with the
// receive interrupt, atmega328p_uart.c's). What it was still sending goes out first, at the speed it had.
void chipsharp_usart0_start(u16 divisor, u8 receive)
{
    if (sent)
    {
        while ((REG(UCSR0A) & UCSR0A_TXC0) == 0)
        {
        }
    }

    u8 sreg = interrupts_off();
    REG(UCSR0B) = 0;
    REG(UBRR0H) = (u8)(divisor >> 8);
    REG(UBRR0L) = (u8)divisor;
    REG(UCSR0A) = UCSR0A_U2X0;
    REG(UCSR0C) = UCSR0C_8_BITS;
    REG(UCSR0B) = UCSR0B_TXEN0 | (receive ? UCSR0B_RXEN0 | UCSR0B_RXCIE0 : 0u);
    chipsharp_usart0_started = 1;
    sent = 0;
    interrupts_back(sreg);
}

// Sends a byte once USART0 has room for it. The check and the write go together with interrupts off, so that a handler
// that writes too never puts its byte in between; the wait itself leaves them on. TXC0 is cleared (a 1 written, U2X0
// kept) with each byte, so that it is set again only when the last has gone out.
static void send(u8 value)
{
    for (;;)
    {
        while ((REG(UCSR0A) & UCSR0A_UDRE0) == 0)
        {
        }

        u8 sreg = interrupts_off();
        if ((REG(UCSR0A) & UCSR0A_UDRE0) != 0)
        {
            REG(UCSR0A) = (REG(UCSR0A) & UCSR0A_U2X0) | UCSR0A_TXC0;
            REG(UDR0) = value;
            sent = 1;
            interrupts_back(sreg);
            return;
        }

        interrupts_back(sreg);
    }
}

// count bytes in RAM to USART0, which starts at 115200 baud the first time; for atmega328p_uart.c too.
void chipsharp_usart0_write(const char *bytes, unsigned int count)
{
    if (!chipsharp_usart0_started)
    {
        chipsharp_usart0_start(UBRR_115200, 0);
    }

    for (unsigned int i = 0; i < count; i++)
    {
        send((u8)bytes[i]);
    }
}

// A byte the program's code gives: in flash when its address has bit 15 set (0x8000 + its flash address, as the code
// generator gives its constants, see ChipSharp.Backend.Llvm's LlvmFlash), in RAM otherwise.
static u8 program_byte(const char *address)
{
    u16 bits = (u16)address;
    return (bits & 0x8000u) != 0 ? *(const __flash u8 *)(bits & 0x7fffu) : (u8)*address;
}

// Console.Write and Console.WriteLine (chipsharp_console.c) write here: text as UTF-8 bytes, in RAM or in flash.
void chipsharp_console_write(const char *bytes, unsigned int count)
{
    if (!chipsharp_usart0_started)
    {
        chipsharp_usart0_start(UBRR_115200, 0);
    }

    for (unsigned int i = 0; i < count; i++)
    {
        send(program_byte(bytes + i));
    }
}

// The AVR runtime's failure messages (avr_failures.c) come here a piece at a time, in RAM; then chipsharp_halt_failure
// stops the program, after chipsharp_before_halt.
void chipsharp_failure_write(const char *text, unsigned int count)
{
    chipsharp_usart0_write(text, count);
}

// Before the program stops (it ended, or failed): what USART0 is still sending goes out. Polling, as interrupts may be
// off already.
void chipsharp_before_halt(void)
{
    if (sent)
    {
        while ((REG(UCSR0A) & UCSR0A_TXC0) == 0)
        {
        }
    }
}

// ---- The runtime's interrupts ------------------------------------------------------------------------------------

// The handlers of the interrupts atmega328p_interrupts.S takes, by kind: the pins' (INT0, INT1, PCINT0-2), the timer
// event's (TIMER1_COMPB) and USART0's receiving (USART_RX). The file of each puts its handler here when the program first
// uses it (Pin.Watch, Timing.Every and After, Uart.Open and Console.Read), so that a program that does not has none of
// them, nor their variables: the vector table in flash cannot change, and it names the entries in every program.
void (*volatile chipsharp_interrupt_hooks[3])(u8 source);

// atmega328p_interrupts.S comes here, with registers and SREG saved, interrupts off: source is 0 for INT0, 1 INT1, 2-4
// PCINT0-2, 5 TIMER1_COMPB, 6 USART_RX.
void chipsharp_interrupt(u8 source)
{
    void (*hook)(u8) = chipsharp_interrupt_hooks[source < 5 ? 0 : source - 4];
    if (hook != 0)
    {
        hook(source);
    }
}
