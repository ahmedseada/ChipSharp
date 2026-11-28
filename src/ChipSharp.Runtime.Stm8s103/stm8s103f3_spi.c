// ChipSharp runtime for the STM8S103F3: ChipSharp.Hardware.Spi, the chip's SPI controller as bus 0: SCK on PC5 (pin 21),
// TX (MOSI) on PC6 (22), RX (MISO) on PC7 (23), as controller, 8 bits a word, most significant bit first; modes 0 to 3, at
// 16 MHz / 2, 4, 8, ..., 256 (8 MHz to 62.5 kHz), the fastest at or below the frequency asked for. A chip select is the
// program's own pin: the controller's NSS is managed in software (SSM, SSI), so no pin of its own makes it a device.
// Each byte goes out once the one before has (TXE), and comes back with what the device sent (RXNE).

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

#define FREQUENCY_MIN 62500

static u8 open;

static void check_bus(chipsharp_i32 bus)
{
    if (bus != 0)
    {
        chipsharp_hardware_failure("System.ArgumentException: SPI bus {0} does not exist: the STM8S103F3 has bus 0 (SCK "
            "21, PC5; TX 22, PC6; RX 23, PC7). (Parameter 'bus')", bus, 0, 0, 0);
    }
}

static void check_open(chipsharp_i32 bus)
{
    check_bus(bus);
    if (!open)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: SPI0 is not open: call Spi.Open(0, 21, 22, 23, "
            "frequency, mode) first.", 0, 0, 0, 0);
    }
}

void ChipSharp_Runtime_SpiOpen(chipsharp_i32 bus, chipsharp_i32 sck, chipsharp_i32 tx, chipsharp_i32 rx,
    chipsharp_i32 hertz, chipsharp_i32 mode)
{
    check_bus(bus);
    if (sck != STM8S103F3_SPI_SCK_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: Pin {0} is not the SCK pin of SPI0: on the STM8S103F3 it is "
            "pin 21 (PC5). (Parameter 'sck')", sck, 0, 0, 0);
    }

    if (tx != STM8S103F3_SPI_MOSI_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: Pin {0} is not the TX pin of SPI0: on the STM8S103F3 it is "
            "pin 22 (PC6, MOSI). (Parameter 'tx')", tx, 0, 0, 0);
    }

    if (rx != STM8S103F3_SPI_MISO_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: Pin {0} is not the RX pin of SPI0: on the STM8S103F3 it is "
            "pin 23 (PC7, MISO). (Parameter 'rx')", rx, 0, 0, 0);
    }

    if (hertz < FREQUENCY_MIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: An SPI clock of {0} Hz is not one the STM8S103F3 makes: it "
            "makes 16 MHz / 2 to 256, 8 MHz to 62500 Hz. (Parameter 'frequency')", hertz, 0, 0, 0);
    }

    if (mode < 0 || mode > 3)
    {
        chipsharp_hardware_failure("System.ArgumentException: SPI mode {0} does not exist: the mode is 0, 1, 2 or 3. "
            "(Parameter 'mode')", mode, 0, 0, 0);
    }

    // BR: 16 MHz / 2^(BR + 1), the first at or below the frequency
    u8 rate = 0;
    while (rate < 7 && (MASTER_HZ >> (rate + 1u)) > (u32)hertz)
    {
        rate++;
    }

    // SCK and TX outputs (push-pull, fast), RX an input, as the controller takes them
    PC_DDR = (u8)((PC_DDR | STM8S103F3_PIN_PC5_MASK | STM8S103F3_PIN_PC6_MASK) & (u8)~STM8S103F3_PIN_PC7_MASK);
    PC_CR1 = (u8)((PC_CR1 | STM8S103F3_PIN_PC5_MASK | STM8S103F3_PIN_PC6_MASK) & (u8)~STM8S103F3_PIN_PC7_MASK);
    PC_CR2 |= (u8)(STM8S103F3_PIN_PC5_MASK | STM8S103F3_PIN_PC6_MASK);
    SPI_CR1 = 0;
    SPI_CR2 = SPI_CR2_SSM_MASK | SPI_CR2_SSI_MASK;
    u8 settings = (u8)(SPI_CR1_MSTR_MASK | (u8)(rate << SPI_CR1_BR_POS) | (mode & 2 ? SPI_CR1_CPOL_MASK : 0u)
        | (mode & 1 ? SPI_CR1_CPHA_MASK : 0u));
    SPI_CR1 = settings;
    SPI_CR1 = settings | SPI_CR1_SPE_MASK;
    open = 1;
}

// One byte each way.
static u8 exchange(u8 value)
{
    while ((SPI_SR & SPI_SR_TXE_MASK) == 0)
    {
    }

    SPI_DR = value;
    while ((SPI_SR & SPI_SR_RXNE_MASK) == 0)
    {
    }

    return SPI_DR;
}

// The last byte has gone out (the bus is idle) before the program's chip select may go up.
static void finish(void)
{
    while ((SPI_SR & SPI_SR_BSY_MASK) != 0)
    {
    }
}

void ChipSharp_Runtime_SpiWrite(chipsharp_i32 bus, const chipsharp_u8 *data, chipsharp_i32 count)
{
    check_open(bus);
    for (chipsharp_i32 i = 0; i < count; i++)
    {
        exchange(data[i]);
    }

    finish();
}

void ChipSharp_Runtime_SpiRead(chipsharp_i32 bus, chipsharp_u8 *data, chipsharp_i32 count)
{
    check_open(bus);
    for (chipsharp_i32 i = 0; i < count; i++)
    {
        data[i] = exchange(0);
    }

    finish();
}

void ChipSharp_Runtime_SpiTransfer(chipsharp_i32 bus, const chipsharp_u8 *write, chipsharp_i32 write_count,
    chipsharp_u8 *read, chipsharp_i32 read_count)
{
    check_open(bus);
    if (write_count != read_count)
    {
        chipsharp_hardware_failure("System.ArgumentException: Spi.Transfer receives a byte for each byte it sends: it "
            "sends {0} and has room for {1}. Give it spans of the same length. (Parameter 'read')", write_count,
            read_count, 0, 0);
    }

    for (chipsharp_i32 i = 0; i < write_count; i++)
    {
        read[i] = exchange(write[i]);
    }

    finish();
}
