// A clock: the date and the time on a 16x2 character LCD, once a second,
//
//   +----------------+
//   |Fri 27 Nov 2026 |
//   |18:00:05        |
//   +----------------+
//
// its time from a DS1307 real-time clock, which keeps it on its battery while the board has no power. The program
// reads the DS1307 once, at start-up, and starts the Pico's own clock from it (Clock.Start): from then on
// DateTime.Now is that time plus what the Pico's timer has counted since, so the DS1307 is not read again. A DS1307
// that does not answer or does not run (a new one is halted) is set to a date to start from, and starts. Over the
// serial port (GPIO 0, 115200 baud) it says where the time came from.
//
// No heap: the text of the date is written into a span on the stack with TryFormat, and the LCD shows the span;
// $"{now:...}" printed straight away makes no string either.
//
// Wiring (diagram.json has it for Wokwi: a wokwi-lcd1602 with "pins": "i2c" and a wokwi-ds1307), both on I2C1: SDA to
// GP26 (pin 31), SCL to GP27 (pin 32), VCC (5V on the DS1307) to 3V3, GND to GND. I2c.Open turns the Pico's pull-ups
// on. The DS1307 is a 5 V part: a real one needs 4.5 V or more to keep time from its supply (on VBUS), and then the
// module's pull-ups take SDA and SCL to 5 V, more than the RP2040's pins stand: take them off, or use a level shifter
// (or a DS3231, which works at 3.3 V and keeps the same registers).
//
// Build it with ChipSharp for the target "pico":
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\PicoClock.dll -t pico
// and run it in the simulator with a simulated LCD and a simulated DS1307 (from the PC's time, or from a date):
//   node tools\rp2040-sim\run.mjs chipsharp-output\pico\PicoClock.uf2 --i2c 1:0x27=lcd1602 --i2c 1:0x68=ds1307 --ms 3500
//   ... --i2c 1:0x68=ds1307@2026-11-27T18:00:00

using System;
using ChipSharp.Components;
using ChipSharp.Hardware;

const int Bus = 1;

I2c.Open(Bus, sda: 26, scl: 27, frequency: 100_000);
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
    // no DS1307, or a halted one: set it (when there is one) and the Pico's clock to a date to start from
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
