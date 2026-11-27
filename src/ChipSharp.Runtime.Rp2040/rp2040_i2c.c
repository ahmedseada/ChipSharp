// ChipSharp runtime for the Raspberry Pi RP2040: the two I2C controllers, ChipSharp.Hardware.I2c.
//
// Freestanding, like rp2040_runtime.c. The transfers follow pico-sdk's i2c_write_blocking and i2c_read_blocking, with
// a time limit; the program's C# (ChipSharp.Library's I2c) hands each span over as its first byte and its length.
//
//   ChipSharp_Runtime_I2cOpen     I2c.Open: the controller taken out of reset as a controller (master) with 7-bit
//                                 addresses and repeated starts, its clock at the frequency asked for (from clk_sys,
//                                 125 MHz), and its SDA and SCL pins connected to it (function 3) with their pull-ups
//   ChipSharp_Runtime_I2cWrite    I2c.Write, and WriteRead's write, which ends without a stop
//   ChipSharp_Runtime_I2cRead     I2c.Read, Probe, and WriteRead's read, which starts with a repeated start
//
// A transfer returns 1 when the device answered its address and took every byte; 0 when it did not (the controller
// then sends the stop itself), and 0 when the bus did not move for longer than a byte's time limit, e.g. a device
// holding the clock low: the controller is then reset and set up afresh, so that a transfer never waits for ever. A
// bus, pin, frequency or address the chip does not have, or a bus that is not open, stops the program with a message
// on UART0, as a failure does (rp2040_failures.c).

#define REG(address) (*(volatile unsigned int *)(address))

#define ALIAS_SET 0x2000u
#define ALIAS_CLEAR 0x3000u

#define CLK_SYS_HZ 125000000u

#define RESETS_RESET 0x4000c000u
#define RESETS_RESET_DONE 0x4000c008u
#define RESET_I2C0 (1u << 3)

#define TIMER_TIMERAWL 0x40054028u

#define IO_BANK0_GPIO_CTRL(pin) (0x40014004u + 8u * (unsigned int)(pin))
#define PADS_BANK0_GPIO(pin) (0x4001c004u + 4u * (unsigned int)(pin))
#define PAD_PULL_DOWN (1u << 2)
#define PAD_PULL_UP (1u << 3)
#define PAD_INPUT_ENABLE (1u << 6)
#define PAD_OUTPUT_DISABLE (1u << 7)
#define FUNCTION_I2C 3u
#define PIN_COUNT 30

// I2C0 at 0x40044000, I2C1 0x4000 above it; the registers of each (datasheet 4.3.17)
#define I2C_BASE(bus) (0x40044000u + 0x4000u * (unsigned int)(bus))
#define IC_CON 0x00u
#define IC_TAR 0x04u
#define IC_DATA_CMD 0x10u
#define IC_FS_SCL_HCNT 0x1cu
#define IC_FS_SCL_LCNT 0x20u
#define IC_RAW_INTR_STAT 0x34u
#define IC_RX_TL 0x38u
#define IC_TX_TL 0x3cu
#define IC_CLR_INTR 0x40u
#define IC_CLR_TX_ABRT 0x54u
#define IC_CLR_STOP_DET 0x60u
#define IC_ENABLE 0x6cu
#define IC_RXFLR 0x78u
#define IC_SDA_HOLD 0x7cu
#define IC_FS_SPKLEN 0xa0u

#define CON_MASTER (1u << 0)
#define CON_SPEED_FAST (2u << 1)
#define CON_RESTART_EN (1u << 5)
#define CON_SLAVE_DISABLE (1u << 6)
#define CON_TX_EMPTY_CTRL (1u << 8) // TX_EMPTY only once the last byte has left the shift register

#define CMD_READ (1u << 8)
#define CMD_STOP (1u << 9)
#define CMD_RESTART (1u << 10)

#define INTR_TX_EMPTY (1u << 4)
#define INTR_TX_ABRT (1u << 6)
#define INTR_STOP_DET (1u << 9)

#define SDA_HOLD_MASK 0xffffu
#define TAR_MASK 0x3ffu

