// Shows a title and a count on a 16x2 character LCD, an HD44780 behind a PCF8574 I2C backpack at address 0x27, the
// count going up once a second:
//
//   +----------------+
//   |Hello, ChipSharp|
//   |count 12        |
//   +----------------+
//
// and says over the serial port (GPIO 0, 115200 baud) whether the display answered. No heap: the text is string
// literals, and Lcd.Write writes the count digit by digit, with no string made.
//
// Wiring (diagram.json has it for Wokwi, a wokwi-lcd1602 with "pins": "i2c"): the backpack's SDA to GP26 (pin 31)
// and SCL to GP27 (pin 32), I2C1; VCC to 3V3 and GND to GND. I2c.Open turns the Pico's pull-ups on. A real 1602 module
// is a 5 V part: on VBUS (5 V) its text has more contrast, but then the backpack's own pull-up resistors take SDA and
// SCL to 5 V, more than the RP2040's pins stand: take them off the backpack (or use a level shifter, or a 3.3 V
// display).
//
// Lcd.Open waits for the display to power up, puts it in 4-bit mode and clears it; Lcd.Line writes a whole row (the
// rest of it spaces), Lcd.Write from a column on. Each character is one I2C transfer of five bytes to the backpack.
//
// Build it with ChipSharp for the target "pico":
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\PicoI2cLcd1602.dll -t pico
// and run it in the simulator with a simulated LCD at 0x27 on I2C1, which prints what it shows:
//   node tools\rp2040-sim\run.mjs chipsharp-output\pico\PicoI2cLcd1602.uf2 --i2c 1:0x27=lcd1602 --ms 3500

using System;
using ChipSharp.Hardware;

const int Bus = 1;
const int Address = 0x27;

I2c.Open(Bus, sda: 26, scl: 27, frequency: 100_000);
if (!Lcd.Open(Bus, Address, columns: 16, rows: 2))
{
    Console.WriteLine("No LCD answers at 0x27: check its wiring.");
    return;
}

Console.WriteLine("LCD 16x2 at 0x27 on I2C1");
Lcd.Line(0, "Hello, ChipSharp");
Lcd.Line(1, "count");
for (var count = 0; ; count++)
{
    Lcd.Write(6, 1, count);
    Timing.DelayMilliseconds(1000);
}
