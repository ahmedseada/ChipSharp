// ChipSharp runtime for the Raspberry Pi RP2040: what this chip needs on top of the ARMv6-M runtime.
//
// Freestanding: no headers, no C library, no pico-sdk. Register addresses and bits are from the RP2040 datasheet;
// the clock set-up follows pico-sdk's clocks_init.
//
//   chipsharp_chip_init                start-up (called by chipsharp_reset before the program): the crystal, the
//                                      system PLL (clk_sys = clk_peri = 125 MHz, clk_ref = 12 MHz), the
//                                      watchdog tick generator (the timer counts microseconds), and GPIO and
//                                      timer taken out of reset
//   ChipSharp_Runtime_Gpio...          ChipSharp.Hardware.Gpio: pins 0-29 through SIO; other pin numbers are ignored
//   ChipSharp_Runtime_Delay...         ChipSharp.Hardware.Delay: sleeps on timer alarm 0 (TIMER_IRQ_0, IRQ 0)
//   ChipSharp_Runtime_Register...      ChipSharp.Hardware.Registers: 32-bit reads and writes of any address
//   ChipSharp_Runtime_WriteLine...     Console.WriteLine: UART0 on GPIO 0 (TX) and 1 (RX), 115200 baud, 8N1, lines
//                                      end with "\r\n" like .NET on Windows; UART0 is set up on the first write

#define REG(address) (*(volatile unsigned int *)(address))

// Every peripheral register has aliases that change only some bits in one write (datasheet 2.1.2).
#define ALIAS_XOR 0x1000u
#define ALIAS_SET 0x2000u
#define ALIAS_CLEAR 0x3000u

// The board: the Pico's crystal, and the clocks this runtime sets up from it.
#define XOSC_HZ 12000000u
#define CLK_SYS_HZ 125000000u
#define CLK_PERI_HZ CLK_SYS_HZ
#define UART_BAUD 115200u

#define RESETS_RESET 0x4000c000u
#define RESETS_RESET_DONE 0x4000c008u
#define RESET_IO_BANK0 (1u << 5)
#define RESET_PADS_BANK0 (1u << 8)
#define RESET_PLL_SYS (1u << 12)
#define RESET_TIMER (1u << 21)
#define RESET_UART0 (1u << 22)

#define CLOCKS_BASE 0x40008000u
#define CLK_REF_CTRL (CLOCKS_BASE + 0x30u)
#define CLK_REF_DIV (CLOCKS_BASE + 0x34u)
#define CLK_REF_SELECTED (CLOCKS_BASE + 0x38u)
#define CLK_SYS_CTRL (CLOCKS_BASE + 0x3cu)
#define CLK_SYS_DIV (CLOCKS_BASE + 0x40u)
#define CLK_SYS_SELECTED (CLOCKS_BASE + 0x44u)
#define CLK_PERI_CTRL (CLOCKS_BASE + 0x48u)
#define CLK_SYS_RESUS_CTRL (CLOCKS_BASE + 0x78u)
#define CLK_REF_SRC_XOSC 2u
#define CLK_SYS_SRC_AUX 1u
#define CLK_SYS_AUXSRC_PLL_SYS (0u << 5)
#define CLK_PERI_AUXSRC_CLK_SYS (0u << 5)
#define CLK_ENABLE (1u << 11)
#define CLK_DIV_1 (1u << 8)

#define XOSC_CTRL 0x40024000u
#define XOSC_STATUS 0x40024004u
#define XOSC_STARTUP 0x4002400cu
#define XOSC_FREQ_RANGE_1_15MHZ 0xaa0u
#define XOSC_ENABLE (0xfabu << 12)
#define XOSC_STABLE (1u << 31)

#define PLL_SYS_CS 0x40028000u
#define PLL_SYS_PWR 0x40028004u
#define PLL_SYS_FBDIV_INT 0x40028008u
#define PLL_SYS_PRIM 0x4002800cu
#define PLL_LOCK (1u << 31)
#define PLL_PWR_PD (1u << 0)
#define PLL_PWR_POSTDIVPD (1u << 3)
#define PLL_PWR_VCOPD (1u << 5)

#define WATCHDOG_TICK 0x4005802cu
#define WATCHDOG_TICK_ENABLE (1u << 9)

#define TIMER_ALARM0 0x40054010u
#define TIMER_ARMED 0x40054020u
#define TIMER_TIMERAWL 0x40054028u
#define TIMER_INTR 0x40054034u
#define TIMER_INTE 0x40054038u
#define TIMER_ALARM0_BIT 1u

