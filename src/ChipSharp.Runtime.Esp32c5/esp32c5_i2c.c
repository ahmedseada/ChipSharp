// ChipSharp runtime for the Espressif ESP32-C5: its I2C controller, ChipSharp.Hardware.I2c, as a controller (master).
//
// Freestanding, like esp32c5_runtime.c. The C5 has one I2C controller for programs, bus 0 (I2C0 at 0x60004000; the other,
// LP_I2C, belongs to the low-power core). Its pins go through the GPIO matrix, so SDA and SCL can be any two of GPIO0 to
// GPIO28 (the target's "i2c" table, targets/chip/esp32c5.json, and the board's, say which a program may use).
//
//   ChipSharp_Runtime_I2cOpen     I2c.Open: the controller reset and set up as a controller at the clock asked for (from
//                                 the crystal, 200 to 1000000 Hz), SDA and SCL open-drain with their pull-ups on
//   ChipSharp_Runtime_I2cWrite    I2c.Write, and WriteRead's write, which ends without a stop
//   ChipSharp_Runtime_I2cRead     I2c.Read, Probe, and WriteRead's read, which starts with a repeated start
//
// How the controller works (TRM 27, ESP-IDF's i2c_ll.h and i2c_master.c): a transfer is a list of up to 8 commands in
// I2C0_COMD0-7 (RESTART, WRITE n bytes with the acknowledgement checked, READ n bytes with an acknowledgement level,
// STOP, END) and the bytes to write in the 32-byte send FIFO; I2C0_CTR's TRANS_START runs the list. The controller
// reports in I2C0_INT_RAW (this runtime polls it, no interrupt): TRANS_COMPLETE after a STOP, END_DETECT at an END (the
// controller waits there, the clock held low, for the next list), NACK when a device did not acknowledge, TIME_OUT when
// SCL stayed low longer than I2C0_TO says (a device holding the clock). A longer transfer than a FIFO holds is several
// lists, each ending with END; a write without a stop ends with END too, and the next transfer's RESTART is then the
// repeated start.
//
// The clock: the crystal over the integer divider PCR_I2C_SCLK_CONF's I2C_SCLK_DIV_NUM + 1, and a half period of that many
// cycles in the SCL low and high registers, as ESP-IDF's i2c_ll_master_cal_bus_clk works them out; the frequency is
// the nearest that division gives (a few percent above or below for the round ones, exact for 100000 and 400000 with the
// 48 MHz crystal).
//
// A device that did not answer its address or refused a byte: the controller's state machine is reset, and a STOP sent,
// and the transfer says 0. A device that held the clock too long, or a controller that did not finish within a time
// limit: the controller is reset and set up afresh, so that a transfer never waits for ever. A bus, pin or frequency the
// chip does not have, an address of more than 7 bits, or a bus that is not open stop the program with a message
// (chipsharp_hardware_failure, esp32c5_failures.c).

#include "chipsharp_hardware.h"
#include "esp32c5_chip.h"
#include "esp32c5_bus.h"

#define BUS_COUNT 1
#define FREQUENCY_MIN 200
#define FREQUENCY_MAX 1000000
#define HIGHEST_ADDRESS 127

// GPIO matrix signals of I2CEXT0 (ESP-IDF's gpio_sig_map.h: I2CEXT0_SCL_OUT_IDX 46 and _IN_IDX 46, SDA 47)
#define SIGNAL_SCL 46u
#define SIGNAL_SDA 47u

// The send and receive FIFOs hold 32 bytes each (SOC_I2C_FIFO_LEN)
#define FIFO_SIZE 32u

// The commands (i2c_ll_hw_cmd_t): byte_num 0-7, ack_en 8, ack_exp 9, ack_val 10, op_code 11-13
#define OP_WRITE 1u
#define OP_STOP 2u
#define OP_READ 3u
#define OP_END 4u
#define OP_RESTART 6u
#define COMMAND(op, bytes, ack_check, ack_level) \
    (((unsigned int)(bytes) & 0xffu) | ((ack_check) ? 0x100u : 0u) | ((ack_level) ? 0x400u : 0u) | ((op) << 11))

// Bits of INT_RAW that end a list, or say it went wrong
#define EVENT_END (I2C0_INT_RAW_TRANS_COMPLETE_INT_RAW_MASK | I2C0_INT_RAW_END_DETECT_INT_RAW_MASK)
#define EVENT_ERROR (I2C0_INT_RAW_NACK_INT_RAW_MASK | I2C0_INT_RAW_TIME_OUT_INT_RAW_MASK \
    | I2C0_INT_RAW_ARBITRATION_LOST_INT_RAW_MASK | I2C0_INT_RAW_MST_TXFIFO_UDF_INT_RAW_MASK)
