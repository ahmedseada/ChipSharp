using System;

// Fibonacci numbers by recursion, with the conditional operator ?: choosing between the two cases.
class Program
{
    static int Fib(int n)
    {
        return n < 2 ? n : Fib(n - 1) + Fib(n - 2);
    }

    static void Main()
    {
        for (int i = 0; i <= 12; i++)
        {
            Console.WriteLine(Fib(i));
        }

        Console.WriteLine(Fib(25));
    }
}
