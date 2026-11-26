using System;

// The greatest common divisor (Euclid) with a loop and by recursion, on int, uint and long,
// and the least common multiple.
class Program
{
    static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            int rest = a % b;
            a = b;
            b = rest;
        }

        return a;
    }

    static int GcdRecursive(int a, int b)
    {
        return b == 0 ? a : GcdRecursive(b, a % b);
    }

    static uint GcdUnsigned(uint a, uint b)
    {
        while (b != 0)
        {
            uint rest = a % b;
            a = b;
            b = rest;
        }

        return a;
    }

    static long GcdLong(long a, long b)
    {
        return b == 0 ? a : GcdLong(b, a % b);
    }

    static long Lcm(long a, long b)
    {
        return a / GcdLong(a, b) * b;
    }

    static void Main()
    {
        Console.WriteLine(Gcd(48, 18));
        Console.WriteLine(Gcd(17, 5));
        Console.WriteLine(GcdRecursive(1071, 462));
        Console.WriteLine(GcdRecursive(0, 9));
        Console.WriteLine(GcdUnsigned(4000000000, 3000000000));
        Console.WriteLine(GcdLong(600851475143, 6857));
        Console.WriteLine(Lcm(21, 6));
        Console.WriteLine(Lcm(1000000007, 998244353));
    }
}