#define ALL_INTERRUPTS 0x7ffffu

static unsigned int frequency_of;  // the bus's clock in hertz once it is open, else 0
static unsigned int period_ticks;  // SYSTIMER ticks (16 a microsecond) of one SCL period at that clock

static unsigned int command_register(unsigned int index)
{
    return I2C0_COMD0_ADDRESS + 4u * index;
}

static void set_command(unsigned int index, unsigned int command)
{
    *(volatile uint32_t *)(__UINTPTR_TYPE__)command_register(index) = command;
}

// ---- Mistakes in the program ---------------------------------------------------------------------------------------

static void check_bus(chipsharp_i32 bus)
{
    if (bus < 0 || bus >= BUS_COUNT)
    {
        chipsharp_hardware_failure("System.ArgumentException: I2C bus {0} does not exist: the ESP32-C5 has bus 0 (I2C0). "
            "(Parameter 'bus')", bus, 0, 0, 0);
    }
}

static void check_pin(chipsharp_i32 pin, int scl)
{
    if (pin < 0 || pin >= C5_PIN_COUNT)
    {
        chipsharp_hardware_failure(scl
            ? "System.ArgumentException: GPIO {0} is not an SCL pin: I2C0 can use GPIO0 to GPIO28. (Parameter 'scl')"
            : "System.ArgumentException: GPIO {0} is not an SDA pin: I2C0 can use GPIO0 to GPIO28. (Parameter 'sda')",
            pin, 0, 0, 0);
    }
}

static void check_open(chipsharp_i32 bus, chipsharp_i32 address)
{
    check_bus(bus);
    if (frequency_of == 0)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: I2C0 is not open: call I2c.Open(0, sda, scl, "
            "frequency) first.", 0, 0, 0, 0);
    }

    if (address < 0 || address > HIGHEST_ADDRESS)
    {
        chipsharp_hardware_failure("System.ArgumentException: I2C address {0} does not exist: a 7-bit address is from 0 to "
            "127 (0x7F). (Parameter 'address')", address, 0, 0, 0);
    }
}

// ---- The controller ---------------------------------------------------------------------------------------------------

static unsigned int bits_of(unsigned int value)
{
    unsigned int bits = 0;
    while (value != 0)
    {
        bits++;
        value >>= 1;
    }

    return bits;
}

// Takes the controller through a reset and sets it up as a controller at frequency_of: the clock, the timing, open-drain
// pins, the FIFOs empty, no interrupt (ESP-IDF's i2c_hal_master_init and i2c_ll_master_set_bus_timing).
static void start_controller(void)
{
    PCR_I2C_CONF = PCR_I2C_CONF_I2C_CLK_EN_MASK | PCR_I2C_CONF_I2C_RST_EN_MASK;
    PCR_I2C_CONF = PCR_I2C_CONF_I2C_CLK_EN_MASK;

    unsigned int source = c5_xtal_hz();
    unsigned int divider = source / (frequency_of * 1024u) + 1u;
    unsigned int clock = source / divider;
    unsigned int half = clock / frequency_of / 2u;
    unsigned int wait_high = frequency_of >= 80000u ? half / 2u - 2u : half / 4u;
    unsigned int high = half - wait_high;
    unsigned int hold = half / 4u;
    unsigned int sample = half / 2u;

    PCR_I2C_SCLK_CONF = PCR_I2C_SCLK_CONF_I2C_SCLK_DIV_NUM(divider - 1u) | PCR_I2C_SCLK_CONF_I2C_SCLK_EN_MASK; // XTAL
    I2C0_SCL_LOW_PERIOD = half - 1u;
    I2C0_SCL_HIGH_PERIOD = high | (wait_high << I2C0_SCL_HIGH_PERIOD_SCL_WAIT_HIGH_PERIOD_POS);
    I2C0_SDA_HOLD = hold - 1u;
    I2C0_SDA_SAMPLE = sample - 1u;
    I2C0_SCL_RSTART_SETUP = half - 1u;
    I2C0_SCL_STOP_SETUP = half - 1u;
    I2C0_SCL_START_HOLD = half - 1u;
    I2C0_SCL_STOP_HOLD = half - 1u;
    // a device may hold the clock for about 10 bus cycles: 2^TIME_OUT_VALUE source cycles, as ESP-IDF sets it
    I2C0_TO = (bits_of(5u * half) + 2u) | I2C0_TO_TIME_OUT_EN_MASK;
    // a controller (MS_MODE), open-drain pins (SDA_FORCE_OUT and SCL_FORCE_OUT off), no arbitration, MSB first, the
    // receiver acknowledging when its FIFO is full
    I2C0_CTR = I2C0_CTR_MS_MODE_MASK;
    I2C0_FIFO_CONF = I2C0_FIFO_CONF_TX_FIFO_RST_MASK | I2C0_FIFO_CONF_RX_FIFO_RST_MASK;
    I2C0_FIFO_CONF = (FIFO_SIZE - 1u) | (0u << 5); // the FIFO's thresholds: not reached before it is full
    I2C0_INT_ENA = 0;
    I2C0_INT_CLR = ALL_INTERRUPTS;
    I2C0_CTR |= I2C0_CTR_CONF_UPGATE_MASK;
}

