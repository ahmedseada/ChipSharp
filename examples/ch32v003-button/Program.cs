// A button toggles an LED on WCH's CH32V003 evaluation board: each press turns it on or off, through a pin interrupt,
// while Main does nothing at all.
//
// Wiring: a wire from the header's LED1 to PD0 (the LED is wired from VCC through 1K, so it is lit while PD0 is low);
// PD3 to one leg of a push button, its other leg to GND. The button needs no resistor: Pin.Input with Pull.Up turns the
// pin's pull-up on, so the pin is high until the button takes it to GND, and Pin.WatchFalling starts the pin event, so
// the chip runs the [Interrupt(Event.Pin)] method each time the pin goes low, whatever Main is doing. (A real button
// bounces, so one press may toggle the LED more than once.) The Pico's, the Uno's and the ESP32-C5's examples use the
// same calls, with their own pins.
//
// Build (after dotnet build -c Release of the repository), in this folder:
//   dotnet build -c Release
//   ..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\Ch32v003Button.dll -t ch32v003-evt
// Flash:     chipsharp flash bin\Release\net10.0\Ch32v003Button.dll -t ch32v003-evt (see README.md)
// Simulate:  chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003Button.bin --ms 1600 --input PD3=low@200ms --input PD3=high@400ms

using ChipSharp.Hardware;

Pin.Output(Program.Led);             // low: lit
Pin.Input(Program.Button, Pull.Up);  // high until pressed
Pin.WatchFalling(Program.Button);    // Pressed runs each time PD3 goes from high to low
while (true)
{
    // nothing to do here: the pin interrupt toggles the LED
}

partial class Program
{
    public const int Led = 24, Button = 27;   // PD0, PD3 (port * 8 + bit)

    // Nothing in the program calls it: the chip does, when the pin goes low.
    [Interrupt(Event.Pin)]
    static void Pressed() => Pin.Toggle(Led);
}
