// ChipSharp runtime for the Raspberry Pi RP2040: the analog inputs (ChipSharp.Hardware.Adc).
//
// Freestanding, like rp2040_runtime.c. The ADC has five inputs: 0 to 3 on GPIO 26 to 29, 4 the temperature sensor. It
// converts one at a time, in 96 cycles of clk_adc: 2 microseconds at 48 MHz, which the USB PLL makes from the crystal
// (12 MHz * 40 / 5 / 2), as pico-sdk sets it up; the runtime starts both with the first analog input.
//
//   ChipSharp_Runtime_AdcInput         Adc.Input: a pin of GPIO 26-29 made an analog input (no digital input, no
//                                      output, no pull-up or pull-down)
//   ChipSharp_Runtime_AdcRead          Adc.Read and Volts: one conversion of the pin's input, 0 to 4095
//   ChipSharp_Runtime_AdcTemperature   Adc.Temperature: one conversion of the temperature sensor's input, in degrees
//                                      Celsius by the datasheet's formula (4.9.5): the sensor gives 0.706 V at 27 C,
//                                      1.721 mV less a degree
//   ChipSharp_Runtime_AdcReference     the voltage that reads as 4096: the Pico's 3.3 V (ADC_VREF is its 3.3 V supply,
//                                      through a filter)
//   ChipSharp_Runtime_AdcFullScale     4096, for Adc.Volts: reading * 3.3 / 4096
//
// A pin that is not an analog input, or one read before Adc.Input, stops the program (chipsharp_hardware_failure,
// rp2040_failures.c) with a message that says what to do.

#include "chipsharp_hardware.h"

#define REG(address) (*(volatile unsigned int *)(address))

#define ALIAS_SET 0x2000u
#define ALIAS_CLEAR 0x3000u

#define RESETS_RESET 0x4000c000u
#define RESETS_RESET_DONE 0x4000c008u
#define RESET_ADC (1u << 0)
#define RESET_PLL_USB (1u << 13)

#define CLK_ADC_CTRL 0x40008060u
#define CLK_ADC_DIV 0x40008064u
#define CLK_ENABLE (1u << 11)
#define CLK_ADC_AUXSRC_PLL_USB (0u << 5)
#define CLK_DIV_1 (1u << 8)

#define PLL_USB_CS 0x4002c000u
#define PLL_USB_PWR 0x4002c004u
#define PLL_USB_FBDIV_INT 0x4002c008u
#define PLL_USB_PRIM 0x4002c00cu
#define PLL_LOCK (1u << 31)
#define PLL_PWR_PD (1u << 0)
#define PLL_PWR_POSTDIVPD (1u << 3)
#define PLL_PWR_VCOPD (1u << 5)

#define ADC_CS 0x4004c000u
#define ADC_RESULT 0x4004c004u
#define ADC_CS_EN (1u << 0)
#define ADC_CS_TS_EN (1u << 1)
#define ADC_CS_START_ONCE (1u << 2)
#define ADC_CS_READY (1u << 8)
#define ADC_CS_AINSEL_SHIFT 12
#define ADC_FIRST_PIN 26
#define ADC_PIN_COUNT 4
#define ADC_TEMPERATURE 4u

#define IO_BANK0_GPIO_CTRL(pin) (0x40014004u + 8u * (unsigned int)(pin))
#define PADS_BANK0_GPIO(pin) (0x4001c004u + 4u * (unsigned int)(pin))
#define PAD_PULL_DOWN (1u << 2)
#define PAD_PULL_UP (1u << 3)
#define PAD_INPUT_ENABLE (1u << 6)
#define PAD_OUTPUT_DISABLE (1u << 7)
#define FUNCTION_NONE 0x1fu

__attribute__((noreturn)) void chipsharp_hardware_failure(const char *text, int n0, int n1, int n2, int n3);

static unsigned int adc_started;
static unsigned int analog_pins; // bit n: GPIO 26 + n is an analog input
static unsigned int adc_flags;   // ADC_CS_EN, and ADC_CS_TS_EN once the temperature has been read

static void unreset(unsigned int blocks)
{
    REG(RESETS_RESET + ALIAS_CLEAR) = blocks;
    while ((REG(RESETS_RESET_DONE) & blocks) != blocks)
    {
    }
}

