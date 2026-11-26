using System;

// Arrays in static fields, which need no heap: a table nothing writes is constant data (in flash on a chip), and an
// array the program fills is a fixed array (in RAM), kept from call to call. Here a 7-segment display: which segments
// show each digit, the current each segment draws, and the last readings shown, summed.
class Program
{
    // the segments (bits g f e d c b a) that show each digit
    static readonly byte[] Digits = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };

    // the current each segment, a to g, draws: in milliamps
    static readonly float[] Milliamps = { 4.5f, 4.5f, 4.25f, 4.25f, 4.25f, 4.5f, 5.0f };

    // the last readings shown, oldest overwritten first
    static int[] readings = new int[6];
    static int shown;

    static int Segments(byte pattern)
    {
        int count = 0;
        for (int segment = 0; segment < 7; segment++)
        {
            if ((pattern & (1 << segment)) != 0)
            {
                count++;
            }
        }

        return count;
    }

    static float Current(byte pattern, float[] perSegment)
    {
        float total = 0;
        for (int segment = 0; segment < perSegment.Length; segment++)
        {
            if ((pattern & (1 << segment)) != 0)
            {
                total += perSegment[segment];
            }
        }

        return total;
    }

    // shows a reading of up to 4 digits; returns the current the display draws for it
    static float Show(int reading)
    {
        readings[shown % readings.Length] = reading;
        shown++;
        float current = 0;
        for (int place = 1000; place > 0; place /= 10)
        {
            current += Current(Digits[reading / place % 10], Milliamps);
        }

        return current;
    }

    static long Sum(int[] values)
    {
        long total = 0;
        foreach (int value in values)
        {
            total += value;
        }

        return total;
    }

    static void Main()
    {
        for (int digit = 0; digit < Digits.Length; digit++)
        {
            Console.WriteLine($"{digit}: 0x{Digits[digit]:X2}, {Segments(Digits[digit])} segments");
        }

        int reading = 1234;
        for (int i = 0; i < 9; i++)
        {
            Console.Write($"{reading,4} takes ");
            Console.Write(Show(reading));
            Console.WriteLine($" mA; the last {readings.Length} add up to {Sum(readings)}");
            reading = (reading * 7 + 501) % 10000;
        }
    }
}
