// ChipSharp runtime for the Espressif ESP32-C5: its general-purpose SPI controller GP-SPI2, ChipSharp.Hardware.Spi, as a
// controller (master): 8 bits a word, most significant bit first, mode 0 to 3, the chip select the program's own pin.
//
// Freestanding, like esp32c5_runtime.c. The C5's SPI2 (GPSPI2 at 0x60081000, "FSPI" in the pin names) is the one SPI bus
// for programs (SPI0 and SPI1 are the flash's). Its pins go through the GPIO matrix, so SCK, TX (MOSI) and RX (MISO) can
// be any of GPIO0 to GPIO28 (the target's "spi" table says which a program may use); RX may be -1, no pin.
//
//   ChipSharp_Runtime_SpiOpen       Spi.Open: the controller reset, its clock from the crystal at the frequency asked for
//                                   (the nearest at or below, 46875 Hz to 26 MHz with the 48 MHz crystal), the mode, and
//                                   SCK, MOSI and MISO connected (FSPICLK, FSPID, FSPIQ)
//   ChipSharp_Runtime_SpiWrite      Spi.Write: the bytes out, what comes back dropped
//   ChipSharp_Runtime_SpiRead       Spi.Read: the bytes in, zeros out
//   ChipSharp_Runtime_SpiTransfer   Spi.Transfer: the bytes out and in at once
//
// How it works (TRM 29, ESP-IDF's spi_ll.h and spi_hal.c, CPU transfers, no DMA): a "user" transaction of up to 64 bytes
// (the 16 data registers SPI_W0-SPI_W15, byte 0 the lowest byte of W0) in full duplex with no command, address or dummy
// phase, MS_DLEN the number of bits less one; SPI_CMD's UPDATE takes the settings over, USR starts it, and
// SPI_DMA_INT_RAW's TRANS_DONE says it ended (polled; no interrupt). A longer transfer is several transactions in a row.
// Every byte is sent and received (a write drops what comes in, a read sends zeros), so a transfer takes the same time
// whatever it is for.
//
// The clock is the crystal over SPI_CLOCK's CLKDIV_PRE (1 to 16) times CLKCNT_N (2 to 64), or the crystal itself:
// ESP-IDF's spi_ll_master_cal_clock, which rounds to the nearest where this runtime picks the nearest at or below, as the
// contract has it. Above about 26 MHz the GPIO matrix cannot keep up, so the highest clock is 26 MHz (24 MHz with the
// 48 MHz crystal).
//
// A bus, pin, frequency or mode the chip does not have, or a bus used before Spi.Open stops the program with a message
// (chipsharp_hardware_failure, esp32c5_failures.c).

#include "chipsharp_hardware.h"
#include "esp32c5_chip.h"
#include "esp32c5_bus.h"

#define BUS_COUNT 1
#define FREQUENCY_MAX 26000000u
#define BLOCK_BYTES 64u

// GPIO matrix signals of FSPI (ESP-IDF's gpio_sig_map.h): FSPICLK_OUT_IDX 56, FSPIQ_IN_IDX 57 (MISO), FSPID_OUT_IDX 58 (MOSI)
#define SIGNAL_SCK 56u
#define SIGNAL_MISO 57u
#define SIGNAL_MOSI 58u

static unsigned int frequency_of; // the clock the bus runs at, once it is open, else 0
static unsigned int clock_value;  // SPI_CLOCK's value for it
static unsigned int user_value;   // SPI_USER's, with the mode's clock phase
static unsigned int misc_value;   // SPI_MISC's, with the mode's idle level of the clock

// ---- Mistakes in the program ---------------------------------------------------------------------------------------

static void check_bus(chipsharp_i32 bus)
{
    if (bus < 0 || bus >= BUS_COUNT)
    {
        chipsharp_hardware_failure("System.ArgumentException: SPI bus {0} does not exist: the ESP32-C5 has bus 0 (SPI2, "
            "the general-purpose one). (Parameter 'bus')", bus, 0, 0, 0);
    }
}

static void check_pin(chipsharp_i32 pin, int role)
{
    if (pin >= -1 && pin < C5_PIN_COUNT && (pin >= 0 || role == 2))
    {
        return;
    }

    chipsharp_hardware_failure(role == 0
        ? "System.ArgumentException: GPIO {0} is not an SCK pin: SPI2 can use GPIO0 to GPIO28. (Parameter 'sck')"
        : "System.ArgumentException: GPIO {0} is not a TX pin: SPI2 can use GPIO0 to GPIO28. (Parameter 'tx')",
        pin, 0, 0, 0);
}

static void check_open(chipsharp_i32 bus)
{
    check_bus(bus);
    if (frequency_of == 0)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: SPI0 is not open: call Spi.Open(0, sck, tx, rx, "
            "frequency, mode) first.", 0, 0, 0, 0);
    }
}

