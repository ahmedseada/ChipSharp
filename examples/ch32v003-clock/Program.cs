// A clock on WCH's CH32V003 evaluation board: the time once a second on the serial port, from the clock the chip keeps.
//
// Clock.Start gives the clock a date; from then on DateTime.Now is that date and the time since, counted by SysTick
// (six counts a microsecond). Each line is written as the second changes: the delay waits for the rest of the second,
// whatever writing the line took. Environment.TickCount64 says the milliseconds since start-up. The chip has no
// real-time clock: the clock starts again at the date below each time the chip starts.
//
// The text goes to USART1's TX, PD5, at 115200 baud: a WCH-LinkE's RX on PD5 shows it on the LinkE's own serial port.
//
// Build (after dotnet build -c Release of the repository), in this folder:
//   dotnet build -c Release
//   ..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\Ch32v003Clock.dll -t ch32v003-evt
// Flash:     chipsharp flash bin\Release\net10.0\Ch32v003Clock.dll -t ch32v003-evt (see README.md)
// Simulate:  chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003Clock.bin --ms 3500

using System;
using ChipSharp.Hardware;

Clock.Start(new DateTime(2026, 11, 28, 12, 0, 0));
while (true)
{
    var now = DateTime.Now;
    Console.Write(now.Hour);
    Console.Write(now.Minute < 10 ? ":0" : ":");
    Console.Write(now.Minute);
    Console.Write(now.Second < 10 ? ":0" : ":");
    Console.Write(now.Second);
    Console.WriteLine($" ({Environment.TickCount64} ms since start-up)");
    Timing.DelayMilliseconds(1000 - DateTime.Now.Millisecond);
}
