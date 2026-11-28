// ChipSharp runtime for the Espressif ESP32-C5: what this chip needs on top of the RISC-V runtime.
//
// Freestanding: no C library, no ESP-IDF, no ROM function, and no header but ChipSharp's hardware contract
// (chipsharp_hardware.h) and the chip's registers (esp32c5_registers.h, which ChipSharp.Chips writes from Espressif's
// SVD), which this file and the others of the esp32c5 runtime implement. The facts and their sources are in
// targets/chip/esp32c5.json; esp32c5_chip.h says how the interrupts are routed.
//
//   chipsharp_chip_init                start-up (called by chipsharp_start before the program, interrupts off): the CPU
//                                      at 160 MHz, SYSTIMER's unit 0 counting and its alarms 0 and 1 ready, no interrupt
//                                      the ROM left routed or enabled, then interrupts on
//   ChipSharp_Runtime_Pin...           ChipSharp.Hardware.Pin: GPIO0-GPIO28 through the GPIO matrix and the IO MUX
//                                      (Output or Input first, then Write, Toggle, Read; other numbers are ignored)
//   ChipSharp_Runtime_Delay...         ChipSharp.Hardware.Timing: the core sleeps (wfi) until SYSTIMER's alarm 0
//   ChipSharp_Runtime_Clock...         the clock (DateTime.Now, Clock.Start) and the uptime (Stopwatch,
//                                      Environment.TickCount): SYSTIMER's 64-bit count, 16 ticks a microsecond
//   ChipSharp_Runtime_Register...      ChipSharp.Hardware.Registers: 32-bit reads and writes of any address
//   chipsharp_c5_sleep, ..._wake       waiting for an interrupt without a race (esp32c5_chip.h)
//
// The CPU clock. The ROM leaves the core on the crystal (48 MHz on the devkit); this runtime runs it at 160 MHz, the
// PLL's PLL_F160M output (the 480 MHz SPLL over 3) with no divider: PCR_SYSCLK_CONF's SOC_CLK_SEL 2, CPU_DIV_NUM 0, taking
// effect when PCR_BUS_CLK_UPDATE is written (ESP-IDF's clk_ll_cpu_clk_src_set and clk_ll_bus_update). 160 MHz, not the
// 240 the chip can reach, because it is the clock the chip's peripherals are specified with in every bus ratio (the
// crystal and PLL_F160M are whole multiples of the 80 MHz the APB bus and the UART use) and takes less power; programs
// that compute and want 240 write PCR_SYSCLK_CONF and call the bus update through Registers. The PLL must be locked
// before the switch: the ROM's boot from flash leaves it so (it does not on an image loaded by a debugger; a runtime
// for that would bring the SPLL up through the analog I2C, as ESP-IDF's rtc_clk does, which is not done here). The
// simulator runs the program as the ROM leaves the chip and switches at once.

#include "chipsharp_hardware.h"
#include "esp32c5_chip.h"

#define PCR_SOC_CLK_PLL_F160M 2u

// The CSRs: mepc (to restart a wait), mintthresh (the CLIC's threshold level)
#define CSR_MEPC "0x341"
#define CSR_MINTTHRESH "0x347"

// ---- Start-up ----------------------------------------------------------------------------------------------------

static void start_cpu_clock(void)
{
    PCR_CPU_FREQ_CONF = (PCR_CPU_FREQ_CONF & ~PCR_CPU_FREQ_CONF_CPU_DIV_NUM_MASK) | 0u; // no divider
    PCR_SYSCLK_CONF = (PCR_SYSCLK_CONF & ~PCR_SYSCLK_CONF_SOC_CLK_SEL_MASK)
        | (PCR_SOC_CLK_PLL_F160M << PCR_SYSCLK_CONF_SOC_CLK_SEL_POS);
    PCR_BUS_CLK_UPDATE = PCR_BUS_CLK_UPDATE_BUS_CLOCK_UPDATE_MASK;
    while ((PCR_BUS_CLK_UPDATE & PCR_BUS_CLK_UPDATE_BUS_CLOCK_UPDATE_MASK) != 0)
    {
    }
}

// The count of unit 0 when the program started, so that the uptime starts at 0 (the ROM has run for a while)
static unsigned long long ticks_at_start;

