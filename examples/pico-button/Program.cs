// A button toggles the Raspberry Pi Pico's LED: each press turns it on or off, through a pin interrupt, while Main
// does nothing at all.
//
// Wire a push button between GPIO 15 and 3.3 V: pressing it takes the pin from low (the pad's pull-down) to high.
// Pin.WatchRising starts the pin event; from then on the chip runs the [Interrupt(Event.Pin)] method each time the
// pin goes high, whatever Main is doing. (A real button bounces, so one press may toggle the LED more than once.)
// Build it with ChipSharp for the target "pico", as examples/pico-blink says.

using ChipSharp.Hardware;

Pin.Output(25);         // the Pico's LED
Pin.Input(15);          // the button
Pin.WatchRising(15);    // Pressed runs each time GPIO 15 goes from low to high
while (true)
{
    // nothing to do here: the pin interrupt toggles the LED
}

partial class Program
{
    // Nothing in the program calls it: the chip does, when the pin goes high.
    [Interrupt(Event.Pin)]
    static void Pressed() => Pin.Toggle(25);
}
