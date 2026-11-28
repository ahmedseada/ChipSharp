// ChipSharp runtime for the Espressif ESP32-C5: PWM outputs, ChipSharp.Hardware.Pwm, on the LEDC (LED PWM controller).
//
// Freestanding, like esp32c5_runtime.c. The LEDC (0x60007000) has 4 timers and 6 channels, all "low speed" (its registers
// take a new setting at the next period, which is what the contract asks for). A channel makes a PWM signal from a
// timer's count and goes to a pin through the GPIO matrix (the signals LEDC_LS_SIG_OUT0 to 5, numbers 0 to 5), so any of
// GPIO0 to GPIO28 can be an output (the board's "pinFunctions.pwm" says which a program may use); up to 6 outputs at
// once, on up to 4 different frequencies: outputs at the same frequency share a timer.
//
//   ChipSharp_Runtime_PwmOutput    Pwm.Output: a channel and a timer at the frequency (1 to 1000000 Hz), the pin low
//   ChipSharp_Runtime_PwmDuty      Pwm.Duty: the part of each period the output is high, 0 to 1
//   ChipSharp_Runtime_PwmPulse     Pwm.PulseMicroseconds: how long the output is high each period
//   ChipSharp_Runtime_PwmStop      Pwm.Stop: the pin an input again, the channel and (when it was the last) the timer free
//
// A timer counts the crystal (the LEDC's clock source XTAL, 48 MHz) divided by CLK_DIV (a fraction: 10 bits and 8 fractional
// bits) and wraps every 2^DUTY_RES counts: the frequency is the crystal over CLK_DIV * 2^DUTY_RES. The resolution is the
// finest that fits: the highest DUTY_RES, 1 to 20, for which the divider is at least 1 (as ESP-IDF's
// ledc_find_suitable_duty_resolution finds it): 15 bits at 1 kHz, 5 at 1 MHz. A channel's duty is a count of 0 to 2^DUTY_RES
// (2^DUTY_RES is high all the time), its output high from the count HPOINT (0) for that many counts. The setting of a
// channel is the one ESP-IDF's ledc_set_duty does (ledc_duty_config): the duty, its fade parameters for a step of one,
// the number of fade ranges 1, then DUTY_START and PARA_UP, which the channel takes over at the end of the period.
//
// A pin the chip does not have, a frequency it cannot make, a seventh output or a fifth frequency, and a pin used before
// Pwm.Output stop the program with a message (chipsharp_hardware_failure, esp32c5_failures.c).

#include "chipsharp_hardware.h"
#include "esp32c5_chip.h"
#include "esp32c5_bus.h"

#define CHANNELS 6
#define TIMERS 4
#define FREQUENCY_MIN 1
#define FREQUENCY_MAX 1000000
#define MAX_RESOLUTION 20u

// the registers of the LEDC by channel (0x14 apart: CONF0, HPOINT, DUTY, CONF1, DUTY_R) and timer (8 apart: CONF, VALUE)
#define CHANNEL_REG(channel, offset) (*(volatile uint32_t *)(LEDC_CH0_CONF0_ADDRESS + 0x14u * (unsigned int)(channel) + (offset)))
#define CONF0 0x00u
#define HPOINT 0x04u
#define DUTY 0x08u
#define CONF1 0x0cu
#define TIMER_CONF(timer) (*(volatile uint32_t *)(LEDC_TIMER0_CONF_ADDRESS + 8u * (unsigned int)(timer)))
#define GAMMA_CONF(channel) (*(volatile uint32_t *)(LEDC_CH0_GAMMA_CONF_ADDRESS + 4u * (unsigned int)(channel)))
// the gamma RAM: 16 entries of 4 bytes a channel, from 0x60007400 (ESP-IDF's esp32c5.peripherals.ld: LEDC_GAMMA_RAM)
#define GAMMA_ENTRY(channel, entry) (*(volatile uint32_t *)(0x60007400u + 64u * (unsigned int)(channel) + 4u * (entry)))

// A fade range of one step: DUTY_INC 1 (bit 0), DUTY_CYCLE 1 (bits 1-10), SCALE 0 (11-20), DUTY_NUM 1 (21-30)
#define STEP_RANGE (1u | (1u << 1) | (1u << 21))

// the signal of channel n is n (ESP-IDF's gpio_sig_map.h: LEDC_LS_SIG_OUT0_IDX 0 ... 5)

static unsigned int started;           // the LEDC's clocks are on
static signed char channel_pin[CHANNELS];   // the pin a channel drives, or -1 (set by first use)
static unsigned char channel_timer[CHANNELS];
static unsigned int timer_hertz[TIMERS];    // the frequency a timer makes (0: free), the actual one
static unsigned int timer_bits[TIMERS];     // its resolution
static unsigned int timer_users[TIMERS];    // the channels on it

static void check_pin(chipsharp_i32 pin)
{
    if (pin < 0 || pin >= C5_PIN_COUNT)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The ESP32-C5 has no GPIO {0}: its pins are GPIO0 "
            "to GPIO28. (Parameter 'pin')", pin, 0, 0, 0);
    }
}