// The USB PLL at 48 MHz for clk_adc, then the ADC itself (rp2040_runtime.c starts the crystal and the system PLL).
static void start_adc(void)
{
    if (adc_started)
    {
        return;
    }

    REG(RESETS_RESET + ALIAS_SET) = RESET_PLL_USB;
    unreset(RESET_PLL_USB);
    REG(PLL_USB_CS) = 1;          // REFDIV
    REG(PLL_USB_FBDIV_INT) = 40;  // 12 MHz * 40 = 480 MHz
    REG(PLL_USB_PWR + ALIAS_CLEAR) = PLL_PWR_PD | PLL_PWR_VCOPD;
    while ((REG(PLL_USB_CS) & PLL_LOCK) == 0)
    {
    }

    REG(PLL_USB_PRIM) = (5u << 16) | (2u << 12); // POSTDIV1, POSTDIV2: 480 MHz / 10 = 48 MHz
    REG(PLL_USB_PWR + ALIAS_CLEAR) = PLL_PWR_POSTDIVPD;

    REG(CLK_ADC_CTRL) = 0;        // stopped while its source changes
    REG(CLK_ADC_DIV) = CLK_DIV_1;
    REG(CLK_ADC_CTRL) = CLK_ADC_AUXSRC_PLL_USB;
    REG(CLK_ADC_CTRL) = CLK_ADC_AUXSRC_PLL_USB | CLK_ENABLE;

    REG(RESETS_RESET + ALIAS_SET) = RESET_ADC;
    unreset(RESET_ADC);
    adc_flags = ADC_CS_EN;
    REG(ADC_CS) = adc_flags;
    while ((REG(ADC_CS) & ADC_CS_READY) == 0)
    {
    }

    adc_started = 1;
}

// One conversion of input (0-4), with interrupts off for its 2 microseconds: a handler that reads an input of its own
// meanwhile would change the input this one converts.
static int convert(unsigned int input)
{
    unsigned int primask;
    __asm__ volatile("mrs %0, primask\n\tcpsid i" : "=r"(primask)::"memory");
    unsigned int select = adc_flags | (input << ADC_CS_AINSEL_SHIFT);
    REG(ADC_CS) = select;
    REG(ADC_CS) = select | ADC_CS_START_ONCE;
    while ((REG(ADC_CS) & ADC_CS_READY) == 0)
    {
    }

    int value = (int)(REG(ADC_RESULT) & 0xfffu);
    __asm__ volatile("msr primask, %0" ::"r"(primask) : "memory");
    return value;
}

static void check_pin(int pin)
{
    if (pin < ADC_FIRST_PIN || pin >= ADC_FIRST_PIN + ADC_PIN_COUNT)
    {
        chipsharp_hardware_failure("System.ArgumentException: GPIO {0} is not an analog input: on the RP2040 they are "
            "GPIO 26, 27, 28 and 29. (Parameter 'pin')", pin, 0, 0, 0);
    }
}

void ChipSharp_Runtime_AdcInput(int pin)
{
    check_pin(pin);
    start_adc();
    unsigned int pad = REG(PADS_BANK0_GPIO(pin));
    REG(PADS_BANK0_GPIO(pin)) = (pad & ~(PAD_INPUT_ENABLE | PAD_PULL_UP | PAD_PULL_DOWN)) | PAD_OUTPUT_DISABLE;
    REG(IO_BANK0_GPIO_CTRL(pin)) = FUNCTION_NONE;
    analog_pins |= 1u << (pin - ADC_FIRST_PIN);
}

int ChipSharp_Runtime_AdcRead(int pin)
{
    check_pin(pin);
    if ((analog_pins & (1u << (pin - ADC_FIRST_PIN))) == 0)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: GPIO {0} is not an analog input yet: call "
            "Adc.Input({0}) first.", pin, 0, 0, 0);
    }

    return convert((unsigned int)(pin - ADC_FIRST_PIN));
}

double ChipSharp_Runtime_AdcTemperature(void)
{
    start_adc();
    adc_flags |= ADC_CS_TS_EN;
    return 27.0 - ((double)convert(ADC_TEMPERATURE) * 3.3 / 4096.0 - 0.706) / 0.001721;
}

double ChipSharp_Runtime_AdcReference(void)
{
    return 3.3;
}

int ChipSharp_Runtime_AdcFullScale(void)
{
    return 4096;
}
