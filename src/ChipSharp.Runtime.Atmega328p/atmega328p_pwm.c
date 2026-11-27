// ChipSharp runtime for the ATmega328P: PWM outputs (ChipSharp.Hardware.Pwm) on the Uno's PWM pins (the "~" ones) but
// D10: D3, D5, D6, D9 and D11.
//
// Freestanding, like atmega328p_runtime.c. Each pin is a compare output of a timer:
//
//   D5, D6    Timer0's outputs B and A (OC0B, OC0A)   8 bits: 256 steps a period (fast PWM) or 255 (phase correct)
//   D3, D11   Timer2's outputs B and A (OC2B, OC2A)
//   D9        Timer1's output A (OC1A)                every half microsecond: any frequency from 1 Hz to 10 kHz
//   D10       Timer1's output B (OC1B): not a PWM output, as Timer1's compare B is the timer event's (Timing.Every...)
//
// Timer0 and Timer2 count from 0 to 255 at 16 MHz / N (Timer0's N 1, 8, 64, 256 or 1024; Timer2's 1, 8, 32, 64, 128,
// 256 or 1024), up and round again (fast PWM: a period of 256 counts) or up and down (phase correct: 510), so they make
// a few frequencies only, from 62500 Hz (16 MHz / 256) down to 30.6 Hz (16 MHz / 1024 / 510); Pwm.Output takes the one
// nearest the frequency asked for (by ratio):
//
//   Timer0 (D5, D6)    62500, 31373, 7813, 3922, 977, 490, 244, 123, 61, 31 Hz
//   Timer2 (D3, D11)   62500, 31373, 7813, 3922, 1953, 980, 977, 490, 488, 245, 244, 123, 61, 31 Hz
//
// and a timer's two outputs run at its one frequency, as a Pico slice's. A duty cycle is a number of counts high: 0 is
// low all the time and the whole period high all the time (the output then a plain level, PORTx), and the others the
// compare's level, which the timer takes at the end of a period. A servo's 50 Hz is 61 Hz on these, with 64 us steps.
//
// Timer1 is the clock's (atmega328p_runtime.c): it counts half microseconds from 0 to 0xffff for ever, so its period
// cannot change. D9 is made by its compare A all the same: the compare sets or clears D9 at the very count of each edge,
// and its interrupt (TIMER1_COMPA, the runtime's handler 3) sets the count of the next; a period longer than half a
// round of Timer1 has matches in between that keep D9's level. So D9's frequency and duty cycle are those asked for to
// the half microsecond: Pwm.Output(9, 50) and PulseMicroseconds(9, 1500) are a servo's 20 ms and 1.5 ms exactly. An edge
// takes the interrupt some 10 microseconds, so a pulse or a gap shorter than 20 us is not made: D9 stays low (or
// high) that period; and an interrupt handler of the program's that runs long delays the edge after it.
//
//   ChipSharp_Runtime_PwmOutput        Pwm.Output: the pin's timer at the frequency, the pin low
//   ChipSharp_Runtime_PwmDuty          Pwm.Duty: the part of each period high, 0 to 1
//   ChipSharp_Runtime_PwmPulse         Pwm.PulseMicroseconds: the time high, from the start of each period
//   ChipSharp_Runtime_PwmStop          Pwm.Stop: the pin no longer driven; the timer stopped once neither output is used
//
// A pin that is not a PWM pin, a frequency the pin's timer cannot make, a second frequency on a timer whose other
// output is in use, D11 while the SPI bus has it, and a pin used before Pwm.Output stop the program
// (chipsharp_hardware_failure, atmega328p_failures.c) with a message that says what to do.

typedef unsigned char u8;
typedef signed char i8;
typedef unsigned int u16;
typedef long i32;
typedef unsigned long u32;
typedef unsigned long long u64;

#define REG(address) (*(volatile u8 *)(u16)(address))

#define SREG 0x5f
#define PRR 0x64
#define PRR_TIMER0 0x20u
#define PRR_TIMER2 0x40u

// The pins' registers, as atmega328p_runtime.c has them: PINx, then DDRx, then PORTx.
#define PORT_B 0x23u
#define PORT_D 0x29u
#define DDR_REGISTER 1u
#define PORT_REGISTER 2u

