// A button toggles the Arduino Uno's LED "L" (D13): each press turns it on or off, through a pin interrupt, while
// Main does nothing at all.
//
// Wire a push button between D2 and GND, the usual Arduino way (INPUT_PULLUP): the pin's pull-up holds it high, and
// pressing the button takes it low. Pin.Input leaves the pull-up off, so the program turns it on itself: for an input
// pin, its bit of the port's PORT register is its pull-up, and D2 is bit 2 of PORTD (data address 0x2B; Registers
// reads and writes the Uno's registers a byte at a time). Then nothing else is needed, on a real board or in Wokwi.
// (The Pico's button example wires the button to 3.3 V instead, against the pad's pull-down: an ATmega328P's pins have
// no pull-down, so a button to 5 V would need a resistor to GND, else the pin floats while it is up.)
//
// Pin.WatchFalling starts the pin event; D2 is the chip's INT0, which catches every falling edge, and from then on the
// chip runs the [Interrupt(Event.Pin)] method each time the pin goes low, whatever Main is doing. (A real button
// bounces, so one press may toggle the LED more than once; Wokwi's button in diagram.json does not: "bounce": "0".)
// Build and flash it as examples/uno-blink says, with UnoButton.dll.

using ChipSharp.Hardware;

const uint PortD = 0x2B;    // PORTD: D0-D7's levels when they drive, their pull-ups when they are inputs

Pin.Output(13);                                             // the LED "L"
Pin.Input(2);                                               // the button
Registers.Write(PortD, Registers.Read(PortD) | 1u << 2);    // D2's pull-up on: the pin reads high until pressed
Pin.WatchFalling(2);                                        // Pressed runs each time D2 goes from high to low
while (true)
{
    // nothing to do here: the pin interrupt toggles the LED
}

partial class Program
{
    // Nothing in the program calls it: the chip does, when the pin goes low.
    [Interrupt(Event.Pin)]
    static void Pressed() => Pin.Toggle(13);
}