// The channel that drives pin, after checking that it is a PWM output.
static int channel_of(chipsharp_i32 pin)
{
    check_pin(pin);
    for (int channel = 0; channel < CHANNELS; channel++)
    {
        if (started && channel_pin[channel] == pin && C5_GPIO_OUT_SEL(pin) == (unsigned int)channel)
        {
            return channel;
        }
    }

    chipsharp_hardware_failure("System.InvalidOperationException: GPIO {0} is not a PWM output: call Pwm.Output({0}, "
        "frequency) first.", pin, 0, 0, 0);
}

static void start_ledc(void)
{
    for (int channel = 0; channel < CHANNELS; channel++)
    {
        channel_pin[channel] = -1;
    }

    // the register clock and a reset, the function clock the crystal, the gamma RAM powered (ESP-IDF's ledc_hal_init and
    // ledc_ll_enable_*: PCR_LEDC_PD_CTRL resets with the memory forced down)
    PCR_LEDC_CONF = PCR_LEDC_CONF_LEDC_CLK_EN_MASK | PCR_LEDC_CONF_LEDC_RST_EN_MASK;
    PCR_LEDC_CONF = PCR_LEDC_CONF_LEDC_CLK_EN_MASK;
    PCR_LEDC_PD_CTRL &= ~PCR_LEDC_PD_CTRL_LEDC_MEM_FORCE_PD_MASK;
    PCR_LEDC_SCLK_CONF = PCR_LEDC_SCLK_CONF_LEDC_SCLK_SEL(0) | PCR_LEDC_SCLK_CONF_LEDC_SCLK_EN_MASK;
    LEDC_CONF = LEDC_CONF_CLK_EN_MASK; // the register clock gate kept open: a setting takes effect without a read first
    started = 1;
}

// The highest resolution (1 to 20 bits) at which a divider of at least 1 makes the frequency, and the divider (Q10.8)
// for it: crystal * 256 / (frequency * 2^bits).
static unsigned int find_divider(unsigned int hertz, unsigned int *bits)
{
    unsigned int source = c5_xtal_hz();
    unsigned int resolution = 1;
    while (resolution < MAX_RESOLUTION && (source >> (resolution + 1u)) >= hertz)
    {
        resolution++;
    }

    *bits = resolution;
    return (unsigned int)(((unsigned long long)source << 8) / ((unsigned long long)hertz << resolution));
}

// Sets up a timer for the frequency: reset, the divider and resolution, then the parameters taken over (and the timer let go).
static void start_timer(int timer, unsigned int hertz)
{
    unsigned int bits;
    unsigned int divider = find_divider(hertz, &bits);
    unsigned int value = (bits << LEDC_TIMER0_CONF_TIMER_DUTY_RES_POS) | (divider << LEDC_TIMER0_CONF_CLK_DIV_TIMER_POS);
    TIMER_CONF(timer) = value | LEDC_TIMER0_CONF_TIMER_PAUSE_MASK | LEDC_TIMER0_CONF_TIMER_RST_MASK;
    TIMER_CONF(timer) = value | LEDC_TIMER0_CONF_TIMER_PAUSE_MASK;
    TIMER_CONF(timer) = value | LEDC_TIMER0_CONF_TIMER_PARA_UP_MASK; // not paused: counting
    timer_bits[timer] = bits;
    // the frequency it makes: crystal / (divider / 256 * 2^bits)
    timer_hertz[timer] = (unsigned int)(((unsigned long long)c5_xtal_hz() << 8) / ((unsigned long long)divider << bits));
}

// Gives the channel its duty, in counts of its timer (0 to 2^bits), to start at the next period.
static void set_duty(int channel, unsigned int counts)
{
    CHANNEL_REG(channel, HPOINT) = 0;
    CHANNEL_REG(channel, DUTY) = counts << 4; // 4 fractional bits
    GAMMA_ENTRY(channel, 0) = STEP_RANGE;
    for (unsigned int entry = 1; entry < 16u; entry++)
    {
        GAMMA_ENTRY(channel, entry) = 0; // random data left in the RAM would make the output wrong
    }

    GAMMA_CONF(channel) = 1u;
    CHANNEL_REG(channel, CONF0) |= LEDC_CH0_CONF0_SIG_OUT_EN_CH_MASK;
    CHANNEL_REG(channel, CONF1) |= LEDC_CH0_CONF1_DUTY_START_CH_MASK;
    CHANNEL_REG(channel, CONF0) |= LEDC_CH0_CONF0_PARA_UP_CH_MASK;
}

void ChipSharp_Runtime_PwmStop(chipsharp_i32 pin)
{
    check_pin(pin);
    if (!started)
    {
        return;
    }

    for (int channel = 0; channel < CHANNELS; channel++)
    {
        if (channel_pin[channel] != pin)
        {
            continue;
        }

        unsigned int enabled = c5_interrupts_off();
        // the signal off, idle low (ESP-IDF's ledc_stop), the pin an input as Pin.Input leaves it
        CHANNEL_REG(channel, CONF0) = (CHANNEL_REG(channel, CONF0) & ~(LEDC_CH0_CONF0_SIG_OUT_EN_CH_MASK
            | LEDC_CH0_CONF0_IDLE_LV_CH_MASK)) | LEDC_CH0_CONF0_PARA_UP_CH_MASK;
        channel_pin[channel] = -1;
        int timer = channel_timer[channel];
        if (--timer_users[timer] == 0)
        {
            TIMER_CONF(timer) |= LEDC_TIMER0_CONF_TIMER_PAUSE_MASK;
            timer_hertz[timer] = 0;
        }

        c5_interrupts_back(enabled);
        ChipSharp_Runtime_PinInput(pin);
        return;
    }
}

