// A clock on an Arduino Uno: the date and the time on a 16x2 character LCD, once a second,
//
//   +----------------+
//   |Fri 27 Nov 2026 |
//   |18:00:05        |
//   +----------------+
//
// its time from a DS1307 real-time clock, which keeps it on its battery while the board has no power. The program
// reads the DS1307 once, at start-up, and starts the Uno's own clock from it (Clock.Start): from then on
// DateTime.Now is that time plus what Timer1 has counted since, so the DS1307 is not read again. A DS1307 that does
// not answer or does not run (a new one is halted) is set to a date to start from, and starts. Over the serial port
// (USART0: D1 sends, 115200 baud) it says where the time came from.
//
// No heap, which the Uno has none of: the text of the date is written into a span on the stack with TryFormat, and
// the LCD shows the span; $"{now:...}" printed straight away makes no string either. It is examples/pico-clock on
// the Uno's I2C bus.
//
// Wiring (diagram.json has it for Wokwi: a wokwi-lcd1602 with "pins": "i2c" and a wokwi-ds1307), both on the Uno's
// I2C bus: SDA to A4, SCL to A5, VCC (5V on the DS1307) to 5V, GND to GND. I2c.Open turns the pins' pull-ups on. Both
// are 5 V parts, as the Uno is: no level shifter is needed.
//
// Build it with ChipSharp for the target "uno", then flash the .hex through the Uno's USB port (see the README):
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\UnoLcdClock.dll -t uno
//   chipsharp flash bin\Release\net10.0\UnoLcdClock.dll -t uno
// and run it in the simulator with a simulated LCD and a simulated DS1307 (from the PC's time, or from a date):
//   node ..\..\tools\avr-sim\run.mjs chipsharp-output\uno\UnoLcdClock.hex --i2c 0:0x27=lcd1602 --i2c 0:0x68=ds1307 --ms 3500
//   ... --i2c 0:0x68=ds1307@2026-11-27T18:00:00

using System;
using ChipSharp.Components;
using ChipSharp.Hardware;

const int Bus = 0;                  // the Uno's one I2C bus: SDA A4 (pin 18), SCL A5 (pin 19)

I2c.Open(Bus, sda: 18, scl: 19, frequency: 100_000);
if (!Lcd.Open(Bus, 0x27, columns: 16, rows: 2))
{
    Console.WriteLine("No LCD answers at 0x27: check its wiring.");
    return;
}

if (Ds1307.Open(Bus) && Ds1307.Running)
{
    Clock.Start(Ds1307.Read());
    Console.WriteLine($"The time from the DS1307: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
}
else
{
    // no DS1307, or a halted one: set it (when there is one) and the Uno's clock to a date to start from
    var start = new DateTime(2026, 11, 27, 18, 0, 0);
    Ds1307.Write(start);
    Clock.Start(start);
    Console.WriteLine($"No DS1307 running: the clock starts at {start:yyyy-MM-dd HH:mm:ss}");
}

Span<char> text = stackalloc char[16];
while (true)
{
    var now = DateTime.Now;
    if (now.TryFormat(text, out var length, "ddd dd MMM yyyy"))
    {
        Lcd.Line(0, text[..length]);
    }

    if (now.TryFormat(text, out length, "HH:mm:ss"))
    {
        Lcd.Line(1, text[..length]);
    }

    // until the next second starts
    Timing.DelayMilliseconds(1000 - DateTime.Now.Millisecond);
}