// ---- The controller ---------------------------------------------------------------------------------------------------

// The clock's registers for the highest frequency at or below hertz, and that frequency; 0 when none (below the lowest).
// From the crystal over pre * n (pre 1 to 16, n 2 to 64: more n, a finer duty cycle, wins a tie).
static unsigned int find_clock(unsigned int hertz, unsigned int *value)
{
    unsigned int source = c5_xtal_hz();
    unsigned int best = 0, best_pre = 0, best_n = 0;
    for (unsigned int n = 2; n <= 64; n++)
    {
        unsigned int pre = (source + n * hertz - 1u) / (n * hertz); // the smallest pre whose clock is not above hertz
        if (pre < 1u)
        {
            pre = 1u;
        }

        if (pre > 16u || source / (pre * n) > hertz)
        {
            continue;
        }

        unsigned int clock = source / (pre * n);
        if (clock >= best)
        {
            best = clock;
            best_pre = pre;
            best_n = n;
        }
    }

    if (best == 0)
    {
        return 0;
    }

    unsigned int high = (128u * best_n + 127u) / 256u; // an even duty cycle: the clock high for half of n
    *value = SPI2_SPI_CLOCK_SPI_CLKCNT_L(best_n - 1u) | SPI2_SPI_CLOCK_SPI_CLKCNT_H(high - 1u)
        | SPI2_SPI_CLOCK_SPI_CLKCNT_N(best_n - 1u) | SPI2_SPI_CLOCK_SPI_CLKDIV_PRE(best_pre - 1u);
    return best;
}

void ChipSharp_Runtime_SpiOpen(chipsharp_i32 bus, chipsharp_i32 sck, chipsharp_i32 tx, chipsharp_i32 rx,
    chipsharp_i32 frequency, chipsharp_i32 mode)
{
    check_bus(bus);
    check_pin(sck, 0);
    check_pin(tx, 1);
    check_pin(rx, 2);
    unsigned int value;
    unsigned int actual = frequency > 0 && (unsigned int)frequency <= FREQUENCY_MAX ? find_clock((unsigned int)frequency, &value) : 0;
    if (actual == 0)
    {
        chipsharp_hardware_failure("System.ArgumentException: An SPI clock of {0} Hz is not one the ESP32-C5 makes: it is "
            "from 46875 to 26000000 Hz with the 48 MHz crystal. (Parameter 'frequency')", frequency, 0, 0, 0);
    }

    if (mode < 0 || mode > 3)
    {
        chipsharp_hardware_failure("System.ArgumentException: SPI mode {0} does not exist: the mode is 0, 1, 2 or 3. "
            "(Parameter 'mode')", mode, 0, 0, 0);
    }

    // the peripheral's clocks (ESP-IDF's spi_ll_enable_bus_clock, spi_ll_reset_register, spi_ll_set_clk_source,
    // spi_ll_clk_source_pre_div and spi_ll_enable_clock): the register bus, a reset, the crystal as the source, no divider
    PCR_SPI2_CONF = PCR_SPI2_CONF_SPI2_CLK_EN_MASK | PCR_SPI2_CONF_SPI2_RST_EN_MASK;
    PCR_SPI2_CONF = PCR_SPI2_CONF_SPI2_CLK_EN_MASK;
    PCR_SPI2_CLKM_CONF = PCR_SPI2_CLKM_CONF_SPI2_CLKM_SEL(0) | PCR_SPI2_CLKM_CONF_SPI2_CLKM_DIV_NUM(0)
        | PCR_SPI2_CLKM_CONF_SPI2_CLKM_EN_MASK;

    frequency_of = actual;
    clock_value = value;
    // CK_OUT_EDGE: the data changes on the second edge (modes 1 and 3 here: CPHA 1); CK_IDLE_EDGE: the clock idles high
    // (modes 2 and 3: CPOL 1) (spi_ll_master_set_mode)
    user_value = SPI2_SPI_USER_SPI_DOUTDIN_MASK | SPI2_SPI_USER_SPI_USR_MOSI_MASK | SPI2_SPI_USER_SPI_USR_MISO_MASK
        | ((mode == 1 || mode == 2) ? SPI2_SPI_USER_SPI_CK_OUT_EDGE_MASK : 0u);
    misc_value = SPI2_SPI_MISC_SPI_CS0_DIS_MASK | SPI2_SPI_MISC_SPI_CS1_DIS_MASK | SPI2_SPI_MISC_SPI_CS2_DIS_MASK
        | SPI2_SPI_MISC_SPI_CS3_DIS_MASK | SPI2_SPI_MISC_SPI_CS4_DIS_MASK | SPI2_SPI_MISC_SPI_CS5_DIS_MASK
        | (mode >= 2 ? SPI2_SPI_MISC_SPI_CK_IDLE_EDGE_MASK : 0u);

    // a controller (SLAVE's MODE 0), CPU transfers (no DMA), MSB first, no command, address or dummy phase
    SPI2_SPI_SLAVE = 0;
    SPI2_SPI_USER1 = 0;
    SPI2_SPI_CTRL = 0;
    SPI2_SPI_DMA_CONF = SPI2_SPI_DMA_CONF_SPI_BUF_AFIFO_RST_MASK | SPI2_SPI_DMA_CONF_SPI_RX_AFIFO_RST_MASK;
    SPI2_SPI_DMA_CONF = 0;
    SPI2_SPI_CLOCK = clock_value;
    SPI2_SPI_USER = user_value;
    SPI2_SPI_MISC = misc_value;
    SPI2_SPI_DMA_INT_ENA = 0;

    // the pins: SCK and MOSI driven (push-pull), MISO read; the idle level of SCK is the mode's (the pin is low until the
    // controller drives it, which is the idle level for modes 0 and 1; modes 2 and 3 raise it at once)
    c5_pad(sck, 0);
    c5_open_drain(sck, 0);
    c5_connect_output(sck, SIGNAL_SCK);
    c5_pad(tx, 0);
    c5_open_drain(tx, 0);
    c5_connect_output(tx, SIGNAL_MOSI);
    if (rx >= 0)
    {
        c5_pad(rx, 0);
        c5_connect_input(rx, SIGNAL_MISO);
    }
}

