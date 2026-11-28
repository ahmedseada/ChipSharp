// ChipSharp runtime for the STM8S103F3: ChipSharp.Hardware.Pwm, on TIM2's three channels: PD4 (pin 28, channel 1), PD3
// (27, channel 2) and PA3 (3, channel 3), at one frequency, TIM2's (TIM1 is the clock's, TIM4 has no pin).
//
// TIM2 counts 16 MHz / 2^p (p from 0 to 15) up to its ARR, a period of up to 65536 counts: the smallest p whose period
// holds the frequency's, so the most counts a period, the nearest frequency there is from 1 Hz to 8 MHz (1 kHz is 16000
// counts of 62.5 ns, 50 Hz 20000 counts of 1 us). Each channel is PWM mode 1, high from the period's start while the count
// is below its CCR: the pulse, in counts (a microsecond is 16 counts at p = 0, a count at p = 4); CCR and ARR are
// preloaded, so a new pulse takes effect at the next period. A second pin at another frequency is a mistake while the
// first runs; once nothing uses TIM2, it stops and is free again.

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

#define FREQUENCY_MAX 8000000

// The frequency TIM2 runs at for the pins of in_use (bit n for channel n + 1), 0 when it runs for none; its prescaler.
static chipsharp_i32 frequency;
static u8 prescaler;
static u8 in_use;

// The channel (0-2) of a PWM pin, else a failure.
static u8 channel_of(chipsharp_i32 pin)
{
    if (pin == STM8S103F3_TIM2_CH1_PIN)
    {
        return 0;
    }

    if (pin == STM8S103F3_TIM2_CH2_PIN)
    {
        return 1;
    }

    if (pin != STM8S103F3_TIM2_CH3_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: Pin {0} is not a PWM pin: on the STM8S103F3 they are 28 "
            "(PD4), 27 (PD3) and 3 (PA3), TIM2's channels 1 to 3. (Parameter 'pin')", pin, 0, 0, 0);
    }

    return 2;
}

// The channel's capture/compare mode register and its compare register's high byte (the low byte follows it).
static volatile u8 *mode_register(u8 channel)
{
    return (volatile u8 *)(TIM2_CCMR1_ADDRESS + channel);
}

static volatile u8 *compare_register(u8 channel)
{
    return (volatile u8 *)(TIM2_CCR1H_ADDRESS + 2u * channel);
}

// Turns the channel's output on (CCxE: TIM2 drives the pin) or off.
static void enable(u8 channel, u8 on)
{
    volatile u8 *ccer = channel == 2 ? &TIM2_CCER2 : &TIM2_CCER1;
    u8 mask = channel == 1 ? TIM2_CCER1_CC2E_MASK : TIM2_CCER1_CC1E_MASK; // CC1E and CC3E are bit 0, CC2E bit 4
    *ccer = on ? *ccer | mask : *ccer & (u8)~mask;
}

static void set_compare(u8 channel, u16 counts)
{
    volatile u8 *ccr = compare_register(channel);
    ccr[0] = (u8)(counts >> 8); // the high byte first: TIM2 takes both when the low one is written
    ccr[1] = (u8)counts;
}