#define BUS_COUNT 2
#define LOWEST_FREQUENCY 2000
#define HIGHEST_FREQUENCY 1000000
#define HIGHEST_ADDRESS 127

// In rp2040_runtime.c: Console.WriteLine's output, UART0.
void chipsharp_uart0_write(const char *bytes, unsigned int count);

// Each bus's clock in hertz once it is open, else 0; how long a byte may take, in microseconds; and whether the last
// transfer was a write without a stop, so that the next one starts with a repeated start.
static unsigned int frequency_of[BUS_COUNT];
static unsigned int byte_limit[BUS_COUNT];
static unsigned int restart_next[BUS_COUNT];

// ---- Mistakes in the program: a message, as .NET writes an exception nothing catches, and a breakpoint -----------

static unsigned int length(const char *text)
{
    unsigned int count = 0;
    while (text[count] != 0)
    {
        count++;
    }

    return count;
}

static void say(const char *text)
{
    chipsharp_uart0_write(text, length(text));
}

static void say_number(int value)
{
    char digits[11];
    unsigned int left = value < 0 ? 0u - (unsigned int)value : (unsigned int)value;
    unsigned int at = sizeof digits;
    do
    {
        digits[--at] = (char)('0' + left % 10u);
        left /= 10u;
    } while (left != 0);

    if (value < 0)
    {
        digits[--at] = '-';
    }

    chipsharp_uart0_write(digits + at, sizeof digits - at);
}

static void begin(const char *exception)
{
    say("Unhandled exception. System.");
    say(exception);
    say(": ");
}

__attribute__((noreturn)) static void stop(void)
{
    for (;;)
    {
        __asm__ volatile("bkpt #0\n\twfi");
    }
}

// Ends an ArgumentException's message with the parameter's name, as .NET does, and stops.
__attribute__((noreturn)) static void wrong(const char *parameter)
{
    say(" (Parameter '");
    say(parameter);
    say("')\r\n");
    stop();
}

// ---- Buses and pins ----------------------------------------------------------------------------------------------

static void check_bus(int bus)
{
    if (bus < 0 || bus >= BUS_COUNT)
    {
        begin("ArgumentException");
        say("I2C bus ");
        say_number(bus);
        say(" does not exist: the RP2040 has bus 0 and bus 1 (I2C0 and I2C1).");
        wrong("bus");
    }
}

// Function 3 of GPIO n is SDA (n even) or SCL (n odd) of I2C0 or I2C1, by turns: 0 and 1 are I2C0's, 2 and 3 I2C1's.
static int is_pin(int pin, int bus, int scl)
{
    return pin >= 0 && pin < PIN_COUNT && ((pin >> 1) & 1) == bus && (pin & 1) == scl;
}

// "GPIO 0, 4, 8, 12, 16, 20, 24 and 28": the bus's SDA (or SCL) pins.
static void say_pins(int bus, int scl)
{
    int total = 0;
    for (int pin = 0; pin < PIN_COUNT; pin++)
    {
        total += is_pin(pin, bus, scl);
    }

    say("GPIO ");
    int said = 0;
    for (int pin = 0; pin < PIN_COUNT; pin++)
    {
        if (is_pin(pin, bus, scl))
        {
            say(said == 0 ? "" : said == total - 1 ? " and " : ", ");
            say_number(pin);
            said++;
        }
    }
}

static void check_pin(int bus, int pin, int scl)
{
    if (!is_pin(pin, bus, scl))
    {
        const char *role = scl ? "SCL" : "SDA";
        begin("ArgumentException");
        say("GPIO ");
        say_number(pin);
        say(" is not an ");
        say(role);
        say(" pin of I2C");
        say_number(bus);
        say(": its ");
        say(role);
        say(" pins are ");
        say_pins(bus, scl);
        say(".");
        wrong(scl ? "scl" : "sda");
    }
}

// The bus's registers, once the program has opened it.
static unsigned int opened(int bus)
{
    check_bus(bus);
    if (frequency_of[bus] == 0)
    {
        begin("InvalidOperationException");
        say("I2C");
        say_number(bus);
        say(" is not open: call I2c.Open(");
        say_number(bus);
        say(", sda, scl, frequency) first.\r\n");
        stop();
    }

    return I2C_BASE(bus);
}

