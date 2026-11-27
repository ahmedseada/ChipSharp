// ChipSharp runtime for the ATmega328P: its two-wire interface (TWI) as ChipSharp.Hardware.I2c's bus 0, SDA on A4 (pin
// 18) and SCL on A5 (pin 19), as the Uno's headers have them (and again by AREF).
//
// Freestanding, like atmega328p_runtime.c. The transfers follow the datasheet's (22.6, "Using the TWI"), polling TWINT,
// with a time limit; the program's C# (ChipSharp.Library's I2c) hands each span over as its first byte and its length.
//
//   ChipSharp_Runtime_I2cOpen     I2c.Open(0, 18, 19, frequency): the TWI on as the bus's controller (master), its
//                                 clock the nearest 16 MHz / (16 + 2 * TWBR * prescaler) makes at or below the
//                                 frequency, 490 to 400000 Hz (100 kHz is TWBR 72, 400 kHz TWBR 12, both exact), and
//                                 the pins' own pull-ups on (some 35 kOhm, to 5 V: a long bus or a fast one needs
//                                 resistors of its own, 4.7 kOhm say); what Adc.Input did to A4 and A5 is undone
//   ChipSharp_Runtime_I2cWrite    I2c.Write, and WriteRead's write, which ends without a stop: the bus stays the chip's,
//                                 and the next transfer starts with a repeated start
//   ChipSharp_Runtime_I2cRead     I2c.Read, Probe, and WriteRead's read; the last byte is answered with a NACK
//
// A transfer returns 1 when the device answered its address and took every byte; 0 when it did not (the chip then sends
// the stop), and 0 when the bus did not move for longer than a byte's time limit (1 ms and 50 clock periods, as on the
// Pico), e.g. a device holding the clock low: the TWI is then switched off and on again, which lets go of the pins, so
// that a transfer never waits for ever. The bytes sent may be in flash, where the program's constants are (an address
// with bit 15 set, see atmega328p_runtime.c). A bus, pin, frequency or address the chip does not have, or a bus that is
// not open, stops the program (chipsharp_hardware_failure, atmega328p_failures.c) with a message that says what to do.

typedef unsigned char u8;
typedef unsigned int u16;
typedef long i32;
typedef unsigned long u32;

#define REG(address) (*(volatile u8 *)(u16)(address))

#define SREG 0x5f
#define PRR 0x64
#define PRR_TWI 0x80u

#define DDRC 0x27
#define PORTC 0x28
#define DIDR0 0x7e
#define PINS_SDA_SCL 0x30u // PC4 (A4) and PC5 (A5)

#define TWBR 0xb8
#define TWSR 0xb9
#define TWDR 0xbb
#define TWCR 0xbc
#define TWINT 0x80u
#define TWEA 0x40u
#define TWSTA 0x20u
#define TWSTO 0x10u
#define TWEN 0x04u
#define STATUS_MASK 0xf8u

// The states of the TWI after each step (TWSR, datasheet table 22-2 and 22-3).
#define STARTED 0x08u
#define RESTARTED 0x10u
#define ADDRESS_WRITE_ACK 0x18u
#define DATA_SENT_ACK 0x28u
#define ARBITRATION_LOST 0x38u
#define ADDRESS_READ_ACK 0x40u
#define DATA_RECEIVED_ACK 0x50u
#define DATA_RECEIVED_NACK 0x58u

#define CLOCK_HZ 16000000u
#define SDA_PIN 18
#define SCL_PIN 19
#define LOWEST_FREQUENCY 490
#define HIGHEST_FREQUENCY 400000
#define HIGHEST_ADDRESS 127

typedef const __flash char flash_char;

// Text in flash, in a section of its own (LLVM puts every __flash constant of a file in one .progmem.data, which the
// linker would keep whole for any one of them).
#define TEXT_OF(name) #name
#define SECTION_NAME(number) ".progmem.data.chipsharp_i2c" TEXT_OF(number)
#define FLASH(text)                                                                                                    \
    (__extension__({                                                                                                   \
        static const __flash char flash_text[] __attribute__((section(SECTION_NAME(__COUNTER__)))) = text;             \
        flash_text;                                                                                                    \
    }))