void ChipSharp_Runtime_PwmOutput(chipsharp_i32 pin, chipsharp_i32 hertz)
{
    u8 channel = channel_of(pin);
    if (hertz < 1 || hertz > FREQUENCY_MAX)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The STM8S103F3 cannot make a PWM frequency of {0} "
            "Hz: TIM2 makes 1 Hz to 8 MHz. (Parameter 'frequency')", hertz, 0, 0, 0);
    }

    u8 others = (u8)(in_use & ~(1u << channel));
    if (others != 0 && hertz != frequency)
    {
        u8 other = (others & 1u) != 0 ? 0 : (others & 2u) != 0 ? 1 : 2;
        static const u8 pins[] = {STM8S103F3_TIM2_CH1_PIN, STM8S103F3_TIM2_CH2_PIN, STM8S103F3_TIM2_CH3_PIN};
        chipsharp_hardware_failure("System.InvalidOperationException: Pin {0} shares TIM2 with pin {1}, which runs at {2} "
            "Hz: TIM2's pins have one frequency. Give both the same, or stop pin {1} first.", pin, pins[other], frequency,
            0);
    }

    u8 mask;
    volatile u8 *port = chipsharp_pin_port(pin, &mask);
    u8 cc = chipsharp_interrupts_off();
    if (others == 0)
    {
        // counts of 16 MHz / 2^p a period, the fewest p that fit in 65536
        u32 counts = (MASTER_HZ + (u32)hertz / 2u) / (u32)hertz;
        u8 p = 0;
        while (counts > 65536u)
        {
            p++;
            counts = ((MASTER_HZ >> p) + (u32)hertz / 2u) / (u32)hertz;
        }

        TIM2_CR1 = TIM2_CR1_ARPE_MASK;
        TIM2_PSCR = p;
        TIM2_ARRH = (u8)((counts - 1u) >> 8);
        TIM2_ARRL = (u8)(counts - 1u);
        TIM2_EGR = TIM2_EGR_UG_MASK; // loads the prescaler and ARR, the count from 0
        TIM2_CR1 = TIM2_CR1_ARPE_MASK | TIM2_CR1_CEN_MASK;
        frequency = hertz;
        prescaler = p;
    }

    set_compare(channel, 0);
    *mode_register(channel) = TIM2_CCMR1_OCM_PWM1 | TIM2_CCMR1_OCPE_MASK;
    port[PORT_DDR] |= mask; // an output, push-pull, which TIM2 drives from now on
    port[PORT_CR1] |= mask;
    enable(channel, 1);
    in_use |= (u8)(1u << channel);
    chipsharp_interrupts_back(cc);
}

static void check_output(chipsharp_i32 pin, u8 channel)
{
    if ((in_use & (1u << channel)) == 0)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: Pin {0} is not a PWM output: call Pwm.Output({0}, "
            "frequency) first.", pin, 0, 0, 0);
    }
}

// The counts of TIM2's period, ARR + 1.
static u32 period_counts(void)
{
    return ((u32)TIM2_ARRH << 8 | TIM2_ARRL) + 1u;
}

// A pulse as long as the period or longer keeps the pin high (CCR above ARR); 0, low.
void ChipSharp_Runtime_PwmPulse(chipsharp_i32 pin, chipsharp_i32 microseconds)
{
    u8 channel = channel_of(pin);
    check_output(pin, channel);
    if (microseconds < 0)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: A pulse of {0} microseconds is shorter than none: "
            "it is 0 (always low) or more. (Parameter 'microseconds')", microseconds, 0, 0, 0);
    }

    // 16 counts of 2^p a microsecond, rounded; no period is longer than 2^27 microseconds (134 s)
    u32 us = microseconds > 0x8000000l ? 0x8000000ul : (u32)microseconds;
    u32 counts = prescaler >= 4 ? (us + ((1ul << (prescaler - 4)) >> 1)) >> (prescaler - 4) : us << (4 - prescaler);
    u32 period = period_counts();
    set_compare(channel, (u16)(counts >= period ? (period > 0xffffu ? 0xffffu : period) : counts));
}

// The pin lets go first (an input, as after start-up), then TIM2 lets go of it; the last channel stops TIM2.
void ChipSharp_Runtime_PwmStop(chipsharp_i32 pin)
{
    u8 channel = channel_of(pin);
    check_output(pin, channel);
    u8 mask;
    volatile u8 *port = chipsharp_pin_port(pin, &mask);
    u8 cc = chipsharp_interrupts_off();
    port[PORT_CR1] &= (u8)~mask;
    port[PORT_DDR] &= (u8)~mask;
    enable(channel, 0);
    *mode_register(channel) = 0;
    in_use &= (u8)~(1u << channel);
    if (in_use == 0)
    {
        TIM2_CR1 = 0;
        frequency = 0;
    }

    chipsharp_interrupts_back(cc);
}
