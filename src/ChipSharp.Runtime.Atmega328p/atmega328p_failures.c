// ChipSharp runtime for the ATmega328P: how the peripherals' functions (atmega328p_i2c.c, atmega328p_spi.c,
// atmega328p_adc.c, atmega328p_pwm.c) stop the program on a mistake in it, a bus, pin, frequency or mode the chip does
// not have: the message .NET's exception would give, on USART0 (the console's port, D1), then the failure stop.
//
// Freestanding, like atmega328p_runtime.c, which sends the bytes. A message is text in flash, each in a section of its
// own in the file that has it, so that a program has only the messages of what it uses; it reaches the port a char at
// a time, so it takes no RAM.
//
//   chipsharp_hardware_failure   "Unhandled exception. " and the text, with the numbers n0 to n3 in place of its {0}
//                                to {3}, as .NET writes them; then the stop (chipsharp_halt_failure, avr_runtime.c)

#include "chipsharp_hardware.h"

typedef long i32;
typedef unsigned long u32;
typedef unsigned char u8;

typedef const __flash char flash_char;

// In atmega328p_runtime.c: count bytes in RAM to USART0 (115200 baud unless the program set another speed).
void chipsharp_usart0_write(const char *bytes, unsigned int count);

// In avr_runtime.c: stops the program as a failure (after chipsharp_before_halt, which sends what is still going out).
__attribute__((noreturn)) void chipsharp_halt_failure(void);

static void write_char(char c)
{
    chipsharp_usart0_write(&c, 1);
}

// A whole number as .NET writes it, a '-' first when negative, with no division.
static void write_number(i32 number)
{
    static const __flash u32 powers[] = {1000000000u, 100000000u, 10000000u, 1000000u, 100000u, 10000u, 1000u, 100u,
        10u, 1u};
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
}

__attribute__((noreturn)) void chipsharp_hardware_failure(flash_char *text, i32 n0, i32 n1, i32 n2, i32 n3)
{
    static const __flash char start[] = "Unhandled exception. ";
    for (flash_char *c = start; *c != 0; c++)
    {
        write_char(*c);
    }

    for (char c = *text; c != 0; c = *++text)
    {
        if (c == '{' && text[1] >= '0' && text[1] <= '3' && text[2] == '}')
        {
            u8 which = (u8)(text[1] - '0');
            write_number(which == 0 ? n0 : which == 1 ? n1 : which == 2 ? n2 : n3);
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
