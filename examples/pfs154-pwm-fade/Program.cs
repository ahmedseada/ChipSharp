// Fades an LED on a Padauk PFS154's PB5 up and down, forever: a PWM output at 1 kHz whose pulse grows from 0 to the
// whole period in a second, then shrinks back in another.
//
// Pwm.Output makes PB5 a PWM output (TM3's, on the PFS154: PB5, PB6 and PB7 are its pins; PA3, PB2 and PB4 are TM2's),
// at the nearest frequency the timer makes to the one asked for: 1008 Hz for 1000, a period of 992 us. The chip then
// switches the pin itself; Pwm.PulseMicroseconds sets how long it is high in each period, which the eye sees as the
// LED's brightness. (Pwm.Duty, the duty cycle as a double from 0 to 1, needs a 64-bit double, which SDCC does not have:
// chipsharp check says so; the pulse in microseconds works everywhere.)
//
// Wiring: an LED with its resistor from PB5 (pin 13 by ChipSharp's numbers) to GND. Build and flash it as
// examples/pfs154-blink says, with Pfs154PwmFade.dll; chipsim prints each new period of PB5:
//   chipsim pfs154 chipsharp-output\pfs154-bare\Pfs154PwmFade.hex --ms 2100
// The same C# builds for "pico" (any GPIO) or "uno" (D9) with another pin number.

using ChipSharp.Hardware;

const int Led = 13;                 // PB5
const int Frequency = 1000;         // Hz: a period of about 1000 us
const int Step = 20;                // us more, or less, every 20 ms: 50 steps a second

Pwm.Output(Led, Frequency);
while (true)
{
    for (int pulse = 0; pulse < 1000; pulse += Step)    // brighter
    {
        Pwm.PulseMicroseconds(Led, pulse);
        Timing.DelayMilliseconds(20);
    }

    for (int pulse = 1000; pulse > 0; pulse -= Step)    // and dimmer
    {
        Pwm.PulseMicroseconds(Led, pulse);
        Timing.DelayMilliseconds(20);
    }
}
