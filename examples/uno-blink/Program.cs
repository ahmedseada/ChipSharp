// Blinks the Arduino Uno's LED "L": on for half a second, off for half a second, forever.
//
// This runs on the Uno, not on .NET: build it with ChipSharp for the target "uno", then flash the .hex through the
// Uno's USB port with chipsharp flash (avrdude), or run it in Wokwi (see the README, "Arduino Uno"):
//
//     dotnet build -c Release
//     chipsharp build bin\Release\net10.0\UnoBlink.dll -t uno         # chipsharp-output\uno\UnoBlink.hex
//     chipsharp flash bin\Release\net10.0\UnoBlink.dll -t uno         # builds it, then avrdude -P <the Uno's port>
//
// (Run on .NET, Pin.Output throws.) The LED "L" is on D13 (pin 13 by the Uno's numbers). diagram.json, Wokwi's
// circuit, also has an LED plugged into D13 and the GND next to it, which blinks with it; on a real board put a
// 220 ohm resistor in series with such an LED (Wokwi needs none).

using ChipSharp.Hardware;

Pin.Output(13);
while (true)
{
    Pin.Toggle(13);
    Timing.DelayMilliseconds(500);
}
