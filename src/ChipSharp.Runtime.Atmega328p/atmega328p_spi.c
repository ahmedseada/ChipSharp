// ChipSharp runtime for the ATmega328P: its SPI controller as ChipSharp.Hardware.Spi's bus 0, SCK on D13, TX (MOSI) on
// D11 and RX (MISO) on D12, as the Uno's headers (and its ICSP header) have them.
//
// Freestanding, like atmega328p_runtime.c; the program's C# (ChipSharp.Library's Spi) hands each span over as its first
// byte and its length.
//
//   ChipSharp_Runtime_SpiOpen       Spi.Open(0, 13, 11, 12, frequency, mode): the controller (master), 8-bit frames,
//                                   most significant bit first, in the SPI mode asked for (CPOL, CPHA), its clock
//                                   16 MHz / 2, 4, 8, 16, 32, 64 or 128 (8 MHz to 125 kHz), the fastest at or below the
//                                   frequency asked for; SCK and TX made outputs (TX only when it is 11, not -1). D10
//                                   (SS) is made an output too, high, unless it is one already: as an input that went
//                                   low it would make the controller a device (slave). Chip select is the program's
//                                   own pin, D10 or any other
//   ChipSharp_Runtime_SpiWrite      Spi.Write: sends, and drops what comes back
//   ChipSharp_Runtime_SpiRead       Spi.Read: receives, sending zeros
//   ChipSharp_Runtime_SpiTransfer   Spi.Transfer: both, as many bytes each way
//
// Each returns when the last byte has come back, that is once its last bit has left, so that the program may drive its
// chip select (or a shift register's latch) high right after. The bytes sent may be in flash, where the program's
// constants are (an address with bit 15 set, see atmega328p_runtime.c). While the bus is open D13 is its clock (the LED
// "L" flickers with it), D12 an input, and D11's level the bus's even with no TX, so D11 cannot be a PWM output then.
// A bus, pin, frequency or mode the chip does not have, or a bus that is not open, stops the program
// (chipsharp_hardware_failure, atmega328p_failures.c) with a message that says what to do.

#include "chipsharp_hardware.h"

typedef unsigned char u8;
typedef unsigned int u16;
typedef long i32;
typedef unsigned long u32;

#define REG(address) (*(volatile u8 *)(u16)(address))

#define SREG 0x5f
#define PRR 0x64
#define PRR_SPI 0x04u

#define DDRB 0x24
#define PORTB 0x25
#define PIN_SS 0x04u   // PB2, D10
#define PIN_MOSI 0x08u // PB3, D11
#define PIN_SCK 0x20u  // PB5, D13

#define PWM_D11 0x02u // in chipsharp_pwm_outputs

#define SPCR 0x4c
#define SPSR 0x4d
#define SPDR 0x4e
#define SPE 0x40u
#define MSTR 0x10u
#define CPOL 0x08u
#define CPHA 0x04u
#define SPIF 0x80u
#define SPI2X 0x01u

#define CLOCK_HZ 16000000u
#define SCK_PIN 13
#define TX_PIN 11
#define RX_PIN 12
#define NO_PIN (-1)
#define LOWEST_FREQUENCY 125000
#define HIGHEST_FREQUENCY 8000000

typedef const __flash char flash_char;

#define TEXT_OF(name) #name
#define SECTION_NAME(number) ".progmem.data.chipsharp_spi" TEXT_OF(number)
#define FLASH(text)                                                                                                    \
    (__extension__({                                                                                                   \
        static const __flash char flash_text[] __attribute__((section(SECTION_NAME(__COUNTER__)))) = text;             \
        flash_text;                                                                                                    \
    }))

// In atmega328p_failures.c and atmega328p_pwm.c (the PWM outputs, a bit each).
__attribute__((noreturn)) void chipsharp_hardware_failure(flash_char *text, i32 n0, i32 n1, i32 n2, i32 n3);
extern u8 chipsharp_pwm_outputs;

static u8 is_open;

static u8 program_byte(const u8 *address)
{
    u16 bits = (u16)address;
    return (bits & 0x8000u) != 0 ? *(const __flash u8 *)(bits & 0x7fffu) : *address;
}

static void check_bus(i32 bus)
{
    if (bus != 0)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: SPI bus {0} does not exist: the ATmega328P has bus 0 "
            "(SPI0: SCK on D13, TX on D11, RX on D12). (Parameter 'bus')"), bus, 0, 0, 0);
    }
}

// The bus, once it is open, and still the controller (D10 as an input pulled low makes it a device).
static void check_open(i32 bus)
{
    check_bus(bus);
    if (!is_open)
    {
        chipsharp_hardware_failure(FLASH("System.InvalidOperationException: SPI0 is not open: call Spi.Open(0, 13, 11, 12, "
            "frequency, mode) first."), 0, 0, 0, 0);
    }

    if ((REG(SPCR) & MSTR) == 0)
    {
        chipsharp_hardware_failure(FLASH("System.InvalidOperationException: SPI0 is no longer the bus's controller: D10 "
            "(SS) was an input, and went low. Keep pin 10 an output (Pin.Output(10)), then open the bus again."),
            0, 0, 0, 0);
    }
}

