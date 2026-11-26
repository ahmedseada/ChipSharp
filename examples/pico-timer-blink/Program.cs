// Blinks the Raspberry Pi Pico's LED from a timer interrupt: on for half a second, off for half a second, forever,
// while Main does nothing at all.
//
// Timing.EveryMilliseconds starts the timer; from then on the chip runs the [Interrupt(Event.Timer)] method every
// 500 ms, whatever Main is doing. Build it with ChipSharp for the target "pico", as examples/pico-blink says.

using ChipSharp.Hardware;

Pin.Output(25);                 // the Pico's LED
Timing.EveryMilliseconds(500);  // Toggle runs 500 ms from now, and every 500 ms after that
while (true)
{
    // nothing to do here: the timer interrupt blinks the LED
}

partial class Program
{
    // Nothing in the program calls it: the chip does, when the timer fires.
    [Interrupt(Event.Timer)]
    static void Toggle() => Pin.Toggle(25);
}
