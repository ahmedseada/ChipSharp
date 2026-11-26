using System;

// while loops: the sum of 1 to 100, the digits of a number, and the Collatz steps from 27 down to 1.
class Program
{
    static void Main()
    {
        int sum = 0;
        int i = 1;
        while (i <= 100)
        {
            sum = sum + i;
            i = i + 1;
        }

        Console.WriteLine(sum);

        int n = 1234567;
        int digits = 0;
        int digitSum = 0;
        while (n > 0)
        {
            digitSum = digitSum + n % 10;
            n = n / 10;
            digits = digits + 1;
        }

        Console.WriteLine(digits);
        Console.WriteLine(digitSum);

        int x = 27;
        int steps = 0;
        int highest = x;
        while (x != 1)
        {
            if (x % 2 == 0)
            {
                x = x / 2;
            }
            else
            {
                x = 3 * x + 1;
            }

            if (x > highest)
            {
                highest = x;
            }

            steps = steps + 1;
        }

        Console.WriteLine(steps);
        Console.WriteLine(highest);
    }
}
