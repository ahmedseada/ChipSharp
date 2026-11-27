using System;

// The methods of System.Array: a sensor's readings go into a ring buffer, which Array.Copy moves along (the copy
// overlaps itself) and Array.Clear empties; the median of a window comes from Array.Sort of a copy of it; Array.Fill
// marks the readings out of range, Array.IndexOf and Array.LastIndexOf find them, and Array.BinarySearch looks a
// reading up in a sorted table of thresholds. A log that grows with Array.Resize and is copied with Clone needs a heap;
// the rest works on any chip.
enum Level : byte { Low, Normal, High }

class Program
{
    // in flash on a chip: only read (IndexOf and BinarySearch write nothing)
    static readonly int[] Thresholds = { 100, 250, 400, 700 };

    static int[] log = new int[2];
    static int logged;

    static int seed = 17;

    static int Reading()
    {
        seed = (seed * 73 + 41) % 997;
        return seed;
    }

    static void Print(int[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            Console.Write(values[i]);
            Console.Write(" ");
        }

        Console.WriteLine();
    }

    static void Print(Level[] levels)
    {
        for (int i = 0; i < levels.Length; i++)
        {
            Console.Write((int)levels[i]);
            Console.Write(" ");
        }

        Console.WriteLine();
    }

    // the median of a window: sorted in a copy, so the window keeps its order
    static int Median(int[] window, int[] scratch)
    {
        Array.Copy(window, scratch, window.Length);
        Array.Sort(scratch);
        return scratch[scratch.Length / 2];
    }

    static void Log(int value)
    {
        if (logged == log.Length)
        {
            Array.Resize(ref log, log.Length * 2);
        }

        log[logged++] = value;
    }

    static void Main()
    {
        int[] window = new int[7];
        int[] scratch = new int[7];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = Reading();
        }

        Print(window);
        for (int round = 0; round < 5; round++)
        {
            // the oldest reading drops out at the front: the copy overlaps, as memmove does
            Array.Copy(window, 1, window, 0, window.Length - 1);
            window[window.Length - 1] = Reading();
            int median = Median(window, scratch);
            int band = Array.BinarySearch(Thresholds, median);
            Console.WriteLine($"median {median}, band {(band >= 0 ? band : ~band)}");
            Log(median);
        }

        Print(window);
        Print(scratch);

        // a level for each reading
        Level[] levels = new Level[window.Length];
        Array.Fill(levels, Level.Normal);
        for (int i = 0; i < window.Length; i++)
        {
            if (window[i] < Thresholds[0])
            {
                levels[i] = Level.Low;
            }
            else if (window[i] > Thresholds[3])
            {
                levels[i] = Level.High;
            }
        }

        Print(levels);
        Console.WriteLine($"first high {Array.IndexOf(levels, Level.High)}, last low {Array.LastIndexOf(levels, Level.Low)}");
        Array.Reverse(levels);
        Print(levels);

        // the log grew by itself; a copy of it is kept, and the buffer cleared for the next window
        int[] kept = (int[])log.Clone();
        Array.Clear(window);
        Print(window);
        Console.WriteLine($"{logged} logged in {log.Length}");
        Print(kept);
    }
}
