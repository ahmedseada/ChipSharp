// A button toggles an LED on a Padauk PFS154: each press turns it on or off, through a pin interrupt, while Main does
// nothing at all.
//
// Wiring (see README.md): an LED with its resistor from PA4 to GND; a push button between PA0 and GND, and a 10 kOhm
// resistor from PA0 to VDD that holds the pin high until the button is pressed. Pin.Input leaves the chip's own
// pull-up off, and the portable API has no call that turns it on, so the resistor does its work.
//
// Pin.WatchFalling starts the pin event; PA0 is one of the PFS154's two pins with an interrupt (PB0 is the other), and
// from then on the chip runs the [Interrupt(Event.Pin)] method each time the pin goes low, whatever Main is doing. (A
// real button bounces, so one press may toggle the LED more than once.) It is the Uno's button with other pin numbers
// and without its Registers.Write of the pull-up: the same C# builds for "uno" with D2 and D13 (and the resistor).
// Build and flash it as examples/pfs154-blink says, with Pfs154Button.dll. Simulate (PA0 high from the start, as the
// resistor holds it, then pressed at 200 ms and 500 ms):
//   chipsim pfs154 chipsharp-output\pfs154-bare\Pfs154Button.hex --ms 1000 --input PA0=high --input PA0=low@200ms
//     --input PA0=high@300ms --input PA0=low@500ms --input PA0=high@600ms

using ChipSharp.Hardware;

Pin.Output(Led);
Pin.Input(Button);
Pin.WatchFalling(Button);   // Pressed runs each time PA0 goes from high to low
while (true)
{
    // nothing to do here: the pin interrupt toggles the LED
}

partial class Program
{
    const int Led = 4;      // PA4
    const int Button = 0;   // PA0

    // Nothing in the program calls it: the chip does, when the pin goes low.
    [Interrupt(Event.Pin)]
    static void Pressed() => Pin.Toggle(Led);
}
