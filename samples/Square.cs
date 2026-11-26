using System;

// The first program ChipSharp runs: a method call, a multiplication and one line of output.
class Program
{
    static int Square(int n)
    {
        return n * n;
    }

    static void Main()
    {
        Console.WriteLine(Square(42));
    }
}
