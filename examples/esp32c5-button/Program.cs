// A button toggles an LED on the ESP32-C5-DevKitC-1: each press turns it on or off, through a pin interrupt, while Main
// does nothing at all.
//
// Wiring (the board's labels): GPIO 4 ("4") to the LED's anode through a resistor of 220 ohm, the LED's cathode to
// "GND"; GPIO 5 ("5") to one leg of a push button, its other leg to "GND". The button needs no resistor: Pin.Input
// with Pull.Up turns the pad's pull-up on, so the pin is high until the button takes it to GND, and Pin.WatchFalling
// starts the pin event, so the chip runs the [Interrupt(Event.Pin)] method each time the pin goes low, whatever Main is
// doing. (A real button bounces, so one press may toggle the LED more than once.) GPIO 4 and 5 are pins the board
// leaves a program, and the Pico's, the Uno's and the STM8's examples use the same calls.
//
// Build (after dotnet build -c Release of the repository), in this folder:
//   dotnet build -c Release
//   ..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\Esp32c5Button.dll -t esp32c5-devkitc
// Flash:     chipsharp flash bin\Release\net10.0\Esp32c5Button.dll -t esp32c5-devkitc (see README.md)
// Simulate:  chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5Button.bin --ms 1600 --input GPIO5=low@200ms --input GPIO5=high@400ms

using ChipSharp.Hardware;

Pin.Output(Program.Led);
Pin.Input(Program.Button, Pull.Up);  // high until pressed
Pin.WatchFalling(Program.Button);    // Pressed runs each time GPIO 5 goes from high to low
while (true)
{
    // nothing to do here: the pin interrupt toggles the LED
}

partial class Program
{
    public const int Led = 4, Button = 5;

    // Nothing in the program calls it: the chip does, when the pin goes low.
    [Interrupt(Event.Pin)]
    static void Pressed() => Pin.Toggle(Led);
}