// Timer0's and Timer2's registers: TCCRnA, TCCRnB, TCNTn, OCRnA, OCRnB, one after the other.
#define TIMER0 0x44u
#define TIMER2 0xb0u
#define TCCRB 1u
#define TCNT 2u
#define WGM_FAST 0x03u  // WGMn1, WGMn0: fast PWM, 0 to 255
#define WGM_PHASE 0x01u // WGMn0: phase correct PWM, 0 to 255 to 0
#define PHASE 0x80u     // in a setting: phase correct; its low 3 bits are the clock select, CSn2:0

#define TCCR1A 0x80
#define TCCR1C 0x82
#define FOC1A 0x80u
#define COM1A_CLEAR 0x80u // compare A clears OC1A
#define COM1A_SET 0xc0u   // compare A sets OC1A
#define OCR1AL 0x88
#define OCR1AH 0x89
#define TIMSK1 0x6f
#define OCIE1A 0x02u
#define TIFR1 0x36
#define OCF1A 0x02u
#define D9_BIT 0x02u // PB1

#define SPCR 0x4c
#define SPE 0x40u

#define HOOK_PWM 3

#define D9 4
#define LOWEST_FREQUENCY 30
#define HIGHEST_FREQUENCY 62500
#define D9_LOWEST_FREQUENCY 1
#define D9_HIGHEST_FREQUENCY 10000

// D9's times, in counts of Timer1 (half microseconds): an edge closer than SOON is made at once (waiting for its count),
// as a compare set to a count Timer1 has passed would match only a round later; a pulse or gap of less than MIN_EDGE is
// not made; the first edge comes START after the duty cycle is set.
#define SOON 32
#define MIN_EDGE 40u
#define START 64u

#define INVALID 0xffffffffu

typedef const __flash char flash_char;

#define TEXT_OF(name) #name
#define SECTION_NAME(number) ".progmem.data.chipsharp_pwm" TEXT_OF(number)
#define FLASH(text)                                                                                                    \
    (__extension__({                                                                                                   \
        static const __flash char flash_text[] __attribute__((section(SECTION_NAME(__COUNTER__)))) = text;             \
        flash_text;                                                                                                    \
    }))

// In atmega328p_failures.c and atmega328p_runtime.c.
__attribute__((noreturn)) void chipsharp_hardware_failure(flash_char *text, i32 n0, i32 n1, i32 n2, i32 n3);
void chipsharp_clock_read(u32 *rounds, u16 *count);
extern void (*volatile chipsharp_interrupt_hooks[4])(u8 source);

// The outputs of Timer2 and Timer0, by twos (a timer's B, then its A, so that output ^ 1 is the other of its timer):
// the pin, the compare output's mode bits in TCCRnA (non-inverting: cleared at the compare, set at the bottom; the bit
// below is the mode's other bit), its OCRn, its port and bit.
struct output
{
    u8 pin;
    u8 compare;
    u8 level;
    u8 port;
    u8 mask;
};

static const __flash struct output outputs[4] = {
    {3, 0x20u, TIMER2 + 4u, PORT_D, 0x08u},  // D3: OC2B, PD3
    {11, 0x80u, TIMER2 + 3u, PORT_B, 0x08u}, // D11: OC2A, PB3
    {5, 0x20u, TIMER0 + 4u, PORT_D, 0x20u},  // D5: OC0B, PD5
    {6, 0x80u, TIMER0 + 3u, PORT_D, 0x40u},  // D6: OC0A, PD6
};

// Each timer's settings by frequency, from the highest: the first whose lowest frequency is at or below the one asked for
// (the geometric mean of it and the next: the nearest by ratio), CSn2:0 and PHASE.
struct setting
{
    u16 from;
    u8 value;
};

static const __flash struct setting timer0_settings[10] = {
    {44281u, 1u},         // 62500 Hz: fast PWM, N = 1
    {15656u, 1u | PHASE}, // 31372.5 Hz: phase correct, N = 1
    {5536u, 2u},          // 7812.5 Hz: N = 8
    {1957u, 2u | PHASE},  // 3921.6 Hz
    {692u, 3u},           // 976.6 Hz: N = 64
    {346u, 3u | PHASE},   // 490.2 Hz
    {173u, 4u},           // 244.1 Hz: N = 256
    {87u, 4u | PHASE},    // 122.5 Hz
    {44u, 5u},            // 61.0 Hz: N = 1024
    {0u, 5u | PHASE},     // 30.6 Hz
};

