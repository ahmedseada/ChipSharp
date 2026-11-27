using System;

// Arrays on the heap: made where their size is known only when the program runs, returned from the method that makes
// them, kept in a static field and replaced, null until there is one, arrays of arrays, and many that are used once,
// whose memory the garbage collector takes back. Here a logger of sensor readings: each channel keeps its readings in
// an array of its own length, every window of readings is smoothed into a new array, and the latest kept window lives
// in a static field.
class Program
{
    // the latest window kept: null until the first one
    static int[] latest;
    static int kept;

    // count readings of a made-up sensor on the channel
    static int[] Read(int channel, int count)
    {
        int[] readings = new int[count];
        int value = channel * 100 + 7;
        for (int i = 0; i < count; i++)
        {
            value = (value * 37 + 11) % 1000;
            readings[i] = value;
        }

        return readings;
    }

    // each reading the average of it and the two before it, in a new array
    static int[] Smooth(int[] readings)
    {
        int[] smooth = new int[readings.Length];
        for (int i = 0; i < readings.Length; i++)
        {
            int sum = readings[i];
            int count = 1;
            for (int back = 1; back <= 2 && back <= i; back++)
            {
                sum += readings[i - back];
                count++;
            }

            smooth[i] = sum / count;
        }

        return smooth;
    }

    // the largest value, or -1 for no array or an empty one
    static int Max(int[] values)
    {
        if (values == null || values.Length == 0)
        {
            return -1;
        }

        int max = values[0];
        foreach (int value in values)
        {
            if (value > max)
            {
                max = value;
            }
        }

        return max;
    }

    static void Keep(int[] window)
    {
        latest = window;
        kept++;
    }

    static int[] Channel(int[][] channels, int number) => number < channels.Length ? channels[number] : null;

    static void Main()
    {
        Console.WriteLine($"latest: max {Max(latest)}, none yet: {latest == null}");

        // one array of readings per channel, each of its own length
        int[][] channels = new int[4][];
        for (int c = 0; c < channels.Length; c++)
        {
            channels[c] = Read(c, 3 + 2 * c);
        }

        foreach (int[] readings in channels)
        {
            Console.WriteLine($"{readings.Length} readings, last {readings[readings.Length - 1]}, max {Max(readings)}");
        }

        // a window a round, smoothed into a new array: all but the kept ones are garbage at once
        long total = 0;
        for (int round = 0; round < 2000; round++)
        {
            int[] window = Smooth(Read(round % 4, 40));
            total += window[39];
            if (round % 500 == 0)
            {
                Keep(window);
            }
        }

        Console.WriteLine($"total {total}, {kept} windows kept, the latest's max {Max(latest)}");

        // the channels lived through every collection
        int first = 0;
        foreach (int[] readings in channels)
        {
            first += readings[0];
        }

        Console.WriteLine($"first readings: {first}");

        // a channel that is not there is null
        int[] missing = Channel(channels, 9);
        Console.WriteLine(missing == null ? "channel 9: none" : "channel 9: there");
        Console.WriteLine($"its length, or 0: {missing?.Length ?? 0}");
        Console.WriteLine($"channel 2 again: {Channel(channels, 2).Length} readings");

        int[] none = [];
        Console.WriteLine($"an empty window: max {Max(none)}");
    }
}
