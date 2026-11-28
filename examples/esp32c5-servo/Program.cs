// A servo sweeps on an ESP32-C5-DevKitC-1: its arm turns from 0 to 180 degrees and back, two degrees every 20 ms, over
// and over. A hobby servo reads the length of a pulse that comes 50 times a second: 1 ms turns it to one end (0
// degrees), 2 ms to the other (180 degrees), 1.5 ms to the middle. The program makes the pulses on GPIO 6 (Pwm.Output
// at 50 Hz, Pwm.PulseMicroseconds for the length): the chip makes them on its own, so the program only says how long.
// Each 30 degrees go to the console (the USB Serial/JTAG port) with the pulse's length.
//
// Wiring on the ESP32-C5-DevKitC-1 (pins labelled by their GPIO number):
//   the servo's signal (orange) to 6, its + (red) to 5V, its - (brown) to GND
// GPIO 6 is a plain pin the board leaves a program, with nothing of the boot or the USB on it. A real servo may draw
// more current than a USB port gives while it moves: power it from its own 5 V then, its - to the board's GND too (its
// signal takes the C5's 3.3 V levels).
//
// The portable API alone, so the same C# builds for the Pico (change the pin number and -t pico).
// Build it with ChipSharp for the target "esp32c5-devkitc", then flash it through either USB-C port (see the README):
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\Esp32c5Servo.dll -t esp32c5-devkitc
//   chipsharp flash bin\Release\net10.0\Esp32c5Servo.dll -t esp32c5-devkitc
// and simulate it:
//   chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5Servo.bin --ms 4000

using System;
using ChipSharp.Hardware;

const int Servo = 6;     // GPIO 6

Pwm.Output(Servo, 50);   // 50 pulses a second, 20 ms apart
var angle = 0;
var step = 2;
while (true)
{
    var pulse = 1000 + angle * 1000 / 180;         // 1000 to 2000 microseconds
    Pwm.PulseMicroseconds(Servo, pulse);
    if (angle % 30 == 0)
    {
        Console.WriteLine($"servo at {angle} degrees: pulse {pulse} us");
    }

    Timing.DelayMilliseconds(20);                  // one pulse at each angle
    angle += step;
    if (angle == 180 || angle == 0)
    {
        step = -step;
    }
}
