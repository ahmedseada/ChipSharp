// ChipSharp runtime for the STM8S103F3: ChipSharp.Hardware.I2c, the chip's I2C controller as bus 0, SDA on PB5 (pin 13)
// and SCL on PB4 (pin 12). PB4 and PB5 are true open-drain, with no pull-up of their own: the bus needs its pull-up
// resistors (an I2C module's, most have them); PB5 is also the blue board's LED, which then shows the bus's traffic.
//
//   ChipSharp_Runtime_I2cOpen    the bus at the nearest clock at or below the one asked for: 16 MHz / (2 * CCR) in
//                                standard mode, up to 100 kHz (1954 Hz at the least; 100 kHz exactly), 16 MHz / (3 * CCR)
//                                in fast mode, above 100 kHz up to 400 kHz (381 kHz)
//   ChipSharp_Runtime_I2cWrite   the bytes to an address, a stop after them or not (then the next transfer starts with a
//                                repeated start: I2c.WriteRead)
//   ChipSharp_Runtime_I2cRead    bytes from an address, then the stop
//
// The transfers follow RM0016's sequences (21.4), polling the events: the start (SB), the address (ADDR, or AF when
// nothing answers), each byte (TXE, BTF; RXNE), and the end of a read, whose last byte the controller must be told not
// to acknowledge before it comes (ACK off before the address's ADDR is cleared for one byte, with POS for two, at BTF of
// the last three for more). A transfer gives up, false, when nothing answers its address, a device refuses a byte, or a
// device holds the clock low for longer than two bytes would take: the controller is then reset (SWRST) and set up
// again, the bus free for the next one.

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

#define FREQUENCY_MIN 1954
#define FREQUENCY_MAX 400000
#define STANDARD_MAX 100000

static u8 open;
static u8 continuing; // the transfer before was a write without a stop: this one starts with a repeated start
static u16 ccr;       // CCRH:CCRL, fast mode's FS (bit 15) included
static u8 trise;
static u16 limit; // the most microseconds an event may take: two bytes at the bus's clock, and some

// The controller set up (again) at the clock Open chose: 16 MHz in FREQR, CCR, the rise time, then on.
static void set_up(void)
{
    I2C_CR1 = 0;
    I2C_FREQR = (u8)(MASTER_HZ / 1000000ul);
    I2C_CCRL = (u8)ccr;
    I2C_CCRH = (u8)(ccr >> 8);
    I2C_TRISER = trise;
    I2C_OARH = I2C_OARH_ADDCONF_MASK; // RM0016: written 1
    I2C_CR1 = I2C_CR1_PE_MASK;
}

void ChipSharp_Runtime_I2cOpen(chipsharp_i32 bus, chipsharp_i32 sda, chipsharp_i32 scl, chipsharp_i32 hertz)
{
    if (bus != 0)
    {
        chipsharp_hardware_failure("System.ArgumentException: I2C bus {0} does not exist: the STM8S103F3 has bus 0 (SDA "
            "13, PB5; SCL 12, PB4). (Parameter 'bus')", bus, 0, 0, 0);
    }

    if (sda != STM8S103F3_I2C_SDA_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: Pin {0} is not the SDA pin of I2C0: on the STM8S103F3 it is "
            "pin 13 (PB5). (Parameter 'sda')", sda, 0, 0, 0);
    }

    if (scl != STM8S103F3_I2C_SCL_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: Pin {0} is not the SCL pin of I2C0: on the STM8S103F3 it is "
            "pin 12 (PB4). (Parameter 'scl')", scl, 0, 0, 0);
    }

    if (hertz < FREQUENCY_MIN || hertz > FREQUENCY_MAX)
    {
        chipsharp_hardware_failure("System.ArgumentException: An I2C clock of {0} Hz is not one the STM8S103F3 makes: it "
            "makes 1954 Hz to 400 kHz. (Parameter 'frequency')", hertz, 0, 0, 0);
    }

    // the controller's pins are its own once it is on: inputs until then, not pulled low by their ODR
    PB_DDR &= (u8)~(STM8S103F3_PIN_PB4_MASK | STM8S103F3_PIN_PB5_MASK);
    PB_ODR |= (u8)(STM8S103F3_PIN_PB4_MASK | STM8S103F3_PIN_PB5_MASK);
    u32 f = (u32)hertz;
    if (hertz <= STANDARD_MAX)
    {
        ccr = (u16)((MASTER_HZ + 2u * f - 1u) / (2u * f)); // 80 to 4095
        trise = (u8)(MASTER_HZ / 1000000ul + 1u);          // 1000 ns
    }
    else
    {
        ccr = (u16)((MASTER_HZ + 3u * f - 1u) / (3u * f)) | 0x8000u; // FS, DUTY 0: 14 to 54
        trise = (u8)(MASTER_HZ / 1000000ul * 3u / 10u + 1u);         // 300 ns
    }

    limit = (u16)(18000000ul / f + 200u);
    open = 1;
    continuing = 0;
    set_up();
}

