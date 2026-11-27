// ChipSharp runtime for the Raspberry Pi RP2040: PWM outputs (ChipSharp.Hardware.Pwm).
//
// Freestanding, like rp2040_runtime.c. The PWM block has 8 slices, each a 16-bit counter that counts clk_sys (125 MHz)
// through a divider (1 to 255 15/16, in sixteenths) from 0 to TOP and wraps, and two outputs, A and B: an output is
// high while the counter is below its level (CC), so a period is TOP + 1 counts and the duty cycle CC / (TOP + 1). GPIO
// n is output A (n even) or B (n odd) of slice (n / 2) % 8, so GPIO n and n + 16 are the same output.
//
//   ChipSharp_Runtime_PwmOutput        Pwm.Output: the pin's slice at the frequency, the pin's output low
//   ChipSharp_Runtime_PwmDuty          Pwm.Duty: the output's level, as a part of the period (0 to 1)
//   ChipSharp_Runtime_PwmPulse         Pwm.PulseMicroseconds: the output's level, as a time
//   ChipSharp_Runtime_PwmStop          Pwm.Stop: the pin no longer driven; the slice stopped once neither output is used
//
// A frequency is made with the most counts per period that fit (TOP + 1 up to 65535, so that a level of TOP + 1, high
// all the time, fits CC's 16 bits too) and the smallest divider: the finest duty cycle. Levels change at the end of a
// period (CC is double-buffered), so no period is cut short.
//
// A pin the chip does not have, a frequency it cannot make, a second frequency for a slice whose other output is in
// use, an output already driving another pin, and a pin used before Pwm.Output stop the program
// (chipsharp_hardware_failure, rp2040_failures.c) with a message that says what to do.

#define REG(address) (*(volatile unsigned int *)(address))

#define ALIAS_CLEAR 0x3000u

#define CLK_SYS_HZ 125000000u

#define RESETS_RESET 0x4000c000u
#define RESETS_RESET_DONE 0x4000c008u
#define RESET_PWM (1u << 14)

#define IO_BANK0_GPIO_CTRL(pin) (0x40014004u + 8u * (unsigned int)(pin))
#define PADS_BANK0_GPIO(pin) (0x4001c004u + 4u * (unsigned int)(pin))
#define PAD_INPUT_ENABLE (1u << 6)
#define PAD_OUTPUT_DISABLE (1u << 7)
#define FUNCTION_PWM 4u
#define FUNCTION_NONE 0x1fu
#define FUNCTION_MASK 0x1fu
#define PIN_COUNT 30

#define PWM_SLICE(slice) (0x40050000u + 0x14u * (unsigned int)(slice))
#define PWM_CSR 0x00u
#define PWM_DIV 0x04u
#define PWM_CTR 0x08u
#define PWM_CC 0x0cu
#define PWM_TOP 0x10u
#define PWM_CSR_EN (1u << 0)
#define SLICE_COUNT 8

// The frequencies: the divider at most 4095 sixteenths and 65535 counts, down to 2 counts.
#define FREQUENCY_MIN 8
#define FREQUENCY_MAX 62500000
#define COUNTS_MAX 65535u
#define DIVIDER_MAX 4095u

__attribute__((noreturn)) void chipsharp_hardware_failure(const char *text, int n0, int n1, int n2, int n3);

static unsigned int pwm_started;
static int slice_frequency[SLICE_COUNT]; // the frequency each slice runs at; 0 while stopped
static int output_pin[SLICE_COUNT * 2];  // the pin each output (slice * 2 + B) drives, plus one; 0 for none

static unsigned int interrupts_off(void)
{
    unsigned int primask;
    __asm__ volatile("mrs %0, primask\n\tcpsid i" : "=r"(primask)::"memory");
    return primask;
}

static void interrupts_back(unsigned int primask)
{
    __asm__ volatile("msr primask, %0" ::"r"(primask) : "memory");
}

static int slice_of(int pin)
{
    return (pin >> 1) & 7;
}

static int output_of(int pin)
{
    return (slice_of(pin) << 1) | (pin & 1);
}

// True when pin is still the PWM output it was made: Pin.Output, for one, gives the pin to SIO.
static int drives(int pin)
{
    return output_pin[output_of(pin)] == pin + 1 && (REG(IO_BANK0_GPIO_CTRL(pin)) & FUNCTION_MASK) == FUNCTION_PWM;
}

static void check_pin(int pin)
{
    if (pin < 0 || pin >= PIN_COUNT)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The RP2040 has no GPIO {0}: its pins are GPIO 0 "
            "to 29. (Parameter 'pin')", pin, 0, 0, 0);
    }
}

// The slice of a PWM output pin, after checking that it is one.
static int pwm_slice(int pin)
{
    check_pin(pin);
    if (!drives(pin))
    {
        chipsharp_hardware_failure("System.InvalidOperationException: GPIO {0} is not a PWM output: call Pwm.Output({0}, "
            "frequency) first.", pin, 0, 0, 0);
    }

    return slice_of(pin);
}

// Sets the level of pin's output (A: CC's low half, B: its high half), in counts.
static void set_level(int pin, unsigned int level)
{
    unsigned int address = PWM_SLICE(slice_of(pin)) + PWM_CC;
    unsigned int primask = interrupts_off();
    REG(address) = (pin & 1) != 0 ? (REG(address) & 0xffffu) | (level << 16) : (REG(address) & 0xffff0000u) | level;
    interrupts_back(primask);
}