// In atmega328p_failures.c and atmega328p_runtime.c.
__attribute__((noreturn)) void chipsharp_hardware_failure(flash_char *text, i32 n0, i32 n1, i32 n2, i32 n3);
u32 chipsharp_clock_microseconds(void);

// The bus's clock in hertz once it is open, else 0; how long a byte may take, in microseconds. (After a write without a
// stop the bus is still the chip's, and the TWI makes the next start a repeated start itself.)
static u32 frequency;
static u32 byte_limit;

// A byte the program gives: in flash when its address has bit 15 set, in RAM otherwise.
static u8 program_byte(const u8 *address)
{
    u16 bits = (u16)address;
    return (bits & 0x8000u) != 0 ? *(const __flash u8 *)(bits & 0x7fffu) : *address;
}

// ---- Buses and pins ----------------------------------------------------------------------------------------------

static void check_bus(i32 bus)
{
    if (bus != 0)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: I2C bus {0} does not exist: the ATmega328P has bus 0 "
            "(I2C0, its TWI, on A4 and A5). (Parameter 'bus')"), bus, 0, 0, 0);
    }
}

static void check_open(i32 bus)
{
    check_bus(bus);
    if (frequency == 0)
    {
        chipsharp_hardware_failure(FLASH("System.InvalidOperationException: I2C0 is not open: call I2c.Open(0, 18, 19, "
            "frequency) first (SDA on A4, SCL on A5)."), 0, 0, 0, 0);
    }
}

static void check_address(i32 address)
{
    if (address < 0 || address > HIGHEST_ADDRESS)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: I2C address {0} does not exist: a 7-bit address is "
            "from 0 to 127 (0x7F). (Parameter 'address')"), address, 0, 0, 0);
    }
}

// The TWI on, idle, at the bus's clock: from start-up, or again after the bus stopped moving.
static void start_twi(void)
{
    REG(TWCR) = 0;
    REG(TWCR) = TWEN;
}

void ChipSharp_Runtime_I2cOpen(i32 bus, i32 sda, i32 scl, i32 hertz)
{
    check_bus(bus);
    if (sda != SDA_PIN)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: Pin {0} is not the SDA pin of I2C0: on the Arduino Uno "
            "it is pin 18 (A4). (Parameter 'sda')"), sda, 0, 0, 0);
    }

    if (scl != SCL_PIN)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: Pin {0} is not the SCL pin of I2C0: on the Arduino Uno "
            "it is pin 19 (A5). (Parameter 'scl')"), scl, 0, 0, 0);
    }

    if (hertz < LOWEST_FREQUENCY || hertz > HIGHEST_FREQUENCY)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: An I2C clock of {0} Hz is not one the ATmega328P makes: "
            "it is from 490 to 400000 Hz (100000 is standard mode, 400000 fast mode). (Parameter 'frequency')"),
            hertz, 0, 0, 0);
    }

    // SCL = 16 MHz / (16 + 2 * TWBR * 4^TWPS): the smallest prescaler whose TWBR fits 8 bits, and the smallest TWBR
    // that makes the clock no faster than asked (at 400 kHz and below, TWBR is 12 or more: the datasheet's 10 at least)
    u32 f = (u32)hertz;
    u8 prescale = 0;
    u32 divisor;
    for (;;)
    {
        u32 unit = 2u * f << (2u * prescale);
        divisor = (CLOCK_HZ - 16u * f + unit - 1u) / unit;
        if (divisor <= 255u || prescale == 3)
        {
            break;
        }

        prescale++;
    }

    u8 sreg = REG(SREG);
    __asm__ volatile("cli" ::: "memory");
    REG(PRR) &= (u8)~PRR_TWI;
    REG(DIDR0) &= (u8)~PINS_SDA_SCL;
    REG(DDRC) &= (u8)~PINS_SDA_SCL;
    REG(PORTC) |= PINS_SDA_SCL; // the pull-ups
    __asm__ volatile("" ::: "memory");
    REG(SREG) = sreg;

    REG(TWBR) = (u8)divisor;
    REG(TWSR) = prescale;
    frequency = f;
    // a byte is 9 clocks; a device may hold the clock low for a while (clock stretching): 50 clocks, and 1 ms more
    byte_limit = 1000u + 50000000u / f;
    start_twi();
}