void ChipSharp_Runtime_PwmOutput(chipsharp_i32 pin, chipsharp_i32 frequency)
{
    check_pin(pin);
    if (frequency < FREQUENCY_MIN || frequency > FREQUENCY_MAX)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The ESP32-C5 cannot make a PWM frequency of {0} "
            "Hz: it makes 1 Hz to 1000000 Hz. (Parameter 'frequency')", frequency, 0, 0, 0);
    }

    ChipSharp_Runtime_PwmStop(pin); // an output on this pin before starts afresh
    unsigned int enabled = c5_interrupts_off();
    if (!started)
    {
        start_ledc();
    }

    unsigned int bits;
    unsigned int divider = find_divider((unsigned int)frequency, &bits);
    unsigned int actual = (unsigned int)(((unsigned long long)c5_xtal_hz() << 8) / ((unsigned long long)divider << bits));
    int timer = -1;
    for (int t = 0; t < TIMERS; t++)
    {
        if (timer_users[t] != 0 && timer_hertz[t] == actual)
        {
            timer = t;
            break;
        }
    }

    if (timer < 0)
    {
        for (int t = 0; t < TIMERS; t++)
        {
            if (timer_users[t] == 0)
            {
                timer = t;
                break;
            }
        }
    }

    int channel = -1;
    for (int c = 0; c < CHANNELS; c++)
    {
        if (channel_pin[c] < 0)
        {
            channel = c;
            break;
        }
    }

    if (timer < 0 || channel < 0)
    {
        c5_interrupts_back(enabled);
        if (channel < 0)
        {
            chipsharp_hardware_failure("System.InvalidOperationException: The ESP32-C5 has 6 PWM outputs (LEDC channels), "
                "and all are in use: stop one with Pwm.Stop before starting GPIO {0}.", pin, 0, 0, 0);
        }

        chipsharp_hardware_failure("System.InvalidOperationException: The ESP32-C5 has 4 PWM timers, one for each "
            "frequency, and all are in use at other frequencies than {0} Hz: give GPIO {1} the frequency of an output "
            "that runs, or stop one with Pwm.Stop first.", frequency, pin, 0, 0);
    }

    if (timer_users[timer] == 0)
    {
        start_timer(timer, (unsigned int)frequency);
    }

    timer_users[timer]++;
    channel_pin[channel] = (signed char)pin;
    channel_timer[channel] = (unsigned char)timer;
    CHANNEL_REG(channel, CONF0) = (unsigned int)timer; // timer_sel, the signal off, idle low
    set_duty(channel, 0);
    c5_pad(pin, 0);
    c5_open_drain(pin, 0);
    c5_connect_output(pin, (unsigned int)channel);
    c5_interrupts_back(enabled);
}

// Sets the duty of pin's output, in counts of its timer, from 0 to 2^bits.
static void level(chipsharp_i32 pin, unsigned int counts)
{
    int channel = channel_of(pin);
    unsigned int enabled = c5_interrupts_off();
    unsigned int full = 1u << timer_bits[channel_timer[channel]];
    set_duty(channel, counts > full ? full : counts);
    c5_interrupts_back(enabled);
}

void ChipSharp_Runtime_PwmDuty(chipsharp_i32 pin, chipsharp_f64 duty)
{
    int channel = channel_of(pin);
    if (!(duty >= 0.0 && duty <= 1.0))
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: A duty cycle is from 0 (always low) to 1 "
            "(always high). (Parameter 'duty')", 0, 0, 0, 0);
    }

    double full = (double)(1u << timer_bits[channel_timer[channel]]);
    level(pin, (unsigned int)(duty * full + 0.5));
}

void ChipSharp_Runtime_PwmPulse(chipsharp_i32 pin, chipsharp_i32 microseconds)
{
    int channel = channel_of(pin);
    if (microseconds < 0)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: A pulse of {0} microseconds is shorter than "
            "none. (Parameter 'microseconds')", microseconds, 0, 0, 0);
    }

    unsigned int timer = channel_timer[channel];
    unsigned int hertz = timer_hertz[timer];
    unsigned int full = 1u << timer_bits[timer];
    // a pulse of a period or more is high all the time; else counts = microseconds * hertz * 2^bits / 1000000, rounded
    // (the product is below 10^6 * 10^6 * 2^20 < 2^63)
    if ((unsigned long long)(unsigned int)microseconds * hertz >= 1000000ull)
    {
        level(pin, full);
        return;
    }

    level(pin, (unsigned int)(((unsigned long long)(unsigned int)microseconds * hertz * full + 500000ull) / 1000000ull));
}
