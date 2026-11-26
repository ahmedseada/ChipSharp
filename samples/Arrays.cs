using System;

// Arrays on the stack: each has a fixed size, lives in the method that makes it, and is passed down to the
// methods that read and fill it (which see its Length too), but never returned.
class Program
{
    static int Dot(int[] w, int[] f)
    {
        int sum = 0;
        for (int i = 0; i < w.Length; i++)
        {
            sum += w[i] * f[i];
        }

        return sum;
    }

    static void FillSquares(int[] values, int start)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (start + i) * (start + i);
        }
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

    // The sieve of Eratosthenes: how many primes are below limit (at most 100)
    static int CountPrimes(int limit)
    {
        bool[] composite = new bool[100];
        int count = 0;
        for (int n = 2; n < limit; n++)
        {
            if (!composite[n])
            {
                count++;
                for (int multiple = n * n; multiple < limit; multiple += n)
                {
                    composite[multiple] = true;
                }
            }
        }

        return count;
    }

    static void Reverse(byte[] bytes)
    {
        for (int i = 0, j = bytes.Length - 1; i < j; i++, j--)
        {
            byte swap = bytes[i];
            bytes[i] = bytes[j];
            bytes[j] = swap;
        }
    }

    static void Main()
    {
        Console.WriteLine(Dot(new[] { 5, -2 }, new[] { 3, 4 }));

        int[] squares = new int[10];
        FillSquares(squares, 1);
        Console.WriteLine(squares.Length);
        Console.WriteLine(squares[9]);
        Console.WriteLine(Sum(squares));

        int[] primes = { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29 };
        Console.WriteLine(Dot(primes, primes));
        Console.WriteLine(CountPrimes(100));

        int[] counts = new int[4];
        for (int i = 0; i < 30; i++)
        {
            counts[i * 7 % 4]++;
        }

        for (int i = 0; i < counts.Length; i++)
        {
            Console.WriteLine(counts[i]);
        }

        byte[] bytes = { 1, 2, 250 };
        Reverse(bytes);
        Console.WriteLine(bytes[0]);
        Console.WriteLine(bytes[0] + bytes[2]);

        long[] longs = { 3_000_000_000, -4_000_000_000, 7 };
        for (long index = 0; index < longs.Length; index++)
        {
            Console.WriteLine(longs[index]);
        }
    }
}