static const __flash struct setting timer2_settings[14] = {
    {44281u, 1u},         // 62500 Hz: fast PWM, N = 1
    {15656u, 1u | PHASE}, // 31372.5 Hz: phase correct, N = 1
    {5536u, 2u},          // 7812.5 Hz: N = 8
    {2768u, 2u | PHASE},  // 3921.6 Hz
    {1384u, 3u},          // 1953.1 Hz: N = 32
    {979u, 3u | PHASE},   // 980.4 Hz
    {692u, 4u},           // 976.6 Hz: N = 64
    {490u, 4u | PHASE},   // 490.2 Hz
    {346u, 5u},           // 488.3 Hz: N = 128
    {245u, 5u | PHASE},   // 245.1 Hz
    {173u, 6u},           // 244.1 Hz: N = 256
    {87u, 6u | PHASE},    // 122.5 Hz
    {44u, 7u},            // 61.0 Hz: N = 1024
    {0u, 7u | PHASE},     // 30.6 Hz
};

// log2 of N by the clock select, for each timer.
static const __flash u8 timer0_shifts[6] = {0, 0, 3, 6, 8, 10};
static const __flash u8 timer2_shifts[8] = {0, 0, 3, 5, 6, 7, 8, 10};

static u8 settings[2]; // Timer2's, Timer0's (as the outputs go): its setting while it runs, else 0
static i32 asked[2];   // the frequency each was asked for, for the messages
// bit n: output n is a PWM output; bit D9: D9 (atmega328p_spi.c reads bit 1, D11's)
u8 chipsharp_pwm_outputs;

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

// ---- Pins --------------------------------------------------------------------------------------------------------

// The output of a PWM pin (D9 for D9), or -1.
static i8 output_of(i32 pin)
{
    if (pin == 9)
    {
        return D9;
    }

    for (u8 i = 0; i < 4; i++)
    {
        if (outputs[i].pin == pin)
        {
            return (i8)i;
        }
    }

    return -1;
}

static u8 check_pin(i32 pin)
{
    if (pin == 10)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: Pin 10 (D10) cannot be a PWM output: its timer output, "
            "OC1B, is Timer1's compare B, which the timer event (Timing.Every..., After...) uses. The PWM pins are 3, 5, 6, "
            "9 and 11. (Parameter 'pin')"), 0, 0, 0, 0);
    }

    i8 output = output_of(pin);
    if (output < 0)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentException: Pin {0} is not a PWM pin: on the Arduino Uno they are "
            "pins 3, 5, 6, 9 and 11 (D3, D5, D6, D9, D11). (Parameter 'pin')"), pin, 0, 0, 0);
    }

    return (u8)output;
}

// The output of a PWM output pin, after checking that it is one.
static u8 pwm_output(i32 pin)
{
    u8 output = check_pin(pin);
    if ((chipsharp_pwm_outputs & (1u << output)) == 0)
    {
        chipsharp_hardware_failure(FLASH("System.InvalidOperationException: Pin {0} is not a PWM output: call "
            "Pwm.Output({0}, frequency) first."), pin, 0, 0, 0);
    }

    return output;
}

// ---- The duty cycle ----------------------------------------------------------------------------------------------

// duty * 65536, rounded down, for a duty cycle from 0 to 1 (0x10000 for 1), from the double's bits; INVALID for any other
// number, NaN too.
static u32 fraction(double duty)
{
    union
    {
        double value;
        u64 bits;
    } number;
    number.value = duty;
    u32 high = (u32)(number.bits >> 32);
    u32 low = (u32)number.bits;
    u16 exponent = (u16)(high >> 20) & 0x7ffu;
    if ((high & 0x80000000u) != 0)
    {
        return ((high << 1) | low) == 0 ? 0 : INVALID; // -0.0 is 0
    }

    if (exponent >= 1023u)
    {
        return exponent == 1023u && (high & 0xfffffu) == 0 && low == 0 ? 0x10000u : INVALID;
    }

    if (exponent < 1007u)
    {
        return 0; // below 2^-16
    }

    // 1.fraction times 2^(exponent - 1023), its 20 top bits: times 65536 is a shift right by 1023 + 4 - exponent
    return ((high & 0xfffffu) | 0x100000u) >> (1027u - exponent);
}

