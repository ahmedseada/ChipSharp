using System;

// Comparisons give bools, and Console.WriteLine prints a bool as True or False. uint and ulong compare as
// unsigned numbers: 4000000000 is more than 1 even though, read as an int, its bits are negative.
class Program
{
    static void Main()
    {
        int a = 3;
        int b = 7;
        Console.WriteLine(a < b);
        Console.WriteLine(a > b);
        Console.WriteLine(a <= 3);
        Console.WriteLine(a >= b);
        Console.WriteLine(a == b);
        Console.WriteLine(a != b);
        Console.WriteLine(!(a < b));
        Console.WriteLine();

        int negative = -1;
        Console.WriteLine(negative < 1);
        uint large = 4000000000;
        Console.WriteLine(large > 1);
        Console.WriteLine(large < 5);
        long x = -5000000000;
        long y = 5000000000;
        Console.WriteLine(x < y);
        Console.WriteLine(x >= y);
        ulong huge = 18446744073709551615;
        Console.WriteLine(huge > 1);
        Console.WriteLine(huge == 0);
        Console.WriteLine();

        bool yes = a < b;
        bool no = a == b;
        Console.WriteLine(yes);
        Console.WriteLine(no);
        Console.WriteLine(!no);
        Console.WriteLine(yes == no);
        Console.WriteLine(yes != no);
    }
}
