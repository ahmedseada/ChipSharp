using System;

// + - * / % on int and long with negative numbers. Division rounds toward zero, the remainder takes the sign
// of the left side, and overflow wraps around (C# does not check it unless asked to).
class Program
{
    static void Main()
    {
        int a = 17;
        int b = -5;
        Console.WriteLine(a + b);
        Console.WriteLine(a - b);
        Console.WriteLine(a * b);
        Console.WriteLine(a / b);
        Console.WriteLine(a % b);
        Console.WriteLine(b / 2);
        Console.WriteLine(b % 2);
        Console.WriteLine(b - a - a);
        Console.WriteLine();

        int max = 2147483647;
        int min = -2147483648;
        Console.WriteLine(max + 1);
        Console.WriteLine(min - 1);
        Console.WriteLine(max * 2);
        int big = 100000;
        Console.WriteLine(big * big);
        Console.WriteLine();

        long wide = big;
        Console.WriteLine(wide * wide);
        long longMax = 9223372036854775807;
        Console.WriteLine(longMax + 1);
        long negative = -1234567890123;
        Console.WriteLine(negative / 1000);
        Console.WriteLine(negative % 1000);
        Console.WriteLine(negative * 3);
        Console.WriteLine(negative + negative - 7);
    }
}
