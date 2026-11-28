// ChipSharp runtime for the ATmega328P: the analog inputs (ChipSharp.Hardware.Adc), A0 to A5 (pins 14 to 19), and the
// chip's temperature sensor.
//
// Freestanding, like atmega328p_runtime.c. The ADC converts one input at a time, in 10 bits, against a reference: AVcc,
// the Uno's 5 V, for A0-A5, so that 0 V reads 0 and 5 V 1023; the chip's 1.1 V for the temperature sensor, as the
// datasheet (24.8) asks. Its clock is 16 MHz / 128, 125 kHz (it wants 50 to 200 kHz for its 10 bits): a conversion
// takes 13 of its clocks, 104 microseconds (25, 200 us, the first).
//
//   ChipSharp_Runtime_AdcInput         Adc.Input: a pin of A0-A5 made an analog input: no output, no pull-up, and its
//                                      digital input off (DIDR0), which would draw current between the levels
//   ChipSharp_Runtime_AdcRead          Adc.Read and Volts: one conversion of the pin's input, 0 to 1023: the chip's own
//                                      range (the Pico's is 0 to 4095)
//   ChipSharp_Runtime_AdcTemperature   Adc.Temperature: one conversion of the sensor, in degrees Celsius, by a line
//                                      through the datasheet's typical readings, 0x0160 (352) at 25 C and 0x01E0 (480)
//                                      at 125 C: 1.28 a degree. Not calibrated: a chip can be 10 degrees off
//   ChipSharp_Runtime_AdcReference     the voltage that reads as 1024: the Uno's 5 V (AVcc; USB's 5 V can be some
//                                      tenths off, and Volts with it)
//   ChipSharp_Runtime_AdcFullScale     1024: the reading the reference would be, for Adc.Volts
//
// Interrupts stay on while the ADC converts, so that a timer, a serial port and a PWM output on D9 keep their time; an
// interrupt handler may read an input too: one that comes while the program's own conversion runs waits for it to end,
// then converts its own, and the program's starts again. Changing to the 1.1 V reference (the temperature after an
// analog input, or the first time) waits 5 ms first, for the capacitor on AREF to follow, and drops the first
// conversion after any change of reference, which is not yet right.
//
// A pin that is not an analog input, or one read before Adc.Input, stops the program (chipsharp_hardware_failure,
// atmega328p_failures.c) with a message that says what to do; so do A4 and A5 while the I2C bus has them.

#include "chipsharp_hardware.h"

typedef unsigned char u8;
typedef unsigned int u16;
typedef long i32;

#define REG(address) (*(volatile u8 *)(u16)(address))

#define SREG 0x5f
#define PRR 0x64
#define PRR_ADC 0x01u

#define DDRC 0x27
#define PORTC 0x28

#define ADCL 0x78
#define ADCH 0x79
#define ADCSRA 0x7a
#define ADMUX 0x7c
#define DIDR0 0x7e
#define ADEN 0x80u
#define ADSC 0x40u
#define ADPS_128 0x07u
#define REFERENCE_AVCC 0x40u // REFS0: AVcc, with the capacitor on AREF
#define REFERENCE_1V1 0xc0u  // REFS1 and REFS0: the internal 1.1 V
#define REFERENCE_MASK 0xc0u
#define TEMPERATURE 0x08u    // MUX: ADC8, the temperature sensor

#define TWCR 0xbc
#define TWEN 0x04u

#define FIRST_PIN 14
#define PIN_COUNT 6
#define SDA_PIN 18

typedef const __flash char flash_char;

#define TEXT_OF(name) #name
#define SECTION_NAME(number) ".progmem.data.chipsharp_adc" TEXT_OF(number)
#define FLASH(text)                                                                                                    \
    (__extension__({                                                                                                   \
        static const __flash char flash_text[] __attribute__((section(SECTION_NAME(__COUNTER__)))) = text;             \
        flash_text;                                                                                                    \
    }))

// In atmega328p_failures.c and atmega328p_runtime.c.
__attribute__((noreturn)) void chipsharp_hardware_failure(flash_char *text, i32 n0, i32 n1, i32 n2, i32 n3);
void ChipSharp_Runtime_DelayMicroseconds(i32 microseconds);