// SYSTIMER: its bus and function clocks on (the function clock is the crystal over 3, 16 MHz), unit 0 counting (it keeps
// counting while the core is stopped by a debugger: no STALL_EN), alarm 0 (the delays) in the interrupt matrix and the
// CLIC with its interrupt on (it is only ever armed for a delay; alarm 1, the timer event's, is set up when a program
// starts it, esp32c5_interrupts.c).
static void start_systimer(void)
{
    PCR_SYSTIMER_CONF |= PCR_SYSTIMER_CONF_SYSTIMER_CLK_EN_MASK;
    PCR_SYSTIMER_FUNC_CLK_CONF = (PCR_SYSTIMER_FUNC_CLK_CONF & ~PCR_SYSTIMER_FUNC_CLK_CONF_SYSTIMER_FUNC_CLK_SEL_MASK)
        | PCR_SYSTIMER_FUNC_CLK_CONF_SYSTIMER_FUNC_CLK_EN_MASK;
    SYSTIMER_CONF = (SYSTIMER_CONF & ~SYSTIMER_CONF_TIMER_UNIT0_CORE0_STALL_EN_MASK) | SYSTIMER_CONF_CLK_EN_MASK
        | SYSTIMER_CONF_TIMER_UNIT0_WORK_EN_MASK;
    SYSTIMER_TARGET0_CONF = 0; // target mode, compared with unit 0
    SYSTIMER_TARGET1_CONF = 0;
    SYSTIMER_INT_CLR = SYSTIMER_INT_CLR_TARGET0_INT_CLR_MASK | SYSTIMER_INT_CLR_TARGET1_INT_CLR_MASK;
    SYSTIMER_INT_ENA = SYSTIMER_INT_ENA_TARGET0_INT_ENA_MASK;
    c5_route(ESP32C5_IRQ_SYSTIMER_TARGET0, C5_ID_DELAY);
    c5_clic_enable(C5_ID_DELAY);
    ticks_at_start = chipsharp_c5_ticks();
}

// Nothing the ROM left may interrupt before the runtime says so: no source routed, no CLIC interrupt enabled, the
// threshold at the lowest level (every level is above it), the CLINT's timer and software interrupts left off.
static void quiet_interrupts(void)
{
    for (unsigned int source = 0; source < ESP32C5_IRQ_COUNT + 1u; source++)
    {
        C5_MAP(source) = 0;
    }

    for (unsigned int id = 16; id < 48; id++)
    {
        C5_CLIC_BYTE(id, C5_CLIC_IE) = 0;
    }

    __asm__ volatile("csrw " CSR_MINTTHRESH ", zero" : : : "memory");
}

void chipsharp_chip_init(void)
{
    start_cpu_clock();
    quiet_interrupts();
    start_systimer();
    __asm__ volatile("csrsi mstatus, 8" : : : "memory");
}

// ---- Waiting for an interrupt ------------------------------------------------------------------------------------

// A core that checks "is the interrupt I wait for here yet?" and then executes wfi can be interrupted in between: the
// handler runs, and the wfi that follows sleeps until an interrupt that may never come. A RISC-V core has no event
// register (the Cortex-M's WFE) to make that wfi return, so the handler that ends the wait finds out whether it
// interrupted the check or the wfi itself (mepc between the two labels: the wfi has not run) and sets mepc back to the
// check. The check and the wfi are these two instructions and a branch, in assembly so that nothing is put between them.
__asm__(
    "    .section .text.chipsharp_c5_sleep, \"ax\", @progbits\n"
    "    .global chipsharp_c5_sleep\n"
    "    .type chipsharp_c5_sleep, @function\n"
    "    .global chipsharp_c5_sleep_check\n"
    "    .global chipsharp_c5_sleep_wfi\n"
    "    .balign 4\n"
    "chipsharp_c5_sleep:\n"
    "chipsharp_c5_sleep_check:\n"
    "    lw t0, 0(a0)\n"
    "    bnez t0, 1f\n"
    "chipsharp_c5_sleep_wfi:\n"
    "    wfi\n"
    "1:  ret\n"
    "    .size chipsharp_c5_sleep, . - chipsharp_c5_sleep\n");

extern char chipsharp_c5_sleep_check[];
extern char chipsharp_c5_sleep_wfi[];

void chipsharp_c5_wake(volatile unsigned int *woken)
{
    *woken = 1;
    unsigned int pc;
    __asm__ volatile("csrr %0, " CSR_MEPC : "=r"(pc));
    if (pc >= (unsigned int)(__UINTPTR_TYPE__)chipsharp_c5_sleep_check && pc <= (unsigned int)(__UINTPTR_TYPE__)chipsharp_c5_sleep_wfi)
    {
        __asm__ volatile("csrw " CSR_MEPC ", %0" : : "r"((unsigned int)(__UINTPTR_TYPE__)chipsharp_c5_sleep_check));
    }
}

// ---- Pins: the GPIO matrix and the IO MUX ------------------------------------------------------------------------

static int is_pin(int pin)
{
    return pin >= 0 && pin < C5_PIN_COUNT;
}