#define NVIC_ISER 0xe000e100u
#define TIMER_IRQ_0 0

#define IO_BANK0_GPIO_CTRL(pin) (0x40014004u + 8u * (unsigned int)(pin))
#define PADS_BANK0_GPIO(pin) (0x4001c004u + 4u * (unsigned int)(pin))
#define PAD_INPUT_ENABLE (1u << 6)
#define PAD_OUTPUT_DISABLE (1u << 7)
#define FUNCTION_UART 2u
#define FUNCTION_SIO 5u
#define PIN_COUNT 30

#define SIO_GPIO_IN 0xd0000004u
#define SIO_GPIO_OUT_SET 0xd0000014u
#define SIO_GPIO_OUT_CLR 0xd0000018u
#define SIO_GPIO_OUT_XOR 0xd000001cu
#define SIO_GPIO_OE_SET 0xd0000024u
#define SIO_GPIO_OE_CLR 0xd0000028u

#define UART0_DR 0x40034000u
#define UART0_FR 0x40034018u
#define UART0_IBRD 0x40034024u
#define UART0_FBRD 0x40034028u
#define UART0_LCR_H 0x4003402cu
#define UART0_CR 0x40034030u
#define UART_FR_TXFF (1u << 5)
#define UART_LCR_H_8_BITS (3u << 5)
#define UART_LCR_H_FIFO_ENABLE (1u << 4)
#define UART_CR_ENABLE ((1u << 0) | (1u << 8) | (1u << 9)) // UARTEN, TXE, RXE
#define UART_TX_PIN 0
#define UART_RX_PIN 1

// The baud rate divisor in 1/128ths, rounded as pico-sdk's uart_set_baudrate does; the compiler works it out.
#define UART_DIVISOR_128 (8u * CLK_PERI_HZ / UART_BAUD + 1u)
#define UART_IBRD (UART_DIVISOR_128 >> 7)
#define UART_FBRD ((UART_DIVISOR_128 & 0x7fu) >> 1)

static void unreset(unsigned int blocks)
{
    REG(RESETS_RESET + ALIAS_CLEAR) = blocks;
    while ((REG(RESETS_RESET_DONE) & blocks) != blocks)
    {
    }
}

static void start_crystal(void)
{
    REG(XOSC_CTRL) = XOSC_FREQ_RANGE_1_15MHZ;
    REG(XOSC_STARTUP) = (XOSC_HZ / 1000u + 128u) / 256u; // about 1 ms, in units of 256 crystal cycles
    REG(XOSC_CTRL + ALIAS_SET) = XOSC_ENABLE;
    while ((REG(XOSC_STATUS) & XOSC_STABLE) == 0)
    {
    }
}

// 12 MHz / 1 * 125 = 1500 MHz for the VCO, / 6 / 2 = 125 MHz.
static void start_system_pll(void)
{
    REG(RESETS_RESET + ALIAS_SET) = RESET_PLL_SYS;
    unreset(RESET_PLL_SYS);
    REG(PLL_SYS_CS) = 1;          // REFDIV
    REG(PLL_SYS_FBDIV_INT) = 125;
    REG(PLL_SYS_PWR + ALIAS_CLEAR) = PLL_PWR_PD | PLL_PWR_VCOPD;
    while ((REG(PLL_SYS_CS) & PLL_LOCK) == 0)
    {
    }

    REG(PLL_SYS_PRIM) = (6u << 16) | (2u << 12); // POSTDIV1, POSTDIV2
    REG(PLL_SYS_PWR + ALIAS_CLEAR) = PLL_PWR_POSTDIVPD;
}

