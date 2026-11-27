using System;

// Physics and sensors the way a microcontroller computes them, with Math and MathF: a projectile's range, flight time
// and height over launch angles (Sin, SinCos, Pow, Asin), a pendulum's period and how its swing dies away (Sqrt, Exp,
// Log), a thermistor read through the Steinhart-Hart equation (Log, Pow, Clamp), a sound level in decibels (Log10) and
// a compass heading from a magnetometer (MathF.Atan2), each reading rounded for a display (Round to digits, by each
// MidpointRounding, Floor, Ceiling, Truncate). It needs no heap.
class Program
{
    const double Gravity = 9.81;   // m/s^2

    static void Main()
    {
        Projectile(20);
        Pendulum(0.75, 0.05);
        Thermistor();
        SoundLevels();
        Headings();
    }

    // A ball thrown at speed (m/s) at angles from 15 to 75 degrees, and the angle that lands it 30 m away.
    static void Projectile(double speed)
    {
        Console.WriteLine($"Projectile at {speed} m/s:");
        for (int degrees = 15; degrees <= 75; degrees += 15)
        {
            double angle = degrees * Math.PI / 180;
            var (sin, cos) = Math.SinCos(angle);
            double range = speed * speed * Math.Sin(2 * angle) / Gravity;
            double time = 2 * speed * sin / Gravity;
            double top = Math.Pow(speed * sin, 2) / (2 * Gravity);
            double across = speed * cos;
            Console.WriteLine($"  {degrees,2} degrees: {Math.Round(range, 2)} m in {Math.Round(time, 3)} s, " +
                $"{Math.Round(top, 2)} m high, {Math.Round(across, 1)} m/s across");
        }

        double aim = Math.Asin(30 * Gravity / (speed * speed)) / 2 * 180 / Math.PI;
        Console.WriteLine($"  to land 30 m away, aim at {Math.Round(aim, 1)} or {Math.Round(90 - aim, 1)} degrees");
    }

    // A pendulum length metres long whose swing dies away by damping per second.
    static void Pendulum(double length, double damping)
    {
        double period = 2 * Math.PI * Math.Sqrt(length / Gravity);
        Console.WriteLine($"Pendulum of {length} m: a swing takes {Math.Round(period, 4)} s");
        for (int swing = 0; swing <= 20; swing += 5)
        {
            double amplitude = 10 * Math.Exp(-damping * swing * period);
            Console.WriteLine($"  after {swing,2} swings: {Math.Round(amplitude, 3, MidpointRounding.AwayFromZero)} degrees");
        }

        Console.WriteLine($"  the swing halves every {Math.Round(Math.Log(2) / damping, 2)} s");
    }

    // A 10 kOhm NTC thermistor under a 10 kOhm resistor, read by a 10-bit ADC; the Steinhart-Hart coefficients of a
    // common part. The temperature is kept within the part's range, -40 to 125 degrees Celsius.
    static void Thermistor()
    {
        const double A = 1.009249522e-03, B = 2.378405444e-04, C = 2.019202697e-07;
        Console.WriteLine("Thermistor:");
        int[] readings = { 20, 180, 300, 512, 700, 900, 1010 };
        foreach (int adc in readings)
        {
            double resistance = 10000.0 * adc / (1023 - adc);
            double lnR = Math.Log(resistance);
            double kelvin = 1 / (A + B * lnR + C * Math.Pow(lnR, 3));
            double celsius = Math.Clamp(kelvin - 273.15, -40, 125);
            Console.WriteLine($"  ADC {adc,4}: {Math.Round(resistance)} Ohm, {Math.Round(celsius, 1)} C " +
                $"(display {Math.Floor(celsius)} to {Math.Ceiling(celsius)}, {Math.Truncate(celsius * 9 / 5 + 32)} F)");
        }
    }

    // Sound pressures in pascals as levels in decibels (20 micropascals is 0 dB), and above 85 dB harmful to the ear.
    static void SoundLevels()
    {
        Console.WriteLine("Sound levels:");
        double[] pressures = { 0.00002, 0.002, 0.0632, 1, 20 };
        foreach (double pressure in pressures)
        {
            double decibels = 20 * Math.Log10(pressure / 0.00002);
            string loudness = Math.Sign(decibels - 85) > 0 ? "harmful" : "safe";
            Console.WriteLine($"  {pressure} Pa: {Math.Round(decibels, 1)} dB, {loudness}");
        }

        // a display that shows whole decibels decides what happens at the half
        double[] halves = { 2.5, 3.5, -2.5, 72.25 };
        foreach (double level in halves)
        {
            Console.WriteLine($"  {level} dB shown: {Math.Round(level, 0)} (to even), " +
                $"{Math.Round(level, 0, MidpointRounding.AwayFromZero)} (away from zero), " +
                $"{Math.Round(level, 0, MidpointRounding.ToZero)} (toward zero), " +
                $"{Math.Round(level, 1, MidpointRounding.ToNegativeInfinity)} (down to a tenth), " +
                $"{Math.Round(level, 1, MidpointRounding.ToPositiveInfinity)} (up to a tenth)");
        }
    }

    // A magnetometer's horizontal field (in microtesla) as a compass heading, in floats: 0 is north, 90 east.
    static void Headings()
    {
        Console.WriteLine("Headings:");
        float[] north = { 30f, 21.2f, 0f, -18.4f, -30f, 5.5f };
        float[] east = { 0f, 21.2f, 30f, 23.7f, -0.5f, -29.1f };
        for (int i = 0; i < north.Length; i++)
        {
            float heading = MathF.Atan2(east[i], north[i]) * 180 / MathF.PI;
            if (heading < 0)
            {
                heading += 360;
            }

            float strength = MathF.Sqrt(north[i] * north[i] + east[i] * east[i]);
            Console.WriteLine($"  ({north[i]}, {east[i]}): {MathF.Round(heading, 1)} degrees, {MathF.Round(strength, 2)} uT");
        }
    }
}