// One transaction of count bytes (1 to 64): the bytes to send (zeros when send is 0), and the bytes that came in (when
// receive is not 0).
static void transact(const chipsharp_u8 *send, chipsharp_u8 *receive, unsigned int count)
{
    for (unsigned int word = 0; word < (count + 3u) / 4u; word++)
    {
        unsigned int value = 0;
        for (unsigned int i = 0; i < 4u && word * 4u + i < count; i++)
        {
            value |= (send != 0 ? (unsigned int)send[word * 4u + i] : 0u) << (8u * i);
        }

        *(volatile uint32_t *)(__UINTPTR_TYPE__)(SPI2_SPI_W0_ADDRESS + 4u * word) = value;
    }

    SPI2_SPI_MS_DLEN = count * 8u - 1u;
    SPI2_SPI_DMA_INT_CLR = SPI2_SPI_DMA_INT_CLR_SPI_TRANS_DONE_INT_CLR_MASK;
    SPI2_SPI_CMD = SPI2_SPI_CMD_SPI_UPDATE_MASK; // the settings take effect, and the bit clears
    while ((SPI2_SPI_CMD & SPI2_SPI_CMD_SPI_UPDATE_MASK) != 0)
    {
    }

    SPI2_SPI_CMD = SPI2_SPI_CMD_SPI_USR_MASK;
    while ((SPI2_SPI_DMA_INT_RAW & SPI2_SPI_DMA_INT_RAW_SPI_TRANS_DONE_INT_RAW_MASK) == 0)
    {
    }

    if (receive != 0)
    {
        for (unsigned int word = 0; word < (count + 3u) / 4u; word++)
        {
            unsigned int value = *(volatile uint32_t *)(__UINTPTR_TYPE__)(SPI2_SPI_W0_ADDRESS + 4u * word);
            for (unsigned int i = 0; i < 4u && word * 4u + i < count; i++)
            {
                receive[word * 4u + i] = (chipsharp_u8)(value >> (8u * i));
            }
        }
    }
}

static void run(const chipsharp_u8 *send, chipsharp_u8 *receive, chipsharp_i32 count)
{
    for (chipsharp_i32 done = 0; done < count;)
    {
        unsigned int block = (unsigned int)(count - done) < BLOCK_BYTES ? (unsigned int)(count - done) : BLOCK_BYTES;
        transact(send != 0 ? send + done : 0, receive != 0 ? receive + done : 0, block);
        done += (chipsharp_i32)block;
    }
}

void ChipSharp_Runtime_SpiWrite(chipsharp_i32 bus, const chipsharp_u8 *data, chipsharp_i32 count)
{
    check_open(bus);
    run(data, 0, count);
}

void ChipSharp_Runtime_SpiRead(chipsharp_i32 bus, chipsharp_u8 *data, chipsharp_i32 count)
{
    check_open(bus);
    run(0, data, count);
}

void ChipSharp_Runtime_SpiTransfer(chipsharp_i32 bus, const chipsharp_u8 *write, chipsharp_i32 write_count,
    chipsharp_u8 *read, chipsharp_i32 read_count)
{
    check_open(bus);
    if (write_count != read_count)
    {
        chipsharp_hardware_failure("System.ArgumentException: Spi.Transfer sends and receives the same number of bytes: "
            "the data to write has {0} and the buffer to read into {1}. (Parameter 'read')", write_count, read_count, 0, 0);
    }

    run(write, read, write_count);
}