static void check_bus(chipsharp_i32 bus, chipsharp_i32 address)
{
    if (bus != 0)
    {
        chipsharp_hardware_failure("System.ArgumentException: I2C bus {0} does not exist: the STM8S103F3 has bus 0 (SDA "
            "13, PB5; SCL 12, PB4). (Parameter 'bus')", bus, 0, 0, 0);
    }

    if (!open)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: I2C0 is not open: call I2c.Open(0, 13, 12, "
            "frequency) first.", 0, 0, 0, 0);
    }

    if (address < 0 || address > 0x7f)
    {
        chipsharp_hardware_failure("System.ArgumentException: I2C address {0} does not exist: a 7-bit address is 0 to "
            "127. (Parameter 'address')", address, 0, 0, 0);
    }
}

// Waits for one of the SR1 events of mask, or a refusal (AF); 0 when the time runs out: the bus is reset.
#define REFUSED 0x100u
#define TIME_UP 0u

static u16 wait(u8 mask)
{
    u16 from = chipsharp_clock_count();
    for (;;)
    {
        u8 events = I2C_SR1 & mask;
        if (events != 0)
        {
            return events;
        }

        if ((I2C_SR2 & I2C_SR2_AF_MASK) != 0)
        {
            I2C_SR2 = 0; // rc_w0
            return REFUSED;
        }

        if ((u16)(chipsharp_clock_count() - from) > limit)
        {
            // a device holds the clock low: the controller starts again, its bus free
            I2C_CR2 = I2C_CR2_SWRST_MASK;
            I2C_CR2 = 0;
            set_up();
            continuing = 0;
            return TIME_UP;
        }
    }
}

// The stop after the last byte, and its end (STOP clears once the controller has made it).
static chipsharp_i32 stop(chipsharp_i32 result)
{
    I2C_CR2 |= I2C_CR2_STOP_MASK;
    u16 from = chipsharp_clock_count();
    while ((I2C_CR2 & I2C_CR2_STOP_MASK) != 0 && (u16)(chipsharp_clock_count() - from) <= limit)
    {
    }

    I2C_CR2 &= (u8)~(I2C_CR2_POS_MASK | I2C_CR2_ACK_MASK);
    continuing = 0;
    return result;
}

// A start (repeated when the write before kept the bus) and the address with the direction; 1 once it answered
// (ADDR, not yet cleared), 0 when it did not (the stop is made) or the time ran out.
static chipsharp_i32 begin(chipsharp_i32 address, u8 read)
{
    if (!continuing)
    {
        u16 from = chipsharp_clock_count();
        while ((I2C_SR3 & I2C_SR3_BUSY_MASK) != 0)
        {
            if ((u16)(chipsharp_clock_count() - from) > limit)
            {
                I2C_CR2 = I2C_CR2_SWRST_MASK;
                I2C_CR2 = 0;
                set_up();
                break;
            }
        }
    }

    continuing = 0;
    I2C_CR2 |= I2C_CR2_START_MASK;
    if (wait(I2C_SR1_SB_MASK) == TIME_UP)
    {
        return 0;
    }

    I2C_DR = (u8)((u8)address << 1 | read); // after reading SR1: clears SB
    u16 answer = wait(I2C_SR1_ADDR_MASK);
    if (answer == TIME_UP)
    {
        return 0;
    }

    if (answer == REFUSED)
    {
        return stop(0);
    }

    return 1;
}

