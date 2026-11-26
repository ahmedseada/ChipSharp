// Blinks the Raspberry Pi Pico's LED: on for half a second, off for half a second, forever.
//
// This runs on the Pico, not on .NET: build it with ChipSharp for the target "pico", hold BOOTSEL while plugging
// the board in, and copy the .uf2 file to the drive that appears. (Run on .NET, Pin.SetOutput throws.)

using ChipSharp.Hardware;

const int Led = 25; // the Pico's LED is on GPIO 25 (on the Pico W it is wired to the Wi-Fi chip instead)

Pin.SetOutput(Led);
while (true)
{
    Pin.Toggle(Led);
    Delay.Milliseconds(500);
}