// ---- Transfers ---------------------------------------------------------------------------------------------------

// Starts the step that control asks for (TWINT written 1 starts it) and waits until it is done: 1, or 0 when the
// byte's time limit passed first, after which the TWI starts afresh.
static u8 step(u8 control)
{
    REG(TWCR) = control | TWINT | TWEN;
    u32 start = chipsharp_clock_microseconds();
    while ((REG(TWCR) & TWINT) == 0)
    {
        if (chipsharp_clock_microseconds() - start > byte_limit)
        {
            start_twi();
            return 0;
        }
    }

    return 1;
}

static u8 status(void)
{
    return REG(TWSR) & STATUS_MASK;
}

// The stop, and the wait for it to be on the bus (TWSTO clears then): 1, or 0 when the bus did not move in time.
static i32 stop(void)
{
    REG(TWCR) = TWINT | TWEN | TWSTO;
    u32 start = chipsharp_clock_microseconds();
    while ((REG(TWCR) & TWSTO) != 0)
    {
        if (chipsharp_clock_microseconds() - start > byte_limit)
        {
            start_twi();
            return 0;
        }
    }

    return 1;
}

// The device did not answer, or refused a byte: the chip sends the stop, and the transfer says no. A transfer that
// lost the bus to another controller has let go of it already.
static i32 refused(void)
{
    if (status() == ARBITRATION_LOST)
    {
        REG(TWCR) = TWINT | TWEN;
        return 0;
    }

    stop();
    return 0;
}

// A start (a repeated start while the bus is still the chip's) and the address with the direction: 1 when the device
// answered, else 0 (after the stop, or after the time limit).
static i32 begin(i32 address, u8 read, u8 answered)
{
    if (!step(TWSTA))
    {
        return 0;
    }

    u8 started = status();
    if (started != STARTED && started != RESTARTED)
    {
        return refused();
    }

    REG(TWDR) = (u8)((u8)address << 1 | read);
    if (!step(0))
    {
        return 0;
    }

    return status() == answered ? 1 : refused();
}

i32 ChipSharp_Runtime_I2cWrite(i32 bus, i32 address, const u8 *data, i32 count, i32 stop_after)
{
    check_open(bus);
    check_address(address);
    if (count <= 0)
    {
        return 0;
    }

    if (!begin(address, 0, ADDRESS_WRITE_ACK))
    {
        return 0;
    }

    for (i32 i = 0; i < count; i++)
    {
        REG(TWDR) = program_byte(data + i);
        if (!step(0))
        {
            return 0;
        }

        if (status() != DATA_SENT_ACK)
        {
            return refused();
        }
    }

    return stop_after ? stop() : 1;
}

i32 ChipSharp_Runtime_I2cRead(i32 bus, i32 address, u8 *data, i32 count)
{
    check_open(bus);
    check_address(address);
    if (count <= 0)
    {
        return 0;
    }

    if (!begin(address, 1, ADDRESS_READ_ACK))
    {
        return 0;
    }

    // each byte answered with an ACK (TWEA), but the last, which a NACK ends
    for (i32 i = 0; i < count; i++)
    {
        u8 last = i == count - 1;
        if (!step(last ? 0 : TWEA))
        {
            return 0;
        }

        if (status() != (last ? DATA_RECEIVED_NACK : DATA_RECEIVED_ACK))
        {
            return refused();
        }

        data[i] = REG(TWDR);
    }

    return stop();
}
