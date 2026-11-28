// ChipSharp runtime for the Espressif ESP32-C5: analog inputs, ChipSharp.Hardware.Adc, on the SAR ADC1 in one-shot mode.
//
// Freestanding, like esp32c5_runtime.c. The C5 has one SAR ADC, ADC1, with 6 channels on GPIO1 to GPIO6 (ESP-IDF's
// adc_channel.h: channel n is GPIO n + 1); GPIO2 and GPIO3 are strapping pins (the board's "pinFunctions.adc" leaves
// them out). 12 bits: a reading is 0 to 4095, AdcFullScale() is 4096.
//
//   ChipSharp_Runtime_AdcInput        Adc.Input: the pin an analog input (its digital input and pulls off, not driven)
//   ChipSharp_Runtime_AdcRead         Adc.Read: one conversion
//   ChipSharp_Runtime_AdcTemperature  Adc.Temperature: the chip's temperature sensor, in degrees Celsius
//   ChipSharp_Runtime_AdcReference    the volts a reading of 4096 would be: 3.3
//   ChipSharp_Runtime_AdcFullScale    4096
//
// Attenuation and range. The input's attenuation is 12 dB (ADC_ATTEN_DB_12, the most the chip has), which takes the
// whole range of the pins' supply: a reading is the pin's voltage over 3.3 V, times 4096, uncalibrated: the SAR ADC's
// gain and offset vary from chip to chip (ESP-IDF's adc_cali schemes correct them with values from the chip's eFuses,
// which this runtime does not read), so a reading is within a few percent, and the top of the range, above about 3.1 V,
// saturates. 3.3 is the reference AdcReference() gives.
//
// A conversion follows ESP-IDF's adc_oneshot_hal_convert for a chip without an RTC ADC controller: the digital controller
// of APB_SARADC (its clock from the crystal over 16, the SAR clock over 2) is told the channel and the attenuation in
// APB_SARADC_ONETIME_SAMPLE, the start bit goes low and high, and APB_SARADC1_DONE in INT_RAW says the result is in
// SAR1DATA_STATUS. The ADC is powered up on the first use as sar_periph_ctrl.c does it for a one-shot read: the modem
// clock's analog front end (MODEM_SYSCON_CLK_CONF1's FE_APB and FE_80M, which the SVD does not have), the analog I2C that
// reaches the ADC's registers (PMU_RF_PWC's XPD_PERIF_I2C: reset, powered, released), the power detector's override that
// shares the SAR's power (PWDET_CONF_REG) and the controller's own SW power-on (XPD_SAR_FORCE). The chip's calibration of
// the ADC through that analog I2C is not done. Not tried on hardware.
//
// The temperature sensor (TSENS, in the same block): its clock and power on, and the 8-bit result raw gives
// celsius = 0.4386 * raw - 27.88 * offset - 20.52 (ESP-IDF's temperature_sensor_ll.h: TEMPERATURE_SENSOR_LL_ADC_FACTOR,
// DAC_FACTOR, OFFSET_FACTOR), with the offset of the sensor's range 0, the one it starts in: within a few degrees from
// 20 to 100 degrees Celsius, a degree or two lower than the die, which warms the sensor too.
//
// A pin that is not an analog input, or an input used before Adc.Input, stops the program with a message
// (chipsharp_hardware_failure, esp32c5_failures.c).

#include "chipsharp_hardware.h"
#include "esp32c5_chip.h"
#include "esp32c5_bus.h"

#define FIRST_PIN 1
#define LAST_PIN 6
#define FULL_SCALE 4096
#define ATTENUATION_12_DB 3u

// Registers the SVD does not have (ESP-IDF's soc/reg_base.h: PWDET_CONF_REG; modem_syscon_reg.h: MODEM_SYSCON_CLK_CONF1_REG)
#define MODEM_SYSCON_CLK_CONF1 (*(volatile uint32_t *)0x600A9C14u)
#define MODEM_SYSCON_CLK_FE_APB_EN (1u << 15)
#define MODEM_SYSCON_CLK_FE_80M_EN (1u << 13)
#define PWDET_CONF (*(volatile uint32_t *)0x600A0810u)
#define PWDET_SAR_POWER_FORCE (1u << 24)
#define PWDET_SAR_POWER_CNTL (1u << 23)

