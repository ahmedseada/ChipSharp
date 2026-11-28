// Blinks an LED on a Padauk PFS154's PA4 from a timer interrupt: on for half a second, off for half a second, forever,
// while Main does nothing at all.
//
// Timing.EveryMilliseconds starts the timer event (TM2, on the PFS154, timed by the clock T16 keeps); from then on the
// chip runs the [Interrupt(Event.Timer)] method every 500 ms, whatever Main is doing. Wiring, build, flash and chipsim
// as examples/pfs154-blink says, with Pfs154TimerBlink.dll. The Pico's and the Uno's timer blink are the same C# with
// another pin number.

using ChipSharp.Hardware;

Pin.Output(Led);
Timing.EveryMilliseconds(500);  // Toggle runs 500 ms from now, and every 500 ms after that
while (true)
{
    // nothing to do here: the timer interrupt blinks the LED
}

partial class Program
{
    const int Led = 4;          // PA4

    // Nothing in the program calls it: the chip does, when the timer fires.
    [Interrupt(Event.Timer)]
    static void Toggle() => Pin.Toggle(Led);
}
