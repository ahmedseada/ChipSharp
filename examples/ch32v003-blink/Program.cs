// Blinks an LED on WCH's CH32V003 evaluation board: on for half a second, off for half a second, forever.
//
// It is examples/stm8-blink with the board's pin: PD0, pin 24 by ChipSharp's CH32V003 numbers (port * 8 + bit), the
// pin WCH's GPIO_Toggle example blinks. The board's LEDs are on the header P4, each from VCC through 1K: a wire from
// LED1 to PD0 lights it while PD0 is low. This runs on the CH32V003, not on .NET: build it with ChipSharp for the target
// "ch32v003-evt", then flash the .bin through a WCH-LinkE (see README.md):
//
//     dotnet build -c Release
//     chipsharp build bin\Release\net10.0\Ch32v003Blink.dll -t ch32v003-evt   # chipsharp-output\...\Ch32v003Blink.bin
//     chipsharp flash bin\Release\net10.0\Ch32v003Blink.dll -t ch32v003-evt   # builds it, then wlink
//
// (Run on .NET, Pin.Output throws.) Without a board, chipsim runs the .bin and prints PD0's changes:
//     chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003Blink.bin --ms 2000

using ChipSharp.Hardware;

const int Led = 24;     // PD0

Pin.Output(Led);
while (true)
{
    Pin.Toggle(Led);
    Timing.DelayMilliseconds(500);
}
