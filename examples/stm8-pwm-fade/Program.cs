// Fades an LED on the STM8S103 board's PD4 up and down, forever: a PWM output at 1 kHz whose pulse grows from 0 to the
// whole period in a second, then shrinks back in another.
//
// Pwm.Output makes PD4 a PWM output (TIM2's channel 1 on the STM8S103; PD3 and PA3 are its channels 2 and 3, at the
// same frequency), at the nearest frequency the timer makes to the one asked for: 1000 Hz exactly, a period of
// 1000 us. The chip then switches the pin itself; Pwm.PulseMicroseconds sets how long it is high in each period,
// which the eye sees as the LED's brightness. (Pwm.Duty, the duty cycle as a double from 0 to 1, needs a 64-bit
// double, which SDCC does not have: chipsharp check says so; the pulse in microseconds works everywhere.)
//
// It is examples/pfs154-pwm-fade with the board's pin: the same C# builds for "pico" (any GPIO), "uno" (D9) or
// "pfs154-bare" (PB5) with another pin number.
//
// Wiring: an LED with a 220 ohm resistor from PD4 (pin 28 by ChipSharp's numbers, D4 on the board) to GND. Build and
// flash it as examples/stm8-blink says, with Stm8PwmFade.dll; chipsim prints each new period of PD4:
//   chipsim stm8s103 chipsharp-output\stm8s103-board\Stm8PwmFade.hex --ms 2100

using ChipSharp.Hardware;

const int Led = 28;                 // PD4
const int Frequency = 1000;         // Hz: a period of 1000 us
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
