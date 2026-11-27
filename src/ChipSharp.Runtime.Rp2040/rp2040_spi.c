// ChipSharp runtime for the Raspberry Pi RP2040: the two SPI controllers (ARM PL022), ChipSharp.Hardware.Spi.
//
// Freestanding, like rp2040_runtime.c. The set-up and the transfers follow pico-sdk's spi_init, spi_set_baudrate and
// spi_write_read_blocking; the program's C# (ChipSharp.Library's Spi) hands each span over as its first byte and its
// length.
//
//   ChipSharp_Runtime_SpiOpen       Spi.Open: the controller taken out of reset as the bus's controller (master), 8-bit
//                                   frames, most significant bit first, in the SPI mode asked for, its clock the
//                                   nearest clk_peri (125 MHz) / (an even prescale of 2 to 254 x a divisor of 1 to
//                                   256) at or below the frequency asked for; its SCK, TX and RX pins connected to it
//                                   (function 1). Chip select is the program's own pin.
//   ChipSharp_Runtime_SpiWrite      Spi.Write: sends, and drops what comes back
//   ChipSharp_Runtime_SpiRead       Spi.Read: receives, sending zeros
//   ChipSharp_Runtime_SpiTransfer   Spi.Transfer: both, as many bytes each way
//
// Each returns when the last byte has come back, that is once the last bit has left, so that the program may drive its
// chip select (or a shift register's latch) high right after. No more than the receive FIFO's 8 bytes are on their way
// at once, so that none is lost while an interrupt handler runs. A bus, pin, frequency or mode the chip does not have,
// or a bus that is not open, stops the program with a message on UART0, as a failure does (rp2040_failures.c).

#define REG(address) (*(volatile unsigned int *)(address))

#define ALIAS_SET 0x2000u
#define ALIAS_CLEAR 0x3000u

#define CLK_PERI_HZ 125000000u

#define RESETS_RESET 0x4000c000u
#define RESETS_RESET_DONE 0x4000c008u
#define RESET_SPI0 (1u << 16)

#define IO_BANK0_GPIO_CTRL(pin) (0x40014004u + 8u * (unsigned int)(pin))
#define PADS_BANK0_GPIO(pin) (0x4001c004u + 4u * (unsigned int)(pin))
#define PAD_INPUT_ENABLE (1u << 6)
#define PAD_OUTPUT_DISABLE (1u << 7)
#define FUNCTION_SPI 1u
#define PIN_COUNT 30
#define NO_PIN (-1)

// SPI0 at 0x4003c000, SPI1 0x4000 above it; the registers of each (datasheet 4.4.4)
#define SPI_BASE(bus) (0x4003c000u + 0x4000u * (unsigned int)(bus))
#define SSPCR0 0x000u
#define SSPCR1 0x004u
#define SSPDR 0x008u
#define SSPSR 0x00cu
#define SSPCPSR 0x010u
#define SSPICR 0x020u

#define CR0_8_BITS 7u          // DSS: frames of 8 bits; FRF 0: Motorola SPI
#define CR0_CPOL (1u << 6)     // SPO: the clock high when idle
#define CR0_CPHA (1u << 7)     // SPH: bits read on the clock's second edge
#define CR0_DIVISOR_SHIFT 8    // SCR: the divisor less 1
#define CR1_ENABLE (1u << 1)   // SSE; MS 0: the controller
#define SR_TX_NOT_FULL (1u << 1)
#define SR_RX_NOT_EMPTY (1u << 2)
#define ICR_RX_OVERRUN 1u
#define FIFO_DEPTH 8u

#define BUS_COUNT 2
#define LOWEST_FREQUENCY 2000
#define HIGHEST_FREQUENCY 62500000

// The roles of a pin in function 1, GPIO number modulo 4 (CSn, the controller's own chip select, is not used).
#define ROLE_RX 0
#define ROLE_SCK 2
#define ROLE_TX 3

// In rp2040_runtime.c: Console.WriteLine's output, UART0.
void chipsharp_uart0_write(const char *bytes, unsigned int count);

// Whether each bus is open.
static unsigned int is_open[BUS_COUNT];

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
        say("SPI bus ");
        say_number(bus);
        say(" does not exist: the RP2040 has bus 0 and bus 1 (SPI0 and SPI1).");
        wrong("bus");
    }
}

// Function 1 of GPIO 0-7 and 16-23 is SPI0's, of GPIO 8-15 and 24-29 SPI1's: RX, CSn, SCK and TX by turns.
static int is_pin(int pin, int bus, int role)
{
    return pin >= 0 && pin < PIN_COUNT && ((pin >> 3) & 1) == bus && (pin & 3) == role;
}

// "GPIO 2, 6, 18 and 22": the bus's pins of a role.
static void say_pins(int bus, int role)
{
    int total = 0;
    for (int pin = 0; pin < PIN_COUNT; pin++)
    {
        total += is_pin(pin, bus, role);
    }

    say("GPIO ");
    int said = 0;
    for (int pin = 0; pin < PIN_COUNT; pin++)
    {
        if (is_pin(pin, bus, role))
        {
            say(said == 0 ? "" : said == total - 1 ? " and " : ", ");
            say_number(pin);
            said++;
        }
    }
}

// A pin of the role, or (when it may be left out) -1 for none.
static void check_pin(int bus, int pin, int role, const char *name, const char *parameter, int optional)
{
    if (!is_pin(pin, bus, role) && !(optional && pin == NO_PIN))
    {
        begin("ArgumentException");
        say("GPIO ");
        say_number(pin);
        say(role == ROLE_TX ? " is not a " : " is not an ");
        say(name);
        say(" pin of SPI");
        say_number(bus);
        say(": its ");
        say(name);
        say(" pins are ");
        say_pins(bus, role);
        say(optional ? " (-1 for none)." : ".");
        wrong(parameter);
    }
}

