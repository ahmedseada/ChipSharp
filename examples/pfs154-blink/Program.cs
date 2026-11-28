// Blinks an LED on a Padauk PFS154's PA4: on for half a second, off for half a second, forever.
//
// This runs on the PFS154, not on .NET: build it with ChipSharp for the target "pfs154-bare", then write the .hex into
// the chip in the Easy PDK programmer's socket with chipsharp flash (easypdkprog), or run it in chipsim (README.md):
//
//     dotnet build -c Release
//     chipsharp build bin\Release\net10.0\Pfs154Blink.dll -t pfs154-bare   # chipsharp-output\pfs154-bare\Pfs154Blink.hex
//     chipsharp flash bin\Release\net10.0\Pfs154Blink.dll -t pfs154-bare   # builds it, then easypdkprog -n PFS154 write
//     chipsim pfs154 chipsharp-output\pfs154-bare\Pfs154Blink.hex --ms 2000
//
// (Run on .NET, Pin.Output throws.) It is the Pico's and the Uno's blink with another pin number: the same C# builds
// for "pico" with 25, for "uno" with 13. Wiring: an LED with its resistor from PA4 (pin 4 by ChipSharp's numbers,
// port * 8 + bit) to GND.

using ChipSharp.Hardware;

const int Led = 4;      // PA4

Pin.Output(Led);
while (true)
{
    Pin.Toggle(Led);
    Timing.DelayMilliseconds(500);
}