// The pin is SDA or SCL: the GPIO block's pad with its pull-up on, open-drain, high, and the peripheral's signals.
static void connect(int pin, unsigned int signal)
{
    c5_pad(pin, 1);
    c5_open_drain(pin, 1);
    GPIO_OUT_W1TS = 1u << pin;
    GPIO_ENABLE_W1TS = 1u << pin;
    c5_connect_output(pin, signal);
    c5_connect_input(pin, signal);
}

void ChipSharp_Runtime_I2cOpen(chipsharp_i32 bus, chipsharp_i32 sda, chipsharp_i32 scl, chipsharp_i32 frequency)
{
    check_bus(bus);
    check_pin(sda, 0);
    check_pin(scl, 1);
    if (sda == scl)
    {
        chipsharp_hardware_failure("System.ArgumentException: SDA and SCL cannot be the same pin, GPIO {0}. (Parameter "
            "'scl')", sda, 0, 0, 0);
    }

    if (frequency < FREQUENCY_MIN || frequency > FREQUENCY_MAX)
    {
        chipsharp_hardware_failure("System.ArgumentException: An I2C clock of {0} Hz is not one the ESP32-C5 makes: it is "
            "from 200 to 1000000 Hz (100000 is standard mode, 400000 fast mode, 1000000 fast mode plus). (Parameter "
            "'frequency')", frequency, 0, 0, 0);
    }

    frequency_of = (unsigned int)frequency;
    period_ticks = C5_TICKS_PER_MICROSECOND * 1000000u / frequency_of;
    start_controller();
    connect(sda, SIGNAL_SDA);
    connect(scl, SIGNAL_SCL);
}

// ---- Running a list of commands ------------------------------------------------------------------------------------

// The most a list of the given bytes may take, in SYSTIMER ticks: each byte is 9 clocks, a start and a stop 2 more, a
// stretching device may add as much again, and a millisecond for the rest.
static unsigned long long limit_for(unsigned int bytes)
{
    return (unsigned long long)period_ticks * (2u * (bytes + 2u) * 9u) + 1000u * C5_TICKS_PER_MICROSECOND;
}

// Loads a list into the controller and runs it: INT_RAW cleared, then the controller's registers taken over
// (CONF_UPGATE) and the list started. Returns the INT_RAW bits that ended it: EVENT_END's or EVENT_ERROR's, or 0 when
// the controller did not finish in time.
static unsigned int run(unsigned int bytes)
{
    I2C0_INT_CLR = ALL_INTERRUPTS;
    I2C0_CTR |= I2C0_CTR_CONF_UPGATE_MASK;
    I2C0_CTR |= I2C0_CTR_TRANS_START_MASK;
    unsigned long long deadline = chipsharp_c5_ticks() + limit_for(bytes);
    for (;;)
    {
        unsigned int raw = I2C0_INT_RAW;
        if ((raw & (EVENT_END | EVENT_ERROR)) != 0)
        {
            return raw & (EVENT_END | EVENT_ERROR);
        }

        if (chipsharp_c5_ticks() > deadline)
        {
            return 0;
        }
    }
}

// The controller did not finish in time, or a device held the clock: starts afresh.
static int timed_out(void)
{
    start_controller();
    return 0;
}