// The bus's registers, once the program has opened it.
static unsigned int opened(int bus)
{
    check_bus(bus);
    if (!is_open[bus])
    {
        begin("InvalidOperationException");
        say("SPI");
        say_number(bus);
        say(" is not open: call Spi.Open(");
        say_number(bus);
        say(", sck, tx, rx, frequency, mode) first.\r\n");
        stop();
    }

    return SPI_BASE(bus);
}

// Connects the pad to the controller, with its input on and its output not disabled; -1 is no pin.
static void connect(int pin)
{
    if (pin == NO_PIN)
    {
        return;
    }

    unsigned int pad = REG(PADS_BANK0_GPIO(pin));
    REG(PADS_BANK0_GPIO(pin)) = (pad & ~PAD_OUTPUT_DISABLE) | PAD_INPUT_ENABLE;
    REG(IO_BANK0_GPIO_CTRL(pin)) = FUNCTION_SPI;
}

void ChipSharp_Runtime_SpiOpen(int bus, int sck, int tx, int rx, int frequency, int mode)
{
    check_bus(bus);
    check_pin(bus, sck, ROLE_SCK, "SCK", "sck", 0);
    check_pin(bus, tx, ROLE_TX, "TX", "tx", 1);
    check_pin(bus, rx, ROLE_RX, "RX", "rx", 1);
    if (frequency < LOWEST_FREQUENCY || frequency > HIGHEST_FREQUENCY)
    {
        begin("ArgumentException");
        say("An SPI clock of ");
        say_number(frequency);
        say(" Hz is not one the RP2040 makes: it is from 2000 to 62500000 Hz.");
        wrong("frequency");
    }

    if (mode < 0 || mode > 3)
    {
        begin("ArgumentException");
        say("SPI mode ");
        say_number(mode);
        say(" does not exist: the mode is 0, 1, 2 or 3.");
        wrong("mode");
    }

    // as pico-sdk's spi_set_baudrate finds them, but worked out rather than searched for: the smallest even prescale
    // with clk_peri < prescale * 256 * frequency, then the largest divisor (1 to 256) with
    // clk_peri / (prescale * (divisor - 1)) > frequency, in whole numbers as it divides
    unsigned int wanted = (unsigned int)frequency;
    unsigned int prescale = CLK_PERI_HZ / 256u / wanted + 1u;
    prescale += prescale & 1u;
    unsigned int divisor = CLK_PERI_HZ / (prescale * (wanted + 1u)) + 1u;
    if (divisor > 256u)
    {
        divisor = 256u;
    }

    unsigned int base = SPI_BASE(bus);
    unsigned int block = RESET_SPI0 << bus;
    REG(RESETS_RESET + ALIAS_SET) = block;
    REG(RESETS_RESET + ALIAS_CLEAR) = block;
    while ((REG(RESETS_RESET_DONE) & block) == 0)
    {
    }

    REG(base + SSPCR1) = 0;
    REG(base + SSPCPSR) = prescale;
    REG(base + SSPCR0) = ((divisor - 1u) << CR0_DIVISOR_SHIFT) | (mode & 1 ? CR0_CPHA : 0u)
        | (mode & 2 ? CR0_CPOL : 0u) | CR0_8_BITS;
    REG(base + SSPCR1) = CR1_ENABLE;
    connect(sck);
    connect(tx);
    connect(rx);
    is_open[bus] = 1;
}

// ---- Transfers ---------------------------------------------------------------------------------------------------

// Sends count bytes, from write (zeros when it is null), and receives as many, into read (dropped when it is null).
// Every byte sent brings one back, so when the last has come the bus is idle. read may be write: a byte is read
// into its place only after it has been sent.
static void transfer(int bus, const unsigned char *write, unsigned char *read, unsigned int count)
{
    unsigned int base = opened(bus);
    while (REG(base + SSPSR) & SR_RX_NOT_EMPTY)
    {
        (void)REG(base + SSPDR);
    }

    unsigned int sent = 0;
    unsigned int received = 0;
    while (received < count)
    {
        if (sent < count && sent - received < FIFO_DEPTH && (REG(base + SSPSR) & SR_TX_NOT_FULL))
        {
            REG(base + SSPDR) = write != 0 ? write[sent] : 0u;
            sent++;
        }

        if (REG(base + SSPSR) & SR_RX_NOT_EMPTY)
        {
            unsigned char value = (unsigned char)REG(base + SSPDR);
            if (read != 0)
            {
                read[received] = value;
            }

            received++;
        }
    }

    REG(base + SSPICR) = ICR_RX_OVERRUN;
}

void ChipSharp_Runtime_SpiWrite(int bus, const unsigned char *data, int count)
{
    transfer(bus, data, 0, count > 0 ? (unsigned int)count : 0u);
}

void ChipSharp_Runtime_SpiRead(int bus, unsigned char *data, int count)
{
    transfer(bus, 0, data, count > 0 ? (unsigned int)count : 0u);
}

void ChipSharp_Runtime_SpiTransfer(int bus, const unsigned char *write, int write_count, unsigned char *read,
    int read_count)
{
    opened(bus);
    if (write_count != read_count)
    {
        begin("ArgumentException");
        say("Spi.Transfer receives a byte for each byte it sends: write has ");
        say_number(write_count);
        say(" bytes and read ");
        say_number(read_count);
        say(".");
        wrong("read");
    }

    transfer(bus, write, read, write_count > 0 ? (unsigned int)write_count : 0u);
}
