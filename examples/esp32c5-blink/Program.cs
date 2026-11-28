// Blinks an LED on the ESP32-C5-DevKitC-1: on for half a second, off for half a second, forever.
//
// Wiring (the board's labels): GPIO 4 ("4") to the LED's anode (the long leg) through a resistor of 220 ohm, the LED's
// cathode to a "GND" pin. The board's own RGB LED is a WS2812 on GPIO 27, which a program sends colors to: it is not a
// pin that turns an LED on, so this example uses an LED of its own. GPIO 4 is one of the pins the board leaves a
// program (0, 1, 4, 5, 6, 8, 9, 10, 23 and 24; chipsharp check refuses the others, e.g. the strapping pins); change
// Led to use another. For a Pico, change Led (to 25 for its own LED) and build with -t pico: nothing here is the C5's.
//
// Build (after dotnet build -c Release of the repository), in this folder:
//   dotnet build -c Release
//   ..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\Esp32c5Blink.dll -t esp32c5-devkitc
// Flash:     chipsharp flash bin\Release\net10.0\Esp32c5Blink.dll -t esp32c5-devkitc (see README.md)
// Simulate:  chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5Blink.bin --ms 1600

using ChipSharp.Hardware;

const int Led = 4;

Pin.Output(Led);
while (true)
{
    Pin.Toggle(Led);
    Timing.DelayMilliseconds(500);
}
