// Blinks the Arduino Uno's LED "L" (D13) from a timer interrupt: on for half a second, off for half a second,
// forever, while Main does nothing at all.
//
// Timing.EveryMilliseconds starts the timer event (Timer1's compare B, on the Uno); from then on the chip runs the
// [Interrupt(Event.Timer)] method every 500 ms, whatever Main is doing. Build and flash it as examples/uno-blink says,
// with UnoTimerBlink.dll; diagram.json is the same circuit, an LED on D13 and GND beside the Uno's own.

using ChipSharp.Hardware;

Pin.Output(13);                 // the LED "L"
Timing.EveryMilliseconds(500);  // Toggle runs 500 ms from now, and every 500 ms after that
while (true)
{
    // nothing to do here: the timer interrupt blinks the LED
}

partial class Program
{
    // Nothing in the program calls it: the chip does, when the timer fires.
    [Interrupt(Event.Timer)]
    static void Toggle() => Pin.Toggle(13);
}