void ChipSharp_Runtime_SpiOpen(i32 bus, i32 sck, i32 tx, i32 rx, i32 hertz, i32 mode)
{
    check_bus(bus);
    if (sck != SCK_PIN)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: Pin {0} is not the SCK pin of SPI0: on the Arduino Uno "
            "it is pin 13 (D13). (Parameter 'sck')"), sck, 0, 0, 0);
    }

    if (tx != TX_PIN && tx != NO_PIN)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: Pin {0} is not the TX pin of SPI0: on the Arduino Uno "
            "it is pin 11 (D11, MOSI), or -1 for none. (Parameter 'tx')"), tx, 0, 0, 0);
    }

    if (rx != RX_PIN && rx != NO_PIN)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: Pin {0} is not the RX pin of SPI0: on the Arduino Uno "
            "it is pin 12 (D12, MISO), or -1 for none. (Parameter 'rx')"), rx, 0, 0, 0);
    }

    if (hertz < LOWEST_FREQUENCY || hertz > HIGHEST_FREQUENCY)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: An SPI clock of {0} Hz is not one the ATmega328P makes: "
            "it is 16 MHz / 2 to 128, from 125000 to 8000000 Hz. (Parameter 'frequency')"), hertz, 0, 0, 0);
    }

    if (mode < 0 || mode > 3)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: SPI mode {0} does not exist: the mode is 0, 1, 2 or 3. "
            "(Parameter 'mode')"), mode, 0, 0, 0);
    }

    if ((chipsharp_pwm_outputs & PWM_D11) != 0)
    {
        chipsharp_hardware_failure(FLASH("System.InvalidOperationException: Pin 11 (D11) is a PWM output, and while SPI0 is "
            "open it is the bus's TX (MOSI). Stop it first (Pwm.Stop(11)), or use another PWM pin: 3, 5, 6 or 9."),
            0, 0, 0, 0);
    }

    // the divider: 2 to the (shift + 1), the smallest whose clock is no faster than asked; SPR1:SPR0 and SPI2X make it
    // (2X: 2, 8, 32, 64; without: 4, 16, 64, 128)
    u8 shift = 0;
    while (shift < 6 && (CLOCK_HZ >> (shift + 1u)) > (u32)hertz)
    {
        shift++;
    }

    u8 rate = shift == 6 ? 3u : (u8)(shift >> 1);
    u8 doubled = shift < 6 && (shift & 1u) == 0;

    u8 sreg = REG(SREG);
    __asm__ volatile("cli" ::: "memory");
    REG(PRR) &= (u8)~PRR_SPI;
    if ((REG(DDRB) & PIN_SS) == 0)
    {
        REG(PORTB) |= PIN_SS;
        REG(DDRB) |= PIN_SS;
    }

    REG(DDRB) |= PIN_SCK | (tx == TX_PIN ? PIN_MOSI : 0u);
    REG(SPCR) = 0;
    REG(SPSR) = doubled ? SPI2X : 0u;
    REG(SPCR) = SPE | MSTR | (mode & 2 ? CPOL : 0u) | (mode & 1 ? CPHA : 0u) | rate;
    is_open = 1;
    __asm__ volatile("" ::: "memory");
    REG(SREG) = sreg;
}

// One byte each way: SPIF comes once its last bit has left (reading SPSR then SPDR clears it).
static u8 exchange(u8 value)
{
    REG(SPDR) = value;
    while ((REG(SPSR) & SPIF) == 0)
    {
    }

    return REG(SPDR);
}

void ChipSharp_Runtime_SpiWrite(i32 bus, const u8 *data, i32 count)
{
    check_open(bus);
    for (i32 i = 0; i < count; i++)
    {
        exchange(program_byte(data + i));
    }
}

void ChipSharp_Runtime_SpiRead(i32 bus, u8 *data, i32 count)
{
    check_open(bus);
    for (i32 i = 0; i < count; i++)
    {
        data[i] = exchange(0);
    }
}

// write and read may be the same bytes: each is read before its answer goes in.
void ChipSharp_Runtime_SpiTransfer(i32 bus, const u8 *write, i32 write_count, u8 *read, i32 read_count)
{
    check_open(bus);
    if (write_count != read_count)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: Spi.Transfer receives a byte for each byte it sends: "
            "write has {0} bytes and read {1}. (Parameter 'read')"), write_count, read_count, 0, 0);
    }

    for (i32 i = 0; i < write_count; i++)
    {
        read[i] = exchange(program_byte(write + i));
    }
}
