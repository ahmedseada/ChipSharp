using System;

// Prime numbers, with break (stop at the first divisor, or at the limit) and continue (skip what cannot be prime).
class Program
{
    static bool IsPrime(int n)
    {
        if (n < 2)
        {
            return false;
        }

        bool prime = true;
        for (int d = 2; d * d <= n; d++)
        {
            if (n % d == 0)
            {
                prime = false;
                break;
            }
        }

        return prime;
    }

    static void Main()
    {
        for (int n = 0; n < 30; n++)
        {
            if (n != 2 && n % 2 == 0)
            {
                continue;
            }

            if (IsPrime(n))
            {
                Console.WriteLine(n);
            }
        }

        Console.WriteLine();

        int count = 0;
        int candidate = 1;
        while (true)
        {
            candidate++;
            if (candidate >= 1000)
            {
                break;
            }

            if (!IsPrime(candidate))
            {
                continue;
            }

            count++;
        }

        Console.WriteLine(count);

        int next = 10000;
        do
        {
            next++;
        } while (!IsPrime(next));

        Console.WriteLine(next);
    }
}
