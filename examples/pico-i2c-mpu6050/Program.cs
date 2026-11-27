// Reads an MPU-6050 accelerometer and gyroscope over I2C, and prints what it measures over the serial port (GPIO 0,
// 115200 baud) twice a second, with no heap:
//
//   MPU-6050 at 0x68, WHO_AM_I 0x68
//   acceleration (mg): x 500, y -250, z 1000; rotation (degrees/s): x 10.0, y -5.0, z 0.0; temperature 24.00 C
//   ...
//
// Wiring (diagram.json has it for Wokwi): the MPU-6050's SDA to GP4 (pin 6) and SCL to GP5 (pin 7), I2C0; VCC to
// 3V3, GND to GND, AD0 to GND or open, so that it answers at address 0x68. I2c.Open turns the Pico's pull-ups on,
// which is enough for short wires; longer ones want 4.7 kOhm resistors from SDA and SCL to 3V3.
//
// The MPU-6050 keeps its settings and measurements in 8-bit registers. To read one, the program writes the register's
// number and then reads from it on, both in one transfer with a repeated start between: I2c.WriteRead. The
// measurements are 16-bit values, most significant byte first, which BinaryPrimitives takes apart. Every buffer is a
// span of stackalloc: nothing allocates, so the program needs no heap.
//
// Build it with ChipSharp for the target "pico":
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\PicoI2cMpu6050.dll -t pico
// and run it in the simulator with a simulated MPU-6050 on I2C0:
//   node tools\rp2040-sim\run.mjs chipsharp-output\pico\PicoI2cMpu6050.uf2 --i2c 0:0x68=mpu6050 --ms 1200

using System;
using System.Buffers.Binary;
using ChipSharp.Hardware;

const int Bus = 0;
const int Address = 0x68;
const byte WhoAmI = 0x75;            // reads 0x68
const byte PowerManagement = 0x6B;   // 0x40 after power-up: asleep
const byte Measurements = 0x3B;      // acceleration X, Y, Z, temperature, rotation X, Y, Z: 14 bytes

I2c.Open(Bus, sda: 4, scl: 5, frequency: 400_000);

Span<byte> register = stackalloc byte[1];
Span<byte> identity = stackalloc byte[1];
register[0] = WhoAmI;
if (!I2c.WriteRead(Bus, Address, register, identity))
{
    Console.WriteLine("No MPU-6050 answers at 0x68: check its wiring.");
    return;
}

Console.WriteLine($"MPU-6050 at 0x{Address:X2}, WHO_AM_I 0x{identity[0]:X2}");

// wake it up: clear the SLEEP bit (the ranges stay the smallest: 2 g, 250 degrees per second)
Span<byte> wake = stackalloc byte[2];
wake[0] = PowerManagement;
wake[1] = 0x00;
I2c.Write(Bus, Address, wake);

Span<byte> data = stackalloc byte[14];
register[0] = Measurements;
while (true)
{
    if (I2c.WriteRead(Bus, Address, register, data))
    {
        Report(data);
    }
    else
    {
        Console.WriteLine("The MPU-6050 did not answer.");
    }

    Timing.DelayMilliseconds(500);
}

partial class Program
{
    // In the smallest ranges: 16384 per g, 131 per degree per second; and degrees Celsius = raw / 340 + 36.53.
    static void Report(ReadOnlySpan<byte> data)
    {
        int ax = BinaryPrimitives.ReadInt16BigEndian(data);
        int ay = BinaryPrimitives.ReadInt16BigEndian(data[2..]);
        int az = BinaryPrimitives.ReadInt16BigEndian(data[4..]);
        int temperature = BinaryPrimitives.ReadInt16BigEndian(data[6..]);
        int gx = BinaryPrimitives.ReadInt16BigEndian(data[8..]);
        int gy = BinaryPrimitives.ReadInt16BigEndian(data[10..]);
        int gz = BinaryPrimitives.ReadInt16BigEndian(data[12..]);

        Console.Write($"acceleration (mg): x {ax * 1000 / 16384}, y {ay * 1000 / 16384}, z {az * 1000 / 16384}");
        Console.Write("; rotation (degrees/s): x ");
        WriteTenths(gx * 10 / 131);
        Console.Write(", y ");
        WriteTenths(gy * 10 / 131);
        Console.Write(", z ");
        WriteTenths(gz * 10 / 131);

        // hundredths of a degree: (raw / 340 + 36.53) * 100
        int hundredths = (temperature * 100 + 1_242_020) / 340;
        Console.WriteLine($"; temperature {hundredths / 100}.{hundredths % 100:D2} C");
    }

    // A number of tenths with its one decimal, e.g. -50 as -5.0.
    static void WriteTenths(int tenths)
    {
        if (tenths < 0)
        {
            Console.Write('-');
            tenths = -tenths;
        }

        Console.Write($"{tenths / 10}.{tenths % 10}");
    }
}