void ChipSharp_Runtime_PwmOutput(int pin, int frequency)
{
    check_pin(pin);
    if (frequency < FREQUENCY_MIN || frequency > FREQUENCY_MAX)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The RP2040 cannot make a PWM frequency of {0} "
            "Hz: it makes 8 Hz to 62500000 Hz. (Parameter 'frequency')", frequency, 0, 0, 0);
    }

    int slice = slice_of(pin);
    int twin = pin < 16 ? pin + 16 : pin - 16; // the same output's other pin
    if (twin < PIN_COUNT && drives(twin))
    {
        chipsharp_hardware_failure("System.InvalidOperationException: GPIO {0} and GPIO {1} are the same PWM output "
            "(slice {2}): GPIO {1} is a PWM output already. Stop GPIO {1} first, or use a pin of another slice.",
            pin, twin, slice, 0);
    }

    int other = pin ^ 1; // the slice's other output, on this pin's side or the twin's
    int neighbour = drives(other) ? other : other + 16 < PIN_COUNT && drives(other + 16) ? other + 16
        : other >= 16 && drives(other - 16) ? other - 16 : -1;
    if (neighbour >= 0 && slice_frequency[slice] != frequency)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: GPIO {0} shares PWM slice {1} with GPIO {2}, which "
            "runs at {3} Hz, and a slice runs at one frequency. Give both the same frequency, stop GPIO {2} first, or "
            "use a pin of another slice.", pin, slice, neighbour, slice_frequency[slice]);
    }

    if (!pwm_started)
    {
        REG(RESETS_RESET + ALIAS_CLEAR) = RESET_PWM;
        while ((REG(RESETS_RESET_DONE) & RESET_PWM) == 0)
        {
        }

        pwm_started = 1;
    }

    unsigned int base = PWM_SLICE(slice);
    if (neighbour < 0)
    {
        // clk_sys counts per period, in sixteenths (2,000,000,000 / frequency: it fits 32 bits), then the smallest
        // divider that makes them fit 65535 counts
        unsigned int sixteenths = (CLK_SYS_HZ * 16u + (unsigned int)frequency / 2u) / (unsigned int)frequency;
        unsigned int divider = (sixteenths + COUNTS_MAX - 1u) / COUNTS_MAX;
        divider = divider < 16u ? 16u : divider;
        unsigned int counts = (sixteenths + divider / 2u) / divider;
        REG(base + PWM_CSR) = 0;
        REG(base + PWM_DIV) = divider; // 8.4 fixed point: the integer part in bits 11-4
        REG(base + PWM_TOP) = counts - 1u;
        REG(base + PWM_CC) = 0;
        REG(base + PWM_CTR) = 0;
        REG(base + PWM_CSR) = PWM_CSR_EN;
        slice_frequency[slice] = frequency;
    }

    set_level(pin, 0);
    output_pin[output_of(pin)] = pin + 1;
    unsigned int pad = REG(PADS_BANK0_GPIO(pin));
    REG(PADS_BANK0_GPIO(pin)) = (pad & ~PAD_OUTPUT_DISABLE) | PAD_INPUT_ENABLE;
    REG(IO_BANK0_GPIO_CTRL(pin)) = FUNCTION_PWM;
}

void ChipSharp_Runtime_PwmDuty(int pin, double duty)
{
    int slice = pwm_slice(pin);
    if (!(duty >= 0.0 && duty <= 1.0))
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: A duty cycle is from 0 (always low) to 1 "
            "(always high). (Parameter 'duty')", 0, 0, 0, 0);
    }

    unsigned int counts = (REG(PWM_SLICE(slice) + PWM_TOP) & 0xffffu) + 1u;
    set_level(pin, (unsigned int)(duty * (double)counts + 0.5));
}

void ChipSharp_Runtime_PwmPulse(int pin, int microseconds)
{
    int slice = pwm_slice(pin);
    if (microseconds < 0)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: A pulse of {0} microseconds is shorter than "
            "none. (Parameter 'microseconds')", microseconds, 0, 0, 0);
    }

    // counts = microseconds * clk_sys / divider: 2000 sixteenths of a count each (125 MHz * 16 / 1000000), so a
    // period is at most 4095 * 65535 / 2000 microseconds (134 ms); a longer pulse is high all the time
    unsigned int base = PWM_SLICE(slice);
    unsigned int counts = (REG(base + PWM_TOP) & 0xffffu) + 1u;
    unsigned int divider = REG(base + PWM_DIV) & 0xfffu;
    unsigned int level = (unsigned int)microseconds > 140000u
        ? counts
        : ((unsigned int)microseconds * 2000u + divider / 2u) / divider;
    set_level(pin, level > counts ? counts : level);
}

void ChipSharp_Runtime_PwmStop(int pin)
{
    check_pin(pin);
    if (!drives(pin))
    {
        return;
    }

    int slice = slice_of(pin);
    set_level(pin, 0);
    output_pin[output_of(pin)] = 0;
    REG(IO_BANK0_GPIO_CTRL(pin)) = FUNCTION_NONE;
    if (output_pin[slice << 1] == 0 && output_pin[(slice << 1) | 1] == 0)
    {
        REG(PWM_SLICE(slice) + PWM_CSR) = 0;
        slice_frequency[slice] = 0;
    }
}