static unsigned int powered;         // the ADC is set up and powered
static unsigned int inputs;          // bit n: GPIOn is an analog input

static void spin_microseconds(unsigned int microseconds)
{
    unsigned long long until = chipsharp_c5_ticks() + (unsigned long long)microseconds * C5_TICKS_PER_MICROSECOND;
    while (chipsharp_c5_ticks() < until)
    {
    }
}

static void check_pin(chipsharp_i32 pin)
{
    if (pin < FIRST_PIN || pin > LAST_PIN)
    {
        chipsharp_hardware_failure("System.ArgumentException: GPIO {0} is not an analog input: on the ESP32-C5 they are "
            "GPIO1 to GPIO6 (ADC1's channels 0 to 5). (Parameter 'pin')", pin, 0, 0, 0);
    }
}

void ChipSharp_Runtime_AdcInput(chipsharp_i32 pin)
{
    check_pin(pin);
    unsigned int enabled = c5_interrupts_off();
    // no output, no digital input, no pull: the pad is the analog switch's
    GPIO_ENABLE_W1TC = 1u << pin;
    C5_GPIO_OUT_SEL(pin) = C5_SIMPLE_GPIO_OUTPUT;
    C5_IO_MUX(pin) &= ~(IO_MUX_GPIO0_FUN_IE_MASK | IO_MUX_GPIO0_FUN_WPU_MASK | IO_MUX_GPIO0_FUN_WPD_MASK);
    inputs |= 1u << pin;
    c5_interrupts_back(enabled);
}

// Powers the ADC and sets its clocks, once.
static void power_up(void)
{
    // the modem's analog front end clocks, and the analog I2C to the SAR ADC (reset, powered, out of reset)
    MODEM_SYSCON_CLK_CONF1 |= MODEM_SYSCON_CLK_FE_APB_EN | MODEM_SYSCON_CLK_FE_80M_EN;
    PMU_RF_PWC &= ~PMU_RF_PWC_PERIF_I2C_RSTB_MASK;
    PMU_RF_PWC |= PMU_RF_PWC_XPD_PERIF_I2C_MASK;
    PMU_RF_PWC |= PMU_RF_PWC_PERIF_I2C_RSTB_MASK;
    PWDET_CONF |= PWDET_SAR_POWER_FORCE | PWDET_SAR_POWER_CNTL;

    // the register clock, a reset of the ADC and its registers, the function clock: the crystal over 16 (div_num 15,
    // b 1, a 0: ADC_LL_CLKM_DIV_NUM_DEFAULT), and the SAR clock over 2 (ADC_LL_DIGI_SAR_CLK_DIV_DEFAULT 1)
    PCR_SARADC_CONF = PCR_SARADC_CONF_SARADC_REG_CLK_EN_MASK;
    PCR_SARADC_CONF = PCR_SARADC_CONF_SARADC_REG_CLK_EN_MASK | PCR_SARADC_CONF_SARADC_RST_EN_MASK;
    PCR_SARADC_CONF = PCR_SARADC_CONF_SARADC_REG_CLK_EN_MASK;
    PCR_SARADC_CONF = PCR_SARADC_CONF_SARADC_REG_CLK_EN_MASK | PCR_SARADC_CONF_SARADC_REG_RST_EN_MASK;
    PCR_SARADC_CONF = PCR_SARADC_CONF_SARADC_REG_CLK_EN_MASK;
    PCR_SARADC_CLKM_CONF = PCR_SARADC_CLKM_CONF_SARADC_CLKM_DIV_NUM(15) | PCR_SARADC_CLKM_CONF_SARADC_CLKM_DIV_B(1)
        | PCR_SARADC_CLKM_CONF_SARADC_CLKM_SEL(0) | PCR_SARADC_CLKM_CONF_SARADC_CLKM_EN_MASK;
    PCR_SAR_CLK_DIV = (PCR_SAR_CLK_DIV & ~PCR_SAR_CLK_DIV_SAR1_CLK_DIV_NUM_MASK) | PCR_SAR_CLK_DIV_SAR1_CLK_DIV_NUM(1);

    // the digital controller's clock on, the SAR powered by software (adc_ll_set_power_manage ADC_LL_POWER_SW_ON)
    APB_SARADC_CTRL = (APB_SARADC_CTRL & ~APB_SARADC_CTRL_SARADC_XPD_SAR_FORCE_MASK)
        | APB_SARADC_CTRL_SARADC_SAR_CLK_GATED_MASK | APB_SARADC_CTRL_SARADC_XPD_SAR_FORCE(3);
    powered = 1;
}