static void check_duty(u32 part)
{
    if (part > 0x10000u)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentOutOfRangeException: A duty cycle is from 0 (always low) to 1 "
            "(always high). (Parameter 'duty')"), 0, 0, 0, 0);
    }
}

static void check_pulse(i32 microseconds)
{
    if (microseconds < 0)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentOutOfRangeException: A pulse of {0} microseconds is shorter than "
            "none. (Parameter 'microseconds')"), microseconds, 0, 0, 0);
    }
}

// ---- Timer0 and Timer2 -------------------------------------------------------------------------------------------

static u8 timer_of(u8 output)
{
    return output < 2 ? TIMER2 : TIMER0;
}

// The counts of a period, by the output's timer: 256 in fast PWM, 255 (each up and down) phase correct.
static u16 steps_of(u8 output)
{
    return (settings[output >> 1] & PHASE) != 0 ? 255u : 256u;
}

// The output high for high counts of each period (0 to 256 fast, 0 to 255 phase correct), with interrupts off: none or
// all of them a plain level, the others the compare's (OCR + 1 counts high in fast PWM, 2 * OCR of 510 phase correct).
static void set_high(u8 output, u16 high)
{
    const __flash struct output *o = &outputs[output];
    u8 timer = timer_of(output);
    u16 steps = steps_of(output);
    u8 mode = o->compare | o->compare >> 1;
    if (high == 0 || high >= steps)
    {
        REG(timer) &= (u8)~mode;
        REG(o->port + PORT_REGISTER) = high != 0 ? REG(o->port + PORT_REGISTER) | o->mask
                                                 : REG(o->port + PORT_REGISTER) & (u8)~o->mask;
    }
    else
    {
        REG(o->level) = (u8)(steps == 256u ? high - 1u : high);
        REG(timer) = (REG(timer) & (u8)~mode) | o->compare;
    }
}

static void timer_output(i32 pin, u8 output, i32 frequency)
{
    u8 index = output >> 1;
    u8 timer = timer_of(output);
    if (pin == 11 && (REG(SPCR) & SPE) != 0)
    {
        chipsharp_hardware_failure(FLASH("System.InvalidOperationException: Pin 11 (D11) is SPI0's TX (MOSI) while the bus "
            "is open, so it cannot be a PWM output. Use pin 3, 5, 6 or 9."), 0, 0, 0, 0);
    }

    if (frequency < LOWEST_FREQUENCY || frequency > HIGHEST_FREQUENCY)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentOutOfRangeException: The ATmega328P cannot make a PWM frequency of "
            "{0} Hz on pin {1}: Timer{2} makes 30 to 62500 Hz (pin 9, any from 1 to 10000 Hz). (Parameter 'frequency')"),
            frequency, pin, timer == TIMER0 ? 0 : 2, 0);
    }

    u8 setting = 0;
    if (timer == TIMER0)
    {
        for (u8 i = 0; i < 10; i++)
        {
            if ((u32)frequency >= timer0_settings[i].from)
            {
                setting = timer0_settings[i].value;
                break;
            }
        }
    }
    else
    {
        for (u8 i = 0; i < 14; i++)
        {
            if ((u32)frequency >= timer2_settings[i].from)
            {
                setting = timer2_settings[i].value;
                break;
            }
        }
    }

    u8 other = output ^ 1u;
    u8 shared = (chipsharp_pwm_outputs & (1u << other)) != 0;
    if (shared && settings[index] != setting)
    {
        chipsharp_hardware_failure(FLASH("System.InvalidOperationException: Pin {0} shares Timer{1} with pin {2}, which "
            "runs at {3} Hz, and a timer runs at one frequency. Give both the same frequency, stop pin {2} first, or use a "
            "pin of another timer."), pin, timer == TIMER0 ? 0 : 2, outputs[other].pin, asked[index]);
    }

    u8 sreg = interrupts_off();
    if (!shared)
    {
        REG(PRR) &= (u8)~(timer == TIMER0 ? PRR_TIMER0 : PRR_TIMER2);
        REG(timer + TCCRB) = 0; // stopped while it is set up
        REG(timer) = (setting & PHASE) != 0 ? WGM_PHASE : WGM_FAST;
        REG(timer + TCNT) = 0;
        REG(timer + TCCRB) = setting & 0x07u;
        settings[index] = setting;
        asked[index] = frequency;
    }

    const __flash struct output *o = &outputs[output];
    set_high(output, 0);
    REG(o->port + DDR_REGISTER) |= o->mask;
    chipsharp_pwm_outputs |= (u8)(1u << output);
    interrupts_back(sreg);
}

