using System;

// A crossroads' traffic counts as a report: text built and printed straight away, with $"...", + and ToString, and no
// string ever made. Each part is written where the text is printed. Like .NET, every hole is worked out first, in
// order, and the text printed after, so a hole that prints something itself prints before its line.
class Program
{
    const int Roads = 4;
    static int checks;

    static string Name(int road) => road switch
    {
        1 => "north",
        2 => "east",
        3 => "south",
        4 => "west",
        _ => null,
    };

    static int Cars(int road) => road * 37 % 23 + road;

    // a hole with a side effect: it prints, and counts
    static int Checked(int value)
    {
        checks++;
        Console.Write($"[check {checks}] ");
        return value;
    }

    static void Main()
    {
        int lane = 1;
        Console.WriteLine($"road : {lane}");
        Console.WriteLine("road : " + (lane + 1));

        int total = 0;
        int busiest = 1;
        for (int road = 1; road <= Roads; road++)
        {
            total += Cars(road);
            busiest = Cars(road) > Cars(busiest) ? road : busiest;
        }

        Console.WriteLine($"{"road",-6}|{"cars",5}|{"busy",-6}| share");
        for (int road = 1; road <= Roads; road++)
        {
            int cars = Cars(road);
            bool busy = cars > total / Roads;
            Console.WriteLine($"{Name(road),-6}|{cars,5}|{busy,-6}| {cars * 100f / total} %");
        }

        int average = total / Roads;
        Console.Write("against the average: ");
        for (int road = 1; road <= Roads; road++)
        {
            Console.Write($"{Cars(road) - average:D3} ");
        }

        Console.WriteLine();
        Console.WriteLine($"sensors: 0x{busiest * 4096 + total:X4}, 0x{-busiest:x}, 0x{(sbyte)-busiest:X2}");
        Console.WriteLine($"checked {Checked(total)} cars on {Checked(Roads)} roads");
        Console.WriteLine("total " + total + ", busiest " + Name(busiest) + ", " + (total > 100 ? "a busy" : "a quiet")
            + " day: " + (total > 100));
        Console.WriteLine($"road 5 is [{Name(5)}], [{Name(5),4}] wide");
        Console.WriteLine($"at this rate: {total * 60L * 24 * 365} cars a year, {total / 60.0} a second");
        Console.WriteLine("" + total);
        Console.WriteLine(lane.ToString() + busiest.ToString("D2") + (lane < busiest).ToString());
    }
}