chipsharp_i32 ChipSharp_Runtime_I2cWrite(chipsharp_i32 bus, chipsharp_i32 address, const chipsharp_u8 *data,
    chipsharp_i32 count, chipsharp_i32 with_stop)
{
    check_bus(bus, address);
    if (!begin(address, 0))
    {
        return 0;
    }

    (void)I2C_SR3; // after SR1: clears ADDR
    for (chipsharp_i32 i = 0; i < count; i++)
    {
        u16 event = wait(I2C_SR1_TXE_MASK);
        if (event == TIME_UP)
        {
            return 0;
        }

        if (event == REFUSED)
        {
            return stop(0);
        }

        I2C_DR = data[i];
    }

    // the last byte out (none: the address alone, I2c.Probe of a write)
    u16 done = count > 0 ? wait(I2C_SR1_BTF_MASK) : I2C_SR1_BTF_MASK;
    if (done == TIME_UP)
    {
        return 0;
    }

    if (done == REFUSED)
    {
        return stop(0);
    }

    if (with_stop)
    {
        return stop(1);
    }

    continuing = 1;
    return 1;
}

chipsharp_i32 ChipSharp_Runtime_I2cRead(chipsharp_i32 bus, chipsharp_i32 address, chipsharp_u8 *data,
    chipsharp_i32 count)
{
    check_bus(bus, address);
    // two bytes: the controller acknowledges the first and not the second, which POS makes ACK's bit say; more: each but
    // the last
    I2C_CR2 = (u8)((I2C_CR2 & (u8)~(I2C_CR2_POS_MASK | I2C_CR2_ACK_MASK))
        | (count == 2 ? I2C_CR2_POS_MASK | I2C_CR2_ACK_MASK : count > 2 ? I2C_CR2_ACK_MASK : 0u));
    if (!begin(address, 1))
    {
        return 0;
    }

    if (count <= 1)
    {
        // no acknowledgement for the one byte (none is asked for: one is read all the same), and the stop after it
        u8 cc = chipsharp_interrupts_off();
        (void)I2C_SR3;
        I2C_CR2 |= I2C_CR2_STOP_MASK;
        chipsharp_interrupts_back(cc);
        if (wait(I2C_SR1_RXNE_MASK) == TIME_UP)
        {
            return 0;
        }

        u8 value = I2C_DR;
        if (count == 1)
        {
            data[0] = value;
        }

        return stop(1);
    }

    (void)I2C_SR3;
    if (count == 2)
    {
        I2C_CR2 &= (u8)~I2C_CR2_ACK_MASK; // with POS: the second byte's
        if (wait(I2C_SR1_BTF_MASK) == TIME_UP)
        {
            return 0;
        }

        u8 cc = chipsharp_interrupts_off();
        I2C_CR2 |= I2C_CR2_STOP_MASK;
        data[0] = I2C_DR;
        data[1] = I2C_DR;
        chipsharp_interrupts_back(cc);
        return stop(1);
    }

    chipsharp_i32 i = 0;
    for (; i < count - 3; i++)
    {
        if (wait(I2C_SR1_RXNE_MASK) == TIME_UP)
        {
            return 0;
        }

        data[i] = I2C_DR;
    }

    // the last three: byte n-2 in DR and n-1 behind it (BTF); no acknowledgement for byte n, then the stop
    if (wait(I2C_SR1_BTF_MASK) == TIME_UP)
    {
        return 0;
    }

    I2C_CR2 &= (u8)~I2C_CR2_ACK_MASK;
    u8 cc = chipsharp_interrupts_off();
    data[i] = I2C_DR;
    I2C_CR2 |= I2C_CR2_STOP_MASK;
    data[i + 1] = I2C_DR;
    chipsharp_interrupts_back(cc);
    if (wait(I2C_SR1_RXNE_MASK) == TIME_UP)
    {
        return 0;
    }

    data[i + 2] = I2C_DR;
    return stop(1);
}