static void timer_pulse(u8 output, i32 microseconds)
{
    // a count is N clocks of 16 MHz: microseconds * 16 / N counts high (fast PWM), or OCR = that / 2 (phase correct);
    // the longest period is 32640 microseconds (N = 1024, phase correct)
    u8 setting = settings[output >> 1];
    u8 select = setting & 0x07u;
    u8 shift = timer_of(output) == TIMER0 ? timer0_shifts[select] : timer2_shifts[select];
    u32 clocks = microseconds > 40000 ? 40000u * 16u : (u32)microseconds * 16u;
    u32 high = (setting & PHASE) != 0 ? (clocks + (1u << shift)) >> (shift + 1u)
                                      : (clocks + ((1u << shift) >> 1)) >> shift;
    u8 sreg = interrupts_off();
    set_high(output, high > 256u ? 256u : (u16)high);
    interrupts_back(sreg);
}

static void timer_stop(u8 output)
{
    const __flash struct output *o = &outputs[output];
    u8 timer = timer_of(output);
    u8 sreg = interrupts_off();
    REG(timer) &= (u8)~(o->compare | o->compare >> 1);
    REG(o->port + DDR_REGISTER) &= (u8)~o->mask;
    REG(o->port + PORT_REGISTER) &= (u8)~o->mask;
    chipsharp_pwm_outputs &= (u8)~(1u << output);
    if ((chipsharp_pwm_outputs & (1u << (output ^ 1u))) == 0)
    {
        REG(timer + TCCRB) = 0;
        REG(timer) = 0;
        settings[output >> 1] = 0;
    }

    interrupts_back(sreg);
}

// ---- D9: Timer1's compare A --------------------------------------------------------------------------------------

static u32 d9_period;  // counts of Timer1 (half microseconds) a period
static u32 d9_high;    // of them high, from the next rising edge on
static u32 d9_rise;    // when the next rising edge comes
static u32 d9_fall;    // when the falling edge of the period started last comes
static u8 d9_rising;   // whether the next edge is the rising one, else the falling one
static u8 d9_level;    // D9's level now
static u8 d9_running;  // whether compare A's interrupt makes the edges

// Timer1's count with its rounds, the low 32 bits: the clock's half microseconds (they go round after 35 minutes).
static u32 counts_now(void)
{
    u32 rounds;
    u16 count;
    chipsharp_clock_read(&rounds, &count);
    return rounds << 16 | count;
}

// D9 high or low at once (FOC1A forces what compare A would do), and kept so by a match before the next edge's.
static void d9_force(u8 high)
{
    REG(TCCR1A) = high ? COM1A_SET : COM1A_CLEAR;
    REG(TCCR1C) = FOC1A;
    d9_level = high;
}

// Sets compare A to make the next edge, with interrupts off: an edge that is due (or late) is made here, and the one
// after it is taken. An edge more than half a round of Timer1 away gets a match a quarter round from now first, which
// keeps D9's level, so that every match is unambiguous.
static void d9_schedule(void)
{
    for (;;)
    {
        u32 now = counts_now();
        u32 edge = d9_rising ? d9_rise : d9_fall;
        i32 until = (i32)(edge - now);
        if (until > SOON)
        {
            u8 high = d9_level;
            u16 match = (u16)now + 0x4000u;
            if (until < 0x8000)
            {
                high = d9_rising;
                match = (u16)edge;
            }

            REG(OCR1AH) = (u8)(match >> 8); // the high byte first: it goes through Timer1's temporary register
            REG(OCR1AL) = (u8)match;
            REG(TCCR1A) = high ? COM1A_SET : COM1A_CLEAR;
            REG(TIFR1) = OCF1A;
            return;
        }

        if (d9_rising && until < -(i32)d9_period)
        {
            // more than a period late (a handler of the program's ran long): the periods missed are left out
            d9_rise += (u32)-until / d9_period * d9_period;
            continue;
        }

        while ((i32)(edge - counts_now()) > 0)
        {
        }

        d9_force(d9_rising);
        if (d9_rising)
        {
            d9_fall = d9_rise + d9_high;
            d9_rise += d9_period;
        }

        d9_rising ^= 1u;
    }
}

