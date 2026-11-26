using System;

// Factorials in long, by recursion and with a do-while loop. 20! is the largest that fits in a long;
// 21! wraps around.
class Program
{
    static long Factorial(int n)
    {
        return n <= 1 ? 1 : n * Factorial(n - 1);
    }

    static long FactorialLoop(int n)
    {
        long result = 1;
        int i = 1;
        do
        {
            result = result * i;
            i++;
        } while (i <= n);

        return result;
    }

    static void Main()
    {
        Console.WriteLine(Factorial(0));
        Console.WriteLine(Factorial(5));
        Console.WriteLine(Factorial(12));
        Console.WriteLine(Factorial(13));
        Console.WriteLine(Factorial(20));
        Console.WriteLine(FactorialLoop(20));
        Console.WriteLine(FactorialLoop(21));
    }
}
