using System;

// for loops, one inside another: a small multiplication table, a count of pairs, a loop that steps by 3,
// and three nested loops counting triangles.
class Program
{
    static void Main()
    {
        for (int row = 1; row <= 3; row++)
        {
            for (int column = 1; column <= 4; column++)
            {
                Console.WriteLine(row * column);
            }

            Console.WriteLine();
        }

        int pairs = 0;
        for (int a = 1; a <= 20; a++)
        {
            for (int b = a; b <= 20; b++)
            {
                if ((a + b) % 7 == 0)
                {
                    pairs++;
                }
            }
        }

        Console.WriteLine(pairs);

        long total = 0;
        for (int i = 10; i > 0; i = i - 3)
        {
            total = total + i;
        }

        Console.WriteLine(total);

        int triangles = 0;
        for (int x = 1; x < 10; x++)
        {
            for (int y = x; y < 10; y++)
            {
                for (int z = y; z < 10; z++)
                {
                    if (x + y > z)
                    {
                        triangles++;
                    }
                }
            }
        }

        Console.WriteLine(triangles);
    }
}
