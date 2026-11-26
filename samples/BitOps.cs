using System;

// Bit operations: & | ^ << >>. A shift uses only the low 5 bits of its count for int and uint (6 for long and
// ulong). >> keeps the sign of an int or long and fills a uint or ulong with zeros.
class Program
{
    static void Main()
    {
        int x = 0x5A;
        int y = 0x0F;
        Console.WriteLine(x & y);
        Console.WriteLine(x | y);
        Console.WriteLine(x ^ y);
        Console.WriteLine();

        int one = 1;
        int count = 33;
        Console.WriteLine(one << 10);
        Console.WriteLine(one << 31);
        Console.WriteLine(one << count);
        int negative = -100;
        Console.WriteLine(negative >> 2);
        Console.WriteLine(negative >> 31);
        Console.WriteLine(negative << 3);
        Console.WriteLine();

        uint u = 4000000000;
        Console.WriteLine(u >> 4);
        Console.WriteLine(u << 1);
        uint allOnes = 4294967295;
        Console.WriteLine(allOnes >> 31);
        Console.WriteLine(allOnes ^ u);
        Console.WriteLine();

        long longOne = 1;
        long big = -1099511627776;
        Console.WriteLine(longOne << 40);
        Console.WriteLine(longOne << 63);
        Console.WriteLine(big >> 20);
        Console.WriteLine(big | 255);
        ulong uBig = 18446744073709551615;
        Console.WriteLine(uBig >> 60);
        Console.WriteLine(uBig ^ 255);
        Console.WriteLine(uBig << count);
        Console.WriteLine();

        uint bits = 4042322160;
        int setBits = 0;
        while (bits != 0)
        {
            if ((bits & 1) != 0)
            {
                setBits++;
            }

            bits = bits >> 1;
        }

        Console.WriteLine(setBits);
        int n = 64;
        Console.WriteLine((n & (n - 1)) == 0);
        n = 96;
        Console.WriteLine((n & (n - 1)) == 0);
    }
}