// Connects the pad to the GPIO block (IO MUX function 1) with its input on, and the GPIO block's output to bit pin of
// GPIO_OUT (the pin is driven where GPIO_ENABLE's bit is set).
static void select_gpio(int pin)
{
    C5_IO_MUX(pin) = (C5_IO_MUX(pin) & ~IO_MUX_GPIO0_MCU_SEL_MASK) | IO_MUX_GPIO0_MCU_SEL(1) | IO_MUX_GPIO0_FUN_IE_MASK;
    C5_GPIO_OUT_SEL(pin) = C5_SIMPLE_GPIO_OUTPUT;
}

// Pin.Output: the GPIO block drives the pin, low until written.
void ChipSharp_Runtime_PinOutput(chipsharp_i32 pin)
{
    if (!is_pin(pin))
    {
        return;
    }

    unsigned int mask = 1u << pin;
    GPIO_ENABLE_W1TC = mask;
    GPIO_OUT_W1TC = mask;
    select_gpio(pin);
    GPIO_ENABLE_W1TS = mask;
}

// Pin.Input: the GPIO block stops driving the pin and no pull is on it (IO MUX FUN_WPU and FUN_WPD cleared, whatever the
// reset left: GPIO4, 12, 15-22, 27 and 28 have a pull-up); Read reads it.
void ChipSharp_Runtime_PinInput(chipsharp_i32 pin)
{
    if (!is_pin(pin))
    {
        return;
    }

    GPIO_ENABLE_W1TC = 1u << pin;
    select_gpio(pin);
    C5_IO_MUX(pin) &= ~(IO_MUX_GPIO0_FUN_WPU_MASK | IO_MUX_GPIO0_FUN_WPD_MASK);
}

// Pin.Input(pin, pull): as PinInput, then the pad's pull-up (1) or pull-down (2); any other number leaves no pull.
void ChipSharp_Runtime_PinInputPull(chipsharp_i32 pin, chipsharp_i32 pull)
{
    ChipSharp_Runtime_PinInput(pin);
    if (!is_pin(pin))
    {
        return;
    }

    if (pull == 1)
    {
        C5_IO_MUX(pin) |= IO_MUX_GPIO0_FUN_WPU_MASK;
    }
    else if (pull == 2)
    {
        C5_IO_MUX(pin) |= IO_MUX_GPIO0_FUN_WPD_MASK;
    }
}

// A bool arrives as an int; anything but 0 is high.
void ChipSharp_Runtime_PinWrite(chipsharp_i32 pin, chipsharp_i32 high)
{
    if (!is_pin(pin))
    {
        return;
    }

    if (high != 0)
    {
        GPIO_OUT_W1TS = 1u << pin;
    }
    else
    {
        GPIO_OUT_W1TC = 1u << pin;
    }
}

// The level goes to the other one: a read and a write of GPIO_OUT, so with interrupts off (a handler may change other pins).
void ChipSharp_Runtime_PinToggle(chipsharp_i32 pin)
{
    if (!is_pin(pin))
    {
        return;
    }

    unsigned int enabled = c5_interrupts_off();
    GPIO_OUT = GPIO_OUT ^ (1u << pin);
    c5_interrupts_back(enabled);
}

// Returns a bool as 0 or 1.
chipsharp_i32 ChipSharp_Runtime_PinRead(chipsharp_i32 pin)
{
    if (!is_pin(pin))
    {
        return 0;
    }

    return (chipsharp_i32)((GPIO_IN >> pin) & 1u);
}

// ---- The count of time -------------------------------------------------------------------------------------------

// SYSTIMER's unit 0, all 52 bits: UPDATE latches the count into VALUE_HI and VALUE_LO (VALID says it did), and the two
// are read with interrupts off, so that a handler's own update never changes them between the halves.
unsigned long long chipsharp_c5_ticks(void)
{
    unsigned int enabled = c5_interrupts_off();
    SYSTIMER_UNIT0_OP = SYSTIMER_UNIT0_OP_TIMER_UNIT0_UPDATE_MASK;
    while ((SYSTIMER_UNIT0_OP & SYSTIMER_UNIT0_OP_TIMER_UNIT0_VALUE_VALID_MASK) == 0)
    {
    }

    unsigned int low = SYSTIMER_UNIT0_VALUE_LO;
    unsigned int high = SYSTIMER_UNIT0_VALUE_HI;
    c5_interrupts_back(enabled);
    return ((unsigned long long)high << 32) | low;
}

// ---- Delays ------------------------------------------------------------------------------------------------------

static volatile unsigned int delay_woken;

// Alarm 0 (the delay's end): the alarm clears, and the wait that was waiting for it ends.
void chipsharp_isr_irq16(void)
{
    SYSTIMER_INT_CLR = SYSTIMER_INT_CLR_TARGET0_INT_CLR_MASK;
    chipsharp_c5_wake(&delay_woken);
}