static void check_address(int address)
{
    if (address < 0 || address > HIGHEST_ADDRESS)
    {
        begin("ArgumentException");
        say("I2C address ");
        say_number(address);
        say(" does not exist: a 7-bit address is from 0 to 127 (0x7F).");
        wrong("address");
    }
}

// Connects the pad to the controller, with its input on, its output not disabled and its pull-up on.
static void connect(int pin)
{
    unsigned int pad = REG(PADS_BANK0_GPIO(pin));
    REG(PADS_BANK0_GPIO(pin)) = (pad & ~(PAD_OUTPUT_DISABLE | PAD_PULL_DOWN)) | PAD_INPUT_ENABLE | PAD_PULL_UP;
    REG(IO_BANK0_GPIO_CTRL(pin)) = FUNCTION_I2C;
}

// Takes the controller through a reset and sets it up for its frequency, as pico-sdk's i2c_init and
// i2c_set_baudrate do: always "fast mode" timing, which serves standard mode too, the clock low 60% of the time.
static void start_controller(int bus)
{
    unsigned int base = I2C_BASE(bus);
    unsigned int block = RESET_I2C0 << bus;
    REG(RESETS_RESET + ALIAS_SET) = block;
    REG(RESETS_RESET + ALIAS_CLEAR) = block;
    while ((REG(RESETS_RESET_DONE) & block) == 0)
    {
    }

    unsigned int frequency = frequency_of[bus];
    unsigned int period = (CLK_SYS_HZ + frequency / 2u) / frequency;
    unsigned int low = period * 3u / 5u;
    unsigned int high = period - low;

    // SDA changes at least 300 ns after SCL falls (120 ns in fast mode plus), as the I2C specification asks
    unsigned int hold = frequency < 1000000u ? CLK_SYS_HZ * 3u / 10000000u + 1u : CLK_SYS_HZ * 3u / 25000000u + 1u;

    REG(base + IC_ENABLE) = 0;
    REG(base + IC_CON) = CON_SPEED_FAST | CON_MASTER | CON_SLAVE_DISABLE | CON_RESTART_EN | CON_TX_EMPTY_CTRL;
    REG(base + IC_TX_TL) = 0;
    REG(base + IC_RX_TL) = 0;
    REG(base + IC_FS_SCL_HCNT) = high;
    REG(base + IC_FS_SCL_LCNT) = low;
    REG(base + IC_FS_SPKLEN) = low < 16u ? 1u : low / 16u;
    REG(base + IC_SDA_HOLD) = (REG(base + IC_SDA_HOLD) & ~SDA_HOLD_MASK) | hold;
    REG(base + IC_ENABLE) = 1;
    restart_next[bus] = 0;
}

void ChipSharp_Runtime_I2cOpen(int bus, int sda, int scl, int frequency)
{
    check_bus(bus);
    check_pin(bus, sda, 0);
    check_pin(bus, scl, 1);
    if (frequency < LOWEST_FREQUENCY || frequency > HIGHEST_FREQUENCY)
    {
        begin("ArgumentException");
        say("An I2C clock of ");
        say_number(frequency);
        say(" Hz is not one the RP2040 makes: it is from 2000 to 1000000 Hz (100000 is standard mode, 400000 fast "
            "mode, 1000000 fast mode plus).");
        wrong("frequency");
    }

    frequency_of[bus] = (unsigned int)frequency;
    // a byte is 9 clocks; a device may hold the clock low for a while (clock stretching): 50 clocks, and 1 ms more
    byte_limit[bus] = 1000u + 50000000u / (unsigned int)frequency;
    start_controller(bus);
    connect(sda);
    connect(scl);
}

// ---- Transfers ---------------------------------------------------------------------------------------------------

static unsigned int now(void)
{
    return REG(TIMER_TIMERAWL);
}

// Waits until one of the bits of the raw interrupt status is set; 0 when the byte's time limit passed first.
static int wait_for(int bus, unsigned int bits)
{
    unsigned int base = I2C_BASE(bus);
    unsigned int start = now();
    while ((REG(base + IC_RAW_INTR_STAT) & bits) == 0)
    {
        if (now() - start > byte_limit[bus])
        {
            return 0;
        }
    }

    return 1;
}

