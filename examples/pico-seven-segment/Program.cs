// Counts from 0 to 9 on a 7-segment display, over and over, one digit a second, and prints each over the serial port
// (GPIO 0, 115200 baud), with no heap:
//
//   digit 0
//   digit 1
//   ...
//
// Wiring (diagram.json has it for Wokwi, wired directly: Wokwi needs no resistors): segments A to G on GP2 to GP8
// (pins 4, 5, 6, 7, 9, 10, 11), each through a 220 ohm resistor on a real display, and COM to GND (common cathode;
// for a common-anode display, COM to 3V3 and commonAnode: true). The display has F and G the other way round from
// GP7 and GP8, so those two wires cross once.
//
// SevenSegment comes from ChipSharp.Components, the drivers of parts connected to the chip: plain C# over Pin, which
// ChipSharp compiles with the program (what it calls of it). new SevenSegment makes the pins outputs and the display
// blank; Show lights the segments of a digit, from a table in flash.
//
// Build it with ChipSharp for the target "pico" (ChipSharp.Components.dll is next to the program's .dll, where
// chipsharp build finds it):
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\PicoSevenSegment.dll -t pico
// and run it in the simulator, which prints each pin's changes:
//   node tools\rp2040-sim\run.mjs chipsharp-output\pico\PicoSevenSegment.uf2 --ms 3000

using System;
using ChipSharp.Components;
using ChipSharp.Hardware;

var display = new SevenSegment(firstPin: 2);     // A to G on GP2 to GP8, common cathode

for (var digit = 0; ; digit = (digit + 1) % 10)
{
    display.Show(digit);
    Console.WriteLine($"digit {digit}");
    Timing.DelayMilliseconds(1000);
}