chipsharp_i32 ChipSharp_Runtime_AdcRead(chipsharp_i32 pin)
{
    check_pin(pin);
    if ((inputs & (1u << pin)) == 0)
    {
        chipsharp_hardware_failure("System.InvalidOperationException: GPIO {0} is not an analog input yet: call "
            "Adc.Input({0}) first.", pin, 0, 0, 0);
    }

    unsigned int enabled = c5_interrupts_off();
    if (!powered)
    {
        power_up();
    }

    // channel n of unit 1 is (0 << 3) | n; the unit's one-shot sampling on, the start low and then high
    unsigned int channel = (unsigned int)pin - FIRST_PIN;
    unsigned int sample = APB_SARADC_ONETIME_SAMPLE_SARADC1_ONETIME_SAMPLE_MASK
        | APB_SARADC_ONETIME_SAMPLE_SARADC_ONETIME_ATTEN(ATTENUATION_12_DB)
        | APB_SARADC_ONETIME_SAMPLE_SARADC_ONETIME_CHANNEL(channel);
    APB_SARADC_INT_CLR = APB_SARADC_INT_CLR_APB_SARADC1_DONE_INT_CLR_MASK;
    APB_SARADC_ONETIME_SAMPLE = sample;
    spin_microseconds(4); // the start must last 3 cycles of the digital controller's clock (3 MHz) to be seen
    APB_SARADC_ONETIME_SAMPLE = sample | APB_SARADC_ONETIME_SAMPLE_SARADC_ONETIME_START_MASK;
    unsigned long long deadline = chipsharp_c5_ticks() + 2000u * C5_TICKS_PER_MICROSECOND;
    while ((APB_SARADC_INT_RAW & APB_SARADC_INT_RAW_APB_SARADC1_DONE_INT_RAW_MASK) == 0 && chipsharp_c5_ticks() < deadline)
    {
    }

    unsigned int reading = APB_SARADC_SAR1DATA_STATUS & 0xfffu;
    APB_SARADC_ONETIME_SAMPLE = 0;
    c5_interrupts_back(enabled);
    return (chipsharp_i32)reading;
}

chipsharp_i32 ChipSharp_Runtime_AdcFullScale(void)
{
    return FULL_SCALE;
}

chipsharp_f64 ChipSharp_Runtime_AdcReference(void)
{
    return 3.3;
}

chipsharp_f64 ChipSharp_Runtime_AdcTemperature(void)
{
    unsigned int enabled = c5_interrupts_off();
    if (!powered)
    {
        power_up();
    }

    // the sensor's clock (the crystal) and reset, then its power; it converts on its own, and the result is the last
    PCR_TSENS_CLK_CONF = PCR_TSENS_CLK_CONF_TSENS_CLK_SEL_MASK | PCR_TSENS_CLK_CONF_TSENS_CLK_EN_MASK
        | PCR_TSENS_CLK_CONF_TSENS_RST_EN_MASK;
    PCR_TSENS_CLK_CONF = PCR_TSENS_CLK_CONF_TSENS_CLK_SEL_MASK | PCR_TSENS_CLK_CONF_TSENS_CLK_EN_MASK;
    APB_SARADC_APB_TSENS_CTRL = (APB_SARADC_APB_TSENS_CTRL & ~APB_SARADC_APB_TSENS_CTRL_TSENS_CLK_DIV_MASK)
        | APB_SARADC_APB_TSENS_CTRL_TSENS_CLK_DIV(6) | APB_SARADC_APB_TSENS_CTRL_TSENS_PU_MASK;
    spin_microseconds(300);
    unsigned int raw = APB_SARADC_APB_TSENS_CTRL & APB_SARADC_APB_TSENS_CTRL_TSENS_OUT_MASK;
    c5_interrupts_back(enabled);
    return 0.4386 * (double)raw - 20.52;
}