// TIMER1_COMPA (atmega328p_interrupts.S): an edge was made, or a match on the way to one.
static void d9_event(u8 source)
{
    (void)source;
    if (d9_running)
    {
        d9_schedule();
    }
}

// D9 high for high counts of each period, from the next period on.
static void d9_set(u32 high)
{
    high = high > d9_period ? d9_period : high;
    high = high < MIN_EDGE ? 0 : d9_period - high < MIN_EDGE ? d9_period : high;
    u8 sreg = interrupts_off();
    if (high == 0 || high == d9_period)
    {
        REG(TIMSK1) &= (u8)~OCIE1A;
        d9_running = 0;
        d9_force(high != 0);
    }
    else
    {
        d9_high = high;
        if (!d9_running)
        {
            chipsharp_interrupt_hooks[HOOK_PWM] = d9_event;
            d9_rise = counts_now() + START;
            d9_rising = 1;
            d9_running = 1;
            d9_schedule();
            REG(TIMSK1) |= OCIE1A;
        }
    }

    interrupts_back(sreg);
}

static void d9_output(i32 frequency)
{
    if (frequency < D9_LOWEST_FREQUENCY || frequency > D9_HIGHEST_FREQUENCY)
    {
        chipsharp_hardware_failure(FLASH("System.ArgumentOutOfRangeException: The ATmega328P cannot make a PWM frequency of "
            "{0} Hz on pin 9: Timer1's compare A makes 1 to 10000 Hz there. (Parameter 'frequency')"), frequency, 0, 0, 0);
    }

    u8 sreg = interrupts_off();
    REG(TIMSK1) &= (u8)~OCIE1A;
    d9_running = 0;
    d9_period = (2000000u + (u32)frequency / 2u) / (u32)frequency;
    d9_force(0);
    REG(PORT_B + PORT_REGISTER) &= (u8)~D9_BIT;
    REG(PORT_B + DDR_REGISTER) |= D9_BIT;
    chipsharp_pwm_outputs |= 1u << D9;
    interrupts_back(sreg);
}

static void d9_stop(void)
{
    u8 sreg = interrupts_off();
    REG(TIMSK1) &= (u8)~OCIE1A;
    d9_running = 0;
    REG(TCCR1A) = 0; // D9 is its port's again
    REG(PORT_B + DDR_REGISTER) &= (u8)~D9_BIT;
    REG(PORT_B + PORT_REGISTER) &= (u8)~D9_BIT;
    chipsharp_pwm_outputs &= (u8)~(1u << D9);
    interrupts_back(sreg);
}

// ---- The runtime functions ---------------------------------------------------------------------------------------

void ChipSharp_Runtime_PwmOutput(i32 pin, i32 frequency)
{
    u8 output = check_pin(pin);
    if (output == D9)
    {
        d9_output(frequency);
    }
    else
    {
        timer_output(pin, output, frequency);
    }
}

void ChipSharp_Runtime_PwmDuty(i32 pin, double duty)
{
    u8 output = pwm_output(pin);
    u32 part = fraction(duty);
    check_duty(part);
    if (output == D9)
    {
        // d9_period * part / 65536, rounded, in two halves that fit 32 bits
        d9_set((d9_period >> 16) * part + (((d9_period & 0xffffu) * part + 0x8000u) >> 16));
        return;
    }

    u8 sreg = interrupts_off();
    set_high(output, (u16)((part * steps_of(output) + 0x8000u) >> 16));
    interrupts_back(sreg);
}

void ChipSharp_Runtime_PwmPulse(i32 pin, i32 microseconds)
{
    u8 output = pwm_output(pin);
    check_pulse(microseconds);
    if (output == D9)
    {
        d9_set((u32)microseconds * 2u);
    }
    else
    {
        timer_pulse(output, microseconds);
    }
}

void ChipSharp_Runtime_PwmStop(i32 pin)
{
    u8 output = check_pin(pin);
    if ((chipsharp_pwm_outputs & (1u << output)) == 0)
    {
        return;
    }

    if (output == D9)
    {
        d9_stop();
    }
    else
    {
        timer_stop(output);
    }
}