// Waits until the count reaches ticks: asleep (wfi) until alarm 0, which is armed for it. With interrupts off (the
// program never turns them off, but a stop in the start-up could) the count is polled.
static void sleep_until(unsigned long long target)
{
    unsigned int mstatus;
    __asm__ volatile("csrr %0, mstatus" : "=r"(mstatus));
    if ((mstatus & 8u) != 0)
    {
        delay_woken = 0;
        SYSTIMER_TARGET0_HI = (unsigned int)(target >> 32);
        SYSTIMER_TARGET0_LO = (unsigned int)target;
        c5_alarm_working(SYSTIMER_CONF_TARGET0_WORK_EN_MASK); // a comparator that starts to work takes the target it has
        SYSTIMER_COMP0_LOAD = SYSTIMER_COMP0_LOAD_TIMER_COMP0_LOAD_MASK;
        while (chipsharp_c5_ticks() < target)
        {
            chipsharp_c5_sleep(&delay_woken);
        }
    }
    else
    {
        while (chipsharp_c5_ticks() < target)
        {
        }
    }
}

void ChipSharp_Runtime_DelayMicroseconds(chipsharp_i32 microseconds)
{
    if (microseconds > 0)
    {
        sleep_until(chipsharp_c5_ticks() + (unsigned long long)(unsigned int)microseconds * C5_TICKS_PER_MICROSECOND);
    }
}

void ChipSharp_Runtime_DelayMilliseconds(chipsharp_i32 milliseconds)
{
    if (milliseconds > 0)
    {
        sleep_until(chipsharp_c5_ticks()
            + (unsigned long long)(unsigned int)milliseconds * 1000u * C5_TICKS_PER_MICROSECOND);
    }
}

// ---- The clock and the uptime ------------------------------------------------------------------------------------

// Microseconds since the program started: the count of unit 0, 16 ticks a microsecond.
static unsigned long long uptime_microseconds(void)
{
    return (chipsharp_c5_ticks() - ticks_at_start) / C5_TICKS_PER_MICROSECOND;
}

// The software clock, in DateTime's ticks of 100 ns: the date it was given (Clock.Start), or 1 January 2000 until it is
// given one, and the uptime then. Read and written with interrupts off, so that a handler that starts the clock never
// leaves half of it for Main to read.
static long long clock_ticks = 630822816000000000LL;
static unsigned long long clock_microseconds;

// DateTime.UtcNow, DateTime.Now: the chip has no time zones, so both are the clock's date and the time since.
chipsharp_i64 ChipSharp_Runtime_ClockUtcTicks(void)
{
    unsigned int enabled = c5_interrupts_off();
    long long ticks = clock_ticks;
    unsigned long long since = uptime_microseconds() - clock_microseconds;
    c5_interrupts_back(enabled);
    return ticks + (long long)since * 10;
}

chipsharp_i64 ChipSharp_Runtime_ClockLocalTicks(void)
{
    return ChipSharp_Runtime_ClockUtcTicks();
}

// Clock.Start(date): the clock counts from date's ticks from now on.
void ChipSharp_Runtime_ClockStart(chipsharp_i64 ticks)
{
    unsigned int enabled = c5_interrupts_off();
    clock_ticks = ticks;
    clock_microseconds = uptime_microseconds();
    c5_interrupts_back(enabled);
}

// Stopwatch.GetTimestamp, in microseconds since start-up; Stopwatch.Frequency is 1,000,000.
chipsharp_i64 ChipSharp_Runtime_ClockTimestamp(void)
{
    return (chipsharp_i64)uptime_microseconds();
}

chipsharp_i64 ChipSharp_Runtime_ClockFrequency(void)
{
    return 1000000;
}

// Stopwatch.GetElapsedTime: 10 ticks of 100 ns a microsecond, exactly what .NET's double arithmetic gives.
chipsharp_i64 ChipSharp_Runtime_ClockElapsedTicks(chipsharp_i64 start, chipsharp_i64 end)
{
    return (end - start) * 10;
}

// Environment.TickCount64: milliseconds since start-up.
chipsharp_i64 ChipSharp_Runtime_ClockTickCount64(void)
{
    return (chipsharp_i64)(uptime_microseconds() / 1000u);
}

// ---- Registers ---------------------------------------------------------------------------------------------------

chipsharp_u32 ChipSharp_Runtime_RegisterRead(chipsharp_u32 address)
{
    return *(volatile chipsharp_u32 *)(__UINTPTR_TYPE__)address;
}

void ChipSharp_Runtime_RegisterWrite(chipsharp_u32 address, chipsharp_u32 value)
{
    *(volatile chipsharp_u32 *)(__UINTPTR_TYPE__)address = value;
}
