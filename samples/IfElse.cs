using System;

// if and else: alone, chained and nested.
class Program
{
    static int Sign(int n)
    {
        if (n < 0)
        {
            return -1;
        }
        else if (n > 0)
        {
            return 1;
        }

        return 0;
    }

    static int Max3(int a, int b, int c)
    {
        int max = a;
        if (b > max)
        {
            max = b;
        }

        if (c > max)
        {
            max = c;
        }

        return max;
    }

    static int Grade(int score)
    {
        if (score >= 90)
        {
            return 1;
        }
        else if (score >= 75)
        {
            return 2;
        }
        else if (score >= 50)
        {
            return 3;
        }
        else
        {
            return 4;
        }
    }

    static bool IsLeapYear(int year)
    {
        if (year % 4 == 0)
        {
            if (year % 100 == 0)
            {
                return year % 400 == 0;
            }

            return true;
        }

        return false;
    }

    static void Main()
    {
        Console.WriteLine(Sign(-12));
        Console.WriteLine(Sign(0));
        Console.WriteLine(Sign(5));
        Console.WriteLine(Max3(3, 9, 4));
        Console.WriteLine(Max3(-1, -8, -3));
        Console.WriteLine(Max3(7, 7, 2));
        Console.WriteLine(Grade(95));
        Console.WriteLine(Grade(75));
        Console.WriteLine(Grade(60));
        Console.WriteLine(Grade(12));
        Console.WriteLine(IsLeapYear(1900));
        Console.WriteLine(IsLeapYear(2000));
        Console.WriteLine(IsLeapYear(2024));
        Console.WriteLine(IsLeapYear(2025));
    }
}