static u8 analog_pins; // bit n: A<n> is an analog input

// Conversions started so far (wrapping): a handler that converts while the program waits for its own adds one, so
// that the program knows its result was taken over.
static volatile u8 conversions;

static u8 interrupts_off(void)
{
    u8 sreg = REG(SREG);
    __asm__ volatile("cli" ::: "memory");
    return sreg;
}

static void interrupts_back(u8 sreg)
{
    __asm__ volatile("" ::: "memory");
    REG(SREG) = sreg;
}

// One conversion of input (MUX) against reference (REFS), 0 to 1023.
static i32 convert(u8 reference, u8 input)
{
    u8 drop = 0; // the conversion after a change of reference
    for (;;)
    {
        u8 sreg = interrupts_off();
        if ((REG(ADCSRA) & ADEN) == 0)
        {
            REG(PRR) &= (u8)~PRR_ADC;
            REG(ADCSRA) = ADEN | ADPS_128;
        }

        // a conversion this code interrupted (in a handler) ends first: the ADC converts one input at a time
        while ((REG(ADCSRA) & ADSC) != 0)
        {
        }

        if ((REG(ADMUX) & REFERENCE_MASK) != reference)
        {
            REG(ADMUX) = reference | input;
            drop = 1;
            if (reference == REFERENCE_1V1)
            {
                interrupts_back(sreg);
                ChipSharp_Runtime_DelayMicroseconds(5000);
                continue;
            }
        }

        REG(ADMUX) = reference | input;
        REG(ADCSRA) |= ADSC;
        u8 mine = ++conversions;
        interrupts_back(sreg);
        while ((REG(ADCSRA) & ADSC) != 0)
        {
        }

        sreg = interrupts_off();
        u8 own = conversions == mine; // else a handler converted meanwhile, after this one: the result is the handler's
        if (own && !drop)
        {
            u8 low = REG(ADCL); // ADCL first: it holds ADCH until ADCH is read
            u8 high = REG(ADCH);
            interrupts_back(sreg);
            return (i32)((u16)high << 8 | low);
        }

        drop = own ? 0 : drop;
        interrupts_back(sreg);
    }
}

static u8 bit_of(i32 pin)
{
    if (pin < FIRST_PIN || pin >= FIRST_PIN + PIN_COUNT)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: Pin {0} is not an analog input: on the Arduino Uno they "
            "are pins 14 to 19 (A0 to A5). (Parameter 'pin')"), pin, 0, 0, 0);
    }

    return (u8)(1u << (u8)(pin - FIRST_PIN));
}

void ChipSharp_Runtime_AdcInput(i32 pin)
{
    u8 bit = bit_of(pin);
    if (pin >= SDA_PIN && (REG(TWCR) & TWEN) != 0)
    {
        chipsharp_hardware_failure(FLASH("System.InvalidOperationException: Pin {0} (A{1}) is the I2C bus's (I2c.Open: SDA "
            "on A4, SCL on A5), so it cannot be an analog input. Use A0 to A3."), pin, pin - FIRST_PIN, 0, 0);
    }

    u8 sreg = interrupts_off();
    REG(DDRC) &= (u8)~bit;
    REG(PORTC) &= (u8)~bit;
    REG(DIDR0) |= bit;
    analog_pins |= bit;
    interrupts_back(sreg);
}

i32 ChipSharp_Runtime_AdcRead(i32 pin)
{
    u8 bit = bit_of(pin);
    if ((analog_pins & bit) == 0)
    {
        chipsharp_hardware_failure(FLASH("System.InvalidOperationException: Pin {0} is not an analog input yet: call "
            "Adc.Input({0}) first."), pin, 0, 0, 0);
    }

    return convert(REFERENCE_AVCC, (u8)(pin - FIRST_PIN));
}

double ChipSharp_Runtime_AdcTemperature(void)
{
    return 25.0 + (double)(convert(REFERENCE_1V1, TEMPERATURE) - 352) / 1.28;
}

double ChipSharp_Runtime_AdcReference(void)
{
    return 5.0;
}

i32 ChipSharp_Runtime_AdcFullScale(void)
{
    return 1024;
}
