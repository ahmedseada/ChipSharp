// Blinks the blue STM8S103F3P6 board's LED: on for half a second, off for half a second, forever.
//
// It is examples/uno-blink and examples/pico-blink with the board's LED pin: PB5, pin 13 by ChipSharp's STM8 numbers
// (port * 8 + bit). The LED is wired from 3.3 V through a resistor to PB5, so it is on while PB5 is low; Pin.Output
// drives the pin low (on), the first toggle lets it go (off). This runs on the STM8, not on .NET: build it with
// ChipSharp for the target "stm8s103-board", then flash the .hex with an ST-LINK V2 (see README.md):
//
//     dotnet build -c Release
//     chipsharp build bin\Release\net10.0\Stm8Blink.dll -t stm8s103-board     # chipsharp-output\stm8s103-board\Stm8Blink.hex
//     chipsharp flash bin\Release\net10.0\Stm8Blink.dll -t stm8s103-board     # builds it, then stm8flash -c stlinkv2
//
// (Run on .NET, Pin.Output throws.) Without a board, chipsim runs the .hex and prints PB5's changes:
//     chipsim stm8s103 chipsharp-output\stm8s103-board\Stm8Blink.hex --ms 2000

using ChipSharp.Hardware;

const int Led = 13;     // PB5, the board's LED

Pin.Output(Led);
while (true)
{
    Pin.Toggle(Led);
    Timing.DelayMilliseconds(500);
}