static void start_clocks(void)
{
    REG(CLK_SYS_RESUS_CTRL) = 0;
    start_crystal();

    // move clk_sys and clk_ref to their plain sources (clk_ref, the ring oscillator) before touching the PLL
    REG(CLK_SYS_CTRL + ALIAS_CLEAR) = 1u;
    while (REG(CLK_SYS_SELECTED) != 1u)
    {
    }

    REG(CLK_REF_CTRL + ALIAS_CLEAR) = 3u;
    while (REG(CLK_REF_SELECTED) != 1u)
    {
    }

    start_system_pll();

    // clk_ref: the crystal, 12 MHz
    REG(CLK_REF_DIV) = CLK_DIV_1;
    REG(CLK_REF_CTRL) = CLK_REF_SRC_XOSC;
    while ((REG(CLK_REF_SELECTED) & (1u << CLK_REF_SRC_XOSC)) == 0)
    {
    }

    // clk_sys: the system PLL through the aux mux, 125 MHz (the aux mux changes only while clk_ref is selected)
    REG(CLK_SYS_DIV) = CLK_DIV_1;
    REG(CLK_SYS_CTRL) = CLK_SYS_AUXSRC_PLL_SYS;
    REG(CLK_SYS_CTRL) = CLK_SYS_AUXSRC_PLL_SYS | CLK_SYS_SRC_AUX;
    while ((REG(CLK_SYS_SELECTED) & (1u << CLK_SYS_SRC_AUX)) == 0)
    {
    }

    // clk_peri (UART, SPI): clk_sys
    REG(CLK_PERI_CTRL) = CLK_PERI_AUXSRC_CLK_SYS;
    REG(CLK_PERI_CTRL) = CLK_PERI_AUXSRC_CLK_SYS | CLK_ENABLE;
}

void chipsharp_chip_init(void)
{
    // the timer counts one microsecond per watchdog tick: one tick every 12 cycles of clk_ref (the crystal)
    REG(WATCHDOG_TICK) = (XOSC_HZ / 1000000u) | WATCHDOG_TICK_ENABLE;
    start_clocks();
    unreset(RESET_IO_BANK0 | RESET_PADS_BANK0 | RESET_TIMER);
    __asm__ volatile("cpsie i");
}

// ---- GPIO --------------------------------------------------------------------------------------------------------

static int is_pin(int pin)
{
    return pin >= 0 && pin < PIN_COUNT;
}

// Connects the pad to a peripheral (SIO, UART, ...), with its input on and its output not disabled.
static void select_function(int pin, unsigned int function)
{
    unsigned int pad = REG(PADS_BANK0_GPIO(pin));
    REG(PADS_BANK0_GPIO(pin)) = (pad & ~PAD_OUTPUT_DISABLE) | PAD_INPUT_ENABLE;
    REG(IO_BANK0_GPIO_CTRL(pin)) = function;
}

void ChipSharp_Runtime_GpioSetOutput(int pin)
{
    if (!is_pin(pin))
    {
        return;
    }

    unsigned int mask = 1u << pin;
    REG(SIO_GPIO_OE_CLR) = mask;
    REG(SIO_GPIO_OUT_CLR) = mask; // starts low
    select_function(pin, FUNCTION_SIO);
    REG(SIO_GPIO_OE_SET) = mask;
}

void ChipSharp_Runtime_GpioSetInput(int pin)
{
    if (!is_pin(pin))
    {
        return;
    }

    REG(SIO_GPIO_OE_CLR) = 1u << pin;
    select_function(pin, FUNCTION_SIO);
}

// A bool arrives as an int; anything but 0 is high.
void ChipSharp_Runtime_GpioWrite(int pin, int high)
{
    if (!is_pin(pin))
    {
        return;
    }

    if (high != 0)
    {
        REG(SIO_GPIO_OUT_SET) = 1u << pin;
    }
    else
    {
        REG(SIO_GPIO_OUT_CLR) = 1u << pin;
    }
}

void ChipSharp_Runtime_GpioToggle(int pin)
{
    if (!is_pin(pin))
    {
        return;
    }

    REG(SIO_GPIO_OUT_XOR) = 1u << pin;
}

// Returns a bool as 0 or 1.
int ChipSharp_Runtime_GpioRead(int pin)
{
    if (!is_pin(pin))
    {
        return 0;
    }

    return (int)((REG(SIO_GPIO_IN) >> pin) & 1u);
}

// ---- Delay -------------------------------------------------------------------------------------------------------

static volatile unsigned int alarm_fired;

// TIMER_IRQ_0: alarm 0 has fired.
void chipsharp_isr_irq0(void)
{
    REG(TIMER_INTR) = TIMER_ALARM0_BIT; // write 1 to clear
    alarm_fired = 1;
}