// The bus did not move in time: the controller starts afresh, and the transfer says no.
static int timed_out(int bus)
{
    start_controller(bus);
    return 0;
}

// The device did not answer its address, or refused a byte: the controller aborted the transfer and sends the stop.
static int refused(int bus)
{
    unsigned int base = I2C_BASE(bus);
    (void)REG(base + IC_CLR_TX_ABRT);
    if (!wait_for(bus, INTR_STOP_DET))
    {
        return timed_out(bus);
    }

    (void)REG(base + IC_CLR_STOP_DET);
    restart_next[bus] = 0;
    return 0;
}

// Points the controller at the device. After a write without a stop to the same device the bus is still the
// controller's, and the next byte goes out with a repeated start: the controller stays on then.
static void begin_transfer(int bus, int address)
{
    unsigned int base = I2C_BASE(bus);
    if (!restart_next[bus] || (REG(base + IC_TAR) & TAR_MASK) != (unsigned int)address)
    {
        restart_next[bus] = 0;
        REG(base + IC_ENABLE) = 0;
        REG(base + IC_TAR) = (unsigned int)address;
        REG(base + IC_ENABLE) = 1;
    }

    (void)REG(base + IC_CLR_INTR); // forgets an abort or a stop from before
}

// Ends a transfer that ended with a stop: once the stop is on the bus, no abort came before it.
static int finished(int bus)
{
    unsigned int base = I2C_BASE(bus);
    if (!wait_for(bus, INTR_STOP_DET))
    {
        return timed_out(bus);
    }

    if (REG(base + IC_RAW_INTR_STAT) & INTR_TX_ABRT)
    {
        return refused(bus);
    }

    (void)REG(base + IC_CLR_STOP_DET);
    restart_next[bus] = 0;
    return 1;
}

int ChipSharp_Runtime_I2cWrite(int bus, int address, const unsigned char *data, int count, int stop_after)
{
    unsigned int base = opened(bus);
    check_address(address);
    if (count <= 0)
    {
        return 0;
    }

    begin_transfer(bus, address);
    for (int i = 0; i < count; i++)
    {
        unsigned int command = data[i];
        if (i == 0 && restart_next[bus])
        {
            command |= CMD_RESTART;
        }

        if (i == count - 1 && stop_after)
        {
            command |= CMD_STOP;
        }

        REG(base + IC_DATA_CMD) = command;

        // the byte has left (TX_EMPTY, as CON_TX_EMPTY_CTRL makes it), or the device did not take it
        if (!wait_for(bus, INTR_TX_EMPTY | INTR_TX_ABRT))
        {
            return timed_out(bus);
        }

        if (REG(base + IC_RAW_INTR_STAT) & INTR_TX_ABRT)
        {
            return refused(bus);
        }
    }

    if (stop_after)
    {
        return finished(bus);
    }

    restart_next[bus] = 1;
    return 1;
}

int ChipSharp_Runtime_I2cRead(int bus, int address, unsigned char *data, int count)
{
    unsigned int base = opened(bus);
    check_address(address);
    if (count <= 0)
    {
        return 0;
    }

    begin_transfer(bus, address);
    for (int i = 0; i < count; i++)
    {
        unsigned int command = CMD_READ;
        if (i == 0 && restart_next[bus])
        {
            command |= CMD_RESTART;
        }

        if (i == count - 1)
        {
            command |= CMD_STOP;
        }

        REG(base + IC_DATA_CMD) = command;

        // the byte has come, or the device did not answer its address
        unsigned int start = now();
        while (REG(base + IC_RXFLR) == 0)
        {
            if (REG(base + IC_RAW_INTR_STAT) & INTR_TX_ABRT)
            {
                return refused(bus);
            }

            if (now() - start > byte_limit[bus])
            {
                return timed_out(bus);
            }
        }

        data[i] = (unsigned char)REG(base + IC_DATA_CMD);
    }

    return finished(bus);
}
