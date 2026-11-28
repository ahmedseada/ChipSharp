// ChipSharp runtime for the STM8S103F3: ChipSharp.Hardware.Adc, ADC1's inputs on this package: AIN2 on PC4 (pin 20), AIN3
// on PD2 (26), AIN4 on PD3 (27), AIN5 on PD5 (29) and AIN6 on PD6 (30); read in the chip's own 10 bits, 0 to 1023 against
// VDD (the board's 3.3 V: Adc.Volts gives the volts, as on the Pico and the Uno). PD5 and PD6 are UART1's too.
//
// Adc.Input makes the pin an input (no pull-up) with its digital input off (ADC_TDRL: the Schmitt trigger), and powers
// ADC1 up (ADON), at fMASTER / 8, 2 MHz. A reading is one conversion, single mode, right aligned (DRL read first, then
// DRH): 14 ADC clocks, 7 microseconds, with interrupts off, so that a handler never starts one in the middle of another.

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

static u8 inputs; // bit n: AIN n is an input

// The channel (AIN) of an analog pin, else a failure.
static u8 channel_of(chipsharp_i32 pin)
{
    switch (pin)
    {
    case STM8S103F3_AIN2_PIN:
        return 2;
    case STM8S103F3_AIN3_PIN:
        return 3;
    case STM8S103F3_AIN4_PIN:
        return 4;
    case STM8S103F3_AIN5_PIN:
        return 5;
    case STM8S103F3_AIN6_PIN:
        return 6;
    default:
        chipsharp_hardware_failure("System.ArgumentException: Pin {0} is not an analog input: on the STM8S103F3 they are "
            "20 (PC4, AIN2), 26 (PD2, AIN3), 27 (PD3, AIN4), 29 (PD5, AIN5) and 30 (PD6, AIN6). (Parameter 'pin')", pin,
            0, 0, 0);
    }
}

void ChipSharp_Runtime_AdcInput(chipsharp_i32 pin)
{
    u8 channel = channel_of(pin);
    u8 mask;
    volatile u8 *port = chipsharp_pin_port(pin, &mask);
    u8 cc = chipsharp_interrupts_off();
    port[PORT_CR2] &= (u8)~mask;
    port[PORT_CR1] &= (u8)~mask;
    port[PORT_DDR] &= (u8)~mask;
    ADC1_TDRL |= (u8)(1u << channel);
    if (inputs == 0)
    {
        ADC1_CR1 = ADC1_CR1_SPSEL_DIV8;
        ADC1_CR2 = ADC1_CR2_ALIGN_MASK;
        ADC1_CR1 = ADC1_CR1_SPSEL_DIV8 | ADC1_CR1_ADON_MASK; // the first ADON wakes it (7 us before a conversion)
    }

    inputs |= (u8)(1u << channel);
    chipsharp_interrupts_back(cc);
}

chipsharp_i32 ChipSharp_Runtime_AdcRead(chipsharp_i32 pin)
{
    u8 channel = channel_of(pin);
    if ((inputs & (1u << channel)) == 0)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: Pin {0} is not an analog input yet: call "
            "Adc.Input({0}) first.", pin, 0, 0, 0);
    }

    u8 cc = chipsharp_interrupts_off();
    ADC1_CSR = channel;   // EOC cleared, the channel chosen
    ADC1_CR1 |= ADC1_CR1_ADON_MASK; // a second ADON starts the conversion
    while ((ADC1_CSR & ADC1_CSR_EOC_MASK) == 0)
    {
    }

    u8 low = ADC1_DRL;
    u8 high = ADC1_DRH;
    ADC1_CSR = channel;
    chipsharp_interrupts_back(cc);
    return (chipsharp_i32)((u16)high << 8 | low);
}

chipsharp_i32 ChipSharp_Runtime_AdcFullScale(void)
{
    return 1024;
}