// Sleeps (WFE) for at least microseconds microseconds; at most 2^30 at a time.
static void sleep_microseconds(unsigned int microseconds)
{
    // one tick more: the timer may be about to tick when it is read, and the wait must not be shorter
    unsigned int target = REG(TIMER_TIMERAWL) + microseconds + 1u;
    alarm_fired = 0;
    REG(TIMER_INTE + ALIAS_SET) = TIMER_ALARM0_BIT;
    REG(NVIC_ISER) = 1u << TIMER_IRQ_0;
    REG(TIMER_ALARM0) = target; // arms the alarm: it fires when the timer's low 32 bits equal target
    while (!alarm_fired)
    {
        // an alarm armed after its time has passed would fire only when the timer wraps (71 minutes later)
        if ((int)(REG(TIMER_TIMERAWL) - target) >= 0)
        {
            REG(TIMER_ARMED) = TIMER_ALARM0_BIT; // write 1 to disarm
            break;
        }

        // the interrupt's return sets the event register, so an alarm that fires before this WFE wakes it at once
        __asm__ volatile("wfe");
    }
}

void ChipSharp_Runtime_DelayMicroseconds(int microseconds)
{
    unsigned int left = microseconds > 0 ? (unsigned int)microseconds : 0u;
    while (left > 0)
    {
        unsigned int step = left > 0x40000000u ? 0x40000000u : left;
        sleep_microseconds(step);
        left -= step;
    }
}

void ChipSharp_Runtime_DelayMilliseconds(int milliseconds)
{
    unsigned int left = milliseconds > 0 ? (unsigned int)milliseconds : 0u;
    while (left > 0)
    {
        unsigned int step = left > 1000000u ? 1000000u : left;
        sleep_microseconds(step * 1000u);
        left -= step;
    }
}

// ---- Registers ---------------------------------------------------------------------------------------------------

unsigned int ChipSharp_Runtime_RegisterRead(unsigned int address)
{
    return REG(address);
}

void ChipSharp_Runtime_RegisterWrite(unsigned int address, unsigned int value)
{
    REG(address) = value;
}

// ---- Console.WriteLine on UART0 ----------------------------------------------------------------------------------

static unsigned int uart_started;

static void start_uart(void)
{
    unreset(RESET_UART0);
    REG(UART0_IBRD) = UART_IBRD;
    REG(UART0_FBRD) = UART_FBRD;
    REG(UART0_LCR_H) = UART_LCR_H_8_BITS | UART_LCR_H_FIFO_ENABLE; // also latches the divisors
    REG(UART0_CR) = UART_CR_ENABLE;
    select_function(UART_TX_PIN, FUNCTION_UART);
    select_function(UART_RX_PIN, FUNCTION_UART);
    uart_started = 1;
}

static void write_bytes(const char *bytes, unsigned int count)
{
    if (!uart_started)
    {
        start_uart();
    }

    for (unsigned int i = 0; i < count; i++)
    {
        while (REG(UART0_FR) & UART_FR_TXFF)
        {
        }

        REG(UART0_DR) = (unsigned char)bytes[i];
    }
}

// Writes the number in decimal, with a minus sign in front when negative is set, and ends the line.
static void write_line_number(unsigned long long magnitude, int negative)
{
    char text[24]; // "-", up to 20 digits, "\r\n"
    int start = (int)sizeof text;
    text[--start] = '\n';
    text[--start] = '\r';
    do
    {
        text[--start] = (char)('0' + (int)(magnitude % 10));
        magnitude /= 10;
    } while (magnitude != 0);

    if (negative)
    {
        text[--start] = '-';
    }

    write_bytes(text + start, (unsigned int)((int)sizeof text - start));
}

// The magnitude of a negative number, computed in unsigned arithmetic so that the most negative value
// (int.MinValue, long.MinValue) has one too: 0 - (unsigned)value never overflows.
static unsigned long long magnitude_of(long long value)
{
    return value < 0 ? 0ULL - (unsigned long long)value : (unsigned long long)value;
}

void ChipSharp_Runtime_WriteLine(void)
{
    write_bytes("\r\n", 2);
}

void ChipSharp_Runtime_WriteLineInt32(int value)
{
    write_line_number(magnitude_of(value), value < 0);
}

void ChipSharp_Runtime_WriteLineUInt32(unsigned int value)
{
    write_line_number(value, 0);
}

void ChipSharp_Runtime_WriteLineInt64(long long value)
{
    write_line_number(magnitude_of(value), value < 0);
}

void ChipSharp_Runtime_WriteLineUInt64(unsigned long long value)
{
    write_line_number(value, 0);
}

// A bool arrives as an int; like .NET, anything but 0 is True.
void ChipSharp_Runtime_WriteLineBool(int value)
{
    if (value != 0)
    {
        write_bytes("True\r\n", 6);
    }
    else
    {
        write_bytes("False\r\n", 7);
    }
}
