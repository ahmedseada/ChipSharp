// Counts from 0 to 9 on a 7-segment digit, over and over, one number every half second, through a 74HC595 shift
// register on SPI: three pins drive the eight segments, and a second 74HC595 chained after the first would drive
// eight more on the same three. That is the trick for a chip with few pins. It prints each number over the serial
// port (GPIO 0, 115200 baud) too, with no heap:
//
//   digit 0
//   digit 1
//   ...
//
// Wiring (diagram.json has it for Wokwi): the 74HC595's DS (serial data) to GP19 (pin 25), SPI0's TX; SHCP (shift
// clock) to GP18 (pin 24), SPI0's SCK; STCP (latch) to GP17 (pin 22), a plain pin; OE to GND, MR and VCC to 3V3, GND
// to GND. Its outputs Q0 to Q6 drive the segments A to G, and Q7 the decimal point, each through a 220 Ohm resistor;
// the display's COM to GND (common cathode).
//
// SPI sends a byte most significant bit first, so after one byte the shift register's output Qn holds bit n: the same
// table of segments as when each segment has a pin of its own (bit 0 = A). The latch is the shift register's own chip
// select: its outputs show the new byte when it goes high, so the digit never flickers through the bits on their way.
// No pin reads anything back, so Spi.Open gets no RX pin (-1).
//
// Build it with ChipSharp for the target "pico":
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\PicoSpiShiftRegister.dll -t pico
// and run it in the simulator with a simulated 74HC595, its latch on GPIO 17:
//   node tools\rp2040-sim\run.mjs chipsharp-output\pico\PicoSpiShiftRegister.uf2 --spi 0=74hc595@17 --ms 2000

using System;
using ChipSharp.Hardware;

const int Bus = 0;
const int Latch = 17;

Spi.Open(Bus, sck: 18, tx: 19, rx: -1, frequency: 1_000_000, mode: 0);
Pin.Output(Latch);

Span<byte> segments = stackalloc byte[1];
for (var number = 0; ; number = (number + 1) % 10)
{
    segments[0] = Digits[number];
    Spi.Write(Bus, segments);       // into the shift register, bit by bit
    Pin.Write(Latch, true);         // onto its outputs, all at once
    Pin.Write(Latch, false);
    Console.WriteLine($"digit {number}");
    Timing.DelayMilliseconds(500);
}

partial class Program
{
    // The segments each digit lights: bit 0 = A, ..., bit 6 = G (bit 7, the decimal point, off). In flash.
    static ReadOnlySpan<byte> Digits => new byte[] { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };
}