// A device did not answer or refused a byte: the state machine is reset, the FIFOs emptied, and a STOP sent so that the
// bus is free.
static int refused(void)
{
    I2C0_CTR |= I2C0_CTR_FSM_RST_MASK;
    I2C0_FIFO_CONF |= I2C0_FIFO_CONF_TX_FIFO_RST_MASK | I2C0_FIFO_CONF_RX_FIFO_RST_MASK;
    I2C0_FIFO_CONF &= ~(I2C0_FIFO_CONF_TX_FIFO_RST_MASK | I2C0_FIFO_CONF_RX_FIFO_RST_MASK);
    set_command(0, COMMAND(OP_STOP, 0, 0, 0));
    unsigned int events = run(0);
    if ((events & I2C0_INT_RAW_TRANS_COMPLETE_INT_RAW_MASK) == 0)
    {
        return timed_out();
    }

    return 0;
}

// What an ended list says about the transfer: 1 to go on, else the transfer's result (0).
static int after(unsigned int events)
{
    if (events == 0 || (events & I2C0_INT_RAW_TIME_OUT_INT_RAW_MASK) != 0)
    {
        return timed_out();
    }

    if ((events & EVENT_ERROR) != 0)
    {
        return refused();
    }

    return 1;
}

chipsharp_i32 ChipSharp_Runtime_I2cWrite(chipsharp_i32 bus, chipsharp_i32 address, const chipsharp_u8 *data,
    chipsharp_i32 count, chipsharp_i32 stop)
{
    check_open(bus, address);
    if (count <= 0)
    {
        return 0;
    }

    chipsharp_i32 sent = 0;
    int first = 1;
    while (sent < count)
    {
        unsigned int room = FIFO_SIZE - (first ? 1u : 0u);
        unsigned int chunk = (unsigned int)(count - sent) < room ? (unsigned int)(count - sent) : room;
        int last = sent + (chipsharp_i32)chunk == count;
        unsigned int command = 0;
        if (first)
        {
            set_command(command++, COMMAND(OP_RESTART, 0, 0, 0));
            I2C0_DATA = ((unsigned int)address << 1) | 0u;
        }

        set_command(command++, COMMAND(OP_WRITE, chunk + (first ? 1u : 0u), 1, 0));
        set_command(command, last && stop ? COMMAND(OP_STOP, 0, 0, 0) : COMMAND(OP_END, 0, 0, 0));
        for (unsigned int i = 0; i < chunk; i++)
        {
            I2C0_DATA = data[sent + (chipsharp_i32)i];
        }

        if (!after(run(chunk + 1u)))
        {
            return 0;
        }

        sent += (chipsharp_i32)chunk;
        first = 0;
    }

    return 1;
}

chipsharp_i32 ChipSharp_Runtime_I2cRead(chipsharp_i32 bus, chipsharp_i32 address, chipsharp_u8 *data, chipsharp_i32 count)
{
    check_open(bus, address);
    if (count <= 0)
    {
        return 0;
    }

    chipsharp_i32 got = 0;
    int first = 1;
    while (got < count)
    {
        unsigned int chunk = (unsigned int)(count - got) < FIFO_SIZE ? (unsigned int)(count - got) : FIFO_SIZE;
        int last = got + (chipsharp_i32)chunk == count;
        unsigned int command = 0;
        if (first)
        {
            set_command(command++, COMMAND(OP_RESTART, 0, 0, 0));
            set_command(command++, COMMAND(OP_WRITE, 1, 1, 0));
            I2C0_DATA = ((unsigned int)address << 1) | 1u;
        }

        // every byte is acknowledged but the last of the whole read, which the controller leaves unacknowledged
        if (last)
        {
            if (chunk > 1)
            {
                set_command(command++, COMMAND(OP_READ, chunk - 1u, 0, 0));
            }

            set_command(command++, COMMAND(OP_READ, 1, 0, 1));
            set_command(command, COMMAND(OP_STOP, 0, 0, 0));
        }
        else
        {
            set_command(command++, COMMAND(OP_READ, chunk, 0, 0));
            set_command(command, COMMAND(OP_END, 0, 0, 0));
        }

        if (!after(run(chunk + 1u)))
        {
            return 0;
        }

        for (unsigned int i = 0; i < chunk; i++)
        {
            data[got + (chipsharp_i32)i] = (chipsharp_u8)(I2C0_DATA & I2C0_DATA_FIFO_RDATA_MASK);
        }

        got += (chipsharp_i32)chunk;
        first = 0;
    }

    return 1;
}
