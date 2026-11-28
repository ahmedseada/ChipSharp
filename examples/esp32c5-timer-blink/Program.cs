// Blinks an LED on the ESP32-C5-DevKitC-1 from a timer interrupt, while Main prints how long the program has run.
//
// Timing.EveryMilliseconds starts the timer; from then on the chip runs the [Interrupt(Event.Timer)] method every
// 500 ms, whatever Main is doing: here, Main prints once a second. The uptime comes from two clocks the chip keeps
// (SYSTIMER's 64-bit count): a Stopwatch started when the program starts, and DateTime.Now (1 January 2000 at start-up,
// as no Clock.Start sets another date), and the timer event's own count of its ticks.
//
// Wiring (the board's labels): GPIO 4 ("4") to the LED's anode through a resistor of 220 ohm, the LED's cathode to "GND".
// The text goes to the USB console (the USB-C port marked "USB"; open its serial port in any terminal).
//
// Build (after dotnet build -c Release of the repository), in this folder:
//   dotnet build -c Release
//   ..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\Esp32c5TimerBlink.dll -t esp32c5-devkitc
// Flash:     chipsharp flash bin\Release\net10.0\Esp32c5TimerBlink.dll -t esp32c5-devkitc (see README.md)
// Simulate:  chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5TimerBlink.bin --ms 3500

using System;
using System.Diagnostics;
using ChipSharp.Hardware;

var watch = Stopwatch.StartNew();
var started = DateTime.Now;

Pin.Output(Program.Led);
Timing.EveryMilliseconds(500);  // Toggle runs 500 ms from now, and every 500 ms after that
Console.WriteLine("ChipSharp on an ESP32-C5: the LED blinks from the timer event");
while (true)
{
    Timing.DelayMilliseconds(1000);
    var seconds = (int)watch.Elapsed.TotalSeconds;
    var now = DateTime.Now - started;
    Console.WriteLine($"uptime {seconds} s (Stopwatch), {(int)now.TotalSeconds} s (DateTime), {Program.ticks} toggles");
}

partial class Program
{
    public const int Led = 4;

    // counted by the interrupt, read by Main
    public static volatile int ticks;

    // Nothing in the program calls it: the chip does, when the timer fires.
    [Interrupt(Event.Timer)]
    static void Toggle()
    {
        ticks++;
        Pin.Toggle(Led);
    }
}
