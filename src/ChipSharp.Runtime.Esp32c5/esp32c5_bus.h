// ChipSharp runtime for the Espressif ESP32-C5: what the files of its buses and analog parts share (esp32c5_i2c.c,
// esp32c5_spi.c, esp32c5_adc.c, esp32c5_pwm.c): the crystal's speed, and how a pin is connected to a peripheral's signal
// through the GPIO matrix.
//
// The GPIO matrix (TRM 8.3, ESP-IDF's esp_rom_gpio_connect_out_signal and _in_signal). A peripheral's output signal n
// goes to a pin when GPIO_FUNC<pin>_OUT_SEL_CFG holds n (OEN_SEL 0: the peripheral says when the pin is driven); its
// input signal n comes from a pin when GPIO_FUNC<n>_IN_SEL_CFG holds the pin and SIG_IN_SEL (bit 8) is 1 (the matrix, not
// the IO MUX's own function). The pad's input is on (IO MUX FUN_IE) for an input signal, and the pad is the GPIO block's
// (IO MUX function 1) for both. None of the buses and outputs of this runtime uses an interrupt of its own: they poll.

#ifndef ESP32C5_BUS_H
#define ESP32C5_BUS_H

#include "esp32c5_chip.h"

#define C5_IN_SEL(signal) (*(volatile uint32_t *)(GPIO_FUNC0_IN_SEL_CFG_ADDRESS + 4u * (unsigned int)(signal)))
#define C5_IN_SEL_MATRIX 0x100u

// The crystal's speed in hertz: PCR_SYSCLK_CONF's CLK_XTAL_FREQ, in MHz (48 on the devkit; 40 on a board with the other
// crystal the chip takes). The clock sources of I2C, SPI, LEDC and the ADC are the crystal, so a bus's speed does not
// depend on the CPU's clock or the PLL.
static inline unsigned int c5_xtal_hz(void)
{
    unsigned int mhz = (PCR_SYSCLK_CONF & PCR_SYSCLK_CONF_CLK_XTAL_FREQ_MASK) >> PCR_SYSCLK_CONF_CLK_XTAL_FREQ_POS;
    return (mhz == 40u ? 40u : 48u) * 1000000u;
}

// The pad becomes the GPIO block's (IO MUX function 1), its input on and its pulls off; with pull_up, the pull-up on.
static inline void c5_pad(int pin, int pull_up)
{
    uint32_t mux = C5_IO_MUX(pin) & ~(IO_MUX_GPIO0_MCU_SEL_MASK | IO_MUX_GPIO0_FUN_WPU_MASK | IO_MUX_GPIO0_FUN_WPD_MASK);
    C5_IO_MUX(pin) = mux | IO_MUX_GPIO0_MCU_SEL(1) | IO_MUX_GPIO0_FUN_IE_MASK
        | (pull_up ? IO_MUX_GPIO0_FUN_WPU_MASK : 0u);
}

// The pin drives the peripheral's output signal.
static inline void c5_connect_output(int pin, unsigned int signal)
{
    C5_GPIO_OUT_SEL(pin) = signal;
}

// The peripheral's input signal reads the pin.
static inline void c5_connect_input(int pin, unsigned int signal)
{
    C5_IN_SEL(signal) = (unsigned int)pin | C5_IN_SEL_MATRIX;
}

// The pin's output is open-drain (it only pulls low: GPIO_PINn's PAD_DRIVER), or push-pull.
static inline void c5_open_drain(int pin, int open_drain)
{
    if (open_drain)
    {
        C5_GPIO_PIN(pin) |= GPIO_PIN0_PAD_DRIVER_MASK;
    }
    else
    {
        C5_GPIO_PIN(pin) &= ~GPIO_PIN0_PAD_DRIVER_MASK;
    }
}

#endif
