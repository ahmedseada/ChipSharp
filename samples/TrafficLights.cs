using System;

// The traffic lights of a crossroads, as a log: each road gets its green in turn, and a car waiting on another road
// cuts a long green short. Fixed text (string literals, in flash on a chip) printed between the numbers, part by part
// with Console.Write, the end of each line with Console.WriteLine.
class Program
{
    const int Roads = 4;
    const int LongGreen = 30;
    static readonly string Title = "crossroads";

    static string Name(int road) => road switch
    {
        1 => "north",
        2 => "east",
        3 => "south",
        4 => "west",
        _ => null,
    };

    static void Main()
    {
        Console.WriteLine(Title);
        Console.WriteLine("");

        int waiting = 3;
        int cycle = 0;
        for (int road = 1; road <= Roads; road++)
        {
            Console.Write("road : ");
            Console.Write(road);
            Console.Write(" (");
            Console.Write(Name(road));
            Console.WriteLine(")");

            int seconds = road % 2 == 1 ? LongGreen : LongGreen / 2;
            bool cut = seconds == LongGreen && waiting != road;
            if (cut)
            {
                seconds -= 17;
            }

            Console.Write("    green ");
            Console.WriteLine(seconds);
            Console.Write("    cut short ");
            Console.WriteLine(cut);
            if (cut)
            {
                Console.Write("lane : ");
                Console.Write(road);
                Console.Write(" interrupted, move to lane ");
                Console.WriteLine(waiting);
            }

            cycle += seconds;
        }

        Console.Write("cycle ");
        Console.Write(cycle);
        Console.Write(" s, ");
        Console.Write(cycle / (float)Roads);
        Console.WriteLine(" s a road");
        Console.Write("against a 90 s cycle: ");
        Console.WriteLine(cycle - 90);
        Console.Write("road 5 is [");
        Console.Write(Name(5));
        Console.Write("");
        Console.WriteLine("], no road at all");
        Console.WriteLine(Name(5) == null);
    }
}
