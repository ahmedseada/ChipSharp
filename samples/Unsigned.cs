using System;

// uint and ulong: numbers above int.MaxValue and long.MaxValue, wrap-around below zero and past the top,
// and unsigned division and remainder.
class Program
{
    static void Main()
    {
        uint zero = 0;
        Console.WriteLine(zero - 1);
        uint big = 3000000000;
        Console.WriteLine(big);
        Console.WriteLine(big + big);
        Console.WriteLine(big / 7);
        Console.WriteLine(big % 7);
        uint product = 65536;
        Console.WriteLine(product * product);
        Console.WriteLine();

        ulong max = 18446744073709551615;
        Console.WriteLine(max);
        Console.WriteLine(max / 10);
        Console.WriteLine(max % 10);
        Console.WriteLine(max + 2);
        ulong none = 0;
        Console.WriteLine(none - 1);
        ulong half = 9223372036854775808;
        Console.WriteLine(half);
        Console.WriteLine(half / 3);
        Console.WriteLine();

        for (uint i = 3; i > 0; i--)
        {
            Console.WriteLine(i);
        }
    }
}
