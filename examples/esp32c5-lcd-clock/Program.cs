// A clock on an ESP32-C5-DevKitC-1: the date and the time on a 16x2 character LCD, once a second,
//
//   +----------------+
//   |Fri 27 Nov 2026 |
//   |18:00:05        |
//   +----------------+
//
// its time from a DS1307 real-time clock, which keeps it on its battery while the board has no power. The program
// reads the DS1307 once, at start-up, and starts the chip's own clock from it (Clock.Start): from then on
// DateTime.Now is that time plus what the timer has counted since, so the DS1307 is not read again. A DS1307 that does
// not answer or does not run (a new one is halted) is set to a date to start from, and starts. On the USB console it
// says where the time came from.
//
// The C5 has a heap, so DateTime and its format strings are used as on a PC (examples\uno-lcd-clock, the Uno's version,
// writes into a span instead). The portable API alone: for a Pico, change the pin numbers and the bus and build with
// -t pico.
//
// Wiring on the ESP32-C5-DevKitC-1 (pins labelled by their GPIO number), both parts on I2C bus 0 (diagram.json has it
// for Wokwi: a wokwi-lcd1602 with "pins": "i2c" and a wokwi-ds1307):
//   SDA to 4, SCL to 5, VCC to 5V, GND to GND (the DS1307 module wants 5 V; a module's I2C lines are pulled up to its
//   supply, which must not be 5 V for the C5's 3.3 V pins: take modules whose pull-ups go to 3V3, or power them from
//   3V3 if they work on it)
// 4 and 5 are the JTAG pins MTCK and MTDO, free while JTAG is on USB (its default). I2c.Open turns the pins' pull-ups on.
//
// Build it with ChipSharp for the target "esp32c5-devkitc", then flash it through either USB-C port (see the README):
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\Esp32c5LcdClock.dll -t esp32c5-devkitc
//   chipsharp flash bin\Release\net10.0\Esp32c5LcdClock.dll -t esp32c5-devkitc
// and run it in the simulator with a simulated LCD and a simulated DS1307 (from the PC's time, or from a date):
//   chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5LcdClock.bin --i2c 0:0x27=lcd1602 --i2c 0:0x68=ds1307 --ms 3500
//   ... --i2c 0:0x68=ds1307@2026-11-27T18:00:00

using System;
using ChipSharp.Components;
using ChipSharp.Hardware;

const int Bus = 0;                  // the C5's one I2C bus, here on SDA GPIO 4 and SCL GPIO 5

I2c.Open(Bus, sda: 4, scl: 5, frequency: 100_000);
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
    // no DS1307, or a halted one: set it (when there is one) and the chip's clock to a date to start from
    var start = new DateTime(2026, 11, 27, 18, 0, 0);
    Ds1307.Write(start);
    Clock.Start(start);
    Console.WriteLine($"No DS1307 running: the clock starts at {start:yyyy-MM-dd HH:mm:ss}");
}

while (true)
{
    var now = DateTime.Now;
    Lcd.Line(0, now.ToString("ddd dd MMM yyyy"));
    Lcd.Line(1, now.ToString("HH:mm:ss"));

    // until the next second starts
    Timing.DelayMilliseconds(1000 - DateTime.Now.Millisecond);
}
