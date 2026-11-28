// Blinks an LED on WCH's CH32V003 evaluation board from a timer interrupt, while Main prints how long the program has
// run.
//
// Timing.EveryMilliseconds starts the timer; from then on the chip runs the [Interrupt(Event.Timer)] method every
// 500 ms, whatever Main is doing: here, Main prints once a second. The uptime comes from the clock the chip keeps
// (SysTick's count, six a microsecond): Stopwatch's time since the program started (its static methods: StartNew
// would make an object, which needs a heap the chip has no room for), and the timer event's own count of its ticks.
//
// Wiring: a wire from the header's LED1 to PD0 (lit while PD0 is low). The text goes to USART1's TX, PD5, at 115200
// baud: a WCH-LinkE's RX on PD5 shows it on the LinkE's own serial port.
//
// Build (after dotnet build -c Release of the repository), in this folder:
//   dotnet build -c Release
//   ..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\Ch32v003TimerBlink.dll -t ch32v003-evt
// Flash:     chipsharp flash bin\Release\net10.0\Ch32v003TimerBlink.dll -t ch32v003-evt (see README.md)
// Simulate:  chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003TimerBlink.bin --ms 3500

using System;
using System.Diagnostics;
using ChipSharp.Hardware;

var started = Stopwatch.GetTimestamp();

Pin.Output(Program.Led);
Timing.EveryMilliseconds(500);  // Toggle runs 500 ms from now, and every 500 ms after that
Console.WriteLine("ChipSharp on a CH32V003: the LED blinks from the timer event");
while (true)
{
    Timing.DelayMilliseconds(1000);
    var elapsed = Stopwatch.GetElapsedTime(started);
    Console.WriteLine($"uptime {elapsed.Ticks / TimeSpan.TicksPerMillisecond} ms, {Program.ticks} toggles");
}

partial class Program
{
    public const int Led = 24;  // PD0

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
