using System;

// && and || stop as soon as the answer is known: the right side runs only when it is needed.
// Check prints its number before answering, so the output shows which sides ran.
class Program
{
    static bool Check(int id, bool answer)
    {
        Console.WriteLine(id);
        return answer;
    }

    static void Main()
    {
        Console.WriteLine(Check(1, false) && Check(2, true));
        Console.WriteLine(Check(3, true) && Check(4, false));
        Console.WriteLine(Check(5, true) || Check(6, false));
        Console.WriteLine(Check(7, false) || Check(8, true));
        Console.WriteLine(Check(9, false) && Check(10, true) || Check(11, true));
        Console.WriteLine(Check(12, true) || Check(13, true) && Check(14, false));
        Console.WriteLine();

        int zero = 0;
        if (zero != 0 && 10 / zero > 1)
        {
            Console.WriteLine(1);
        }
        else
        {
            Console.WriteLine(0);
        }

        Console.WriteLine(zero == 0 || 10 / zero > 1);
        Console.WriteLine(!(Check(15, true) && Check(16, true)));
    }
}
