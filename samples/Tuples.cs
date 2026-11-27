using System;
using System.Collections.Generic;

// A crossroads counted by its sensors, with tuples: readings as (lane, cars), the busiest and quietest lane returned
// together, a light's next state from a switch on (light, waiting), a grid of sensors keyed by (row, column), and
// collection expressions of lanes and of their names.

enum Light { Red, Green, Yellow }

class Lane
{
    public Lane(string name, int limit)
    {
        Name = name;
        Limit = limit;
    }

    public string Name { get; }

    public int Limit { get; }

    public override string ToString() => Name;
}

class Program
{
    static (int Lane, int Cars) Busiest((int Lane, int Cars)[] readings)
    {
        var best = readings[0];
        foreach (var reading in readings)
        {
            if (reading.Cars > best.Cars)
            {
                best = reading;
            }
        }

        return best;
    }

    static (int Min, int Max, int Total) Summary((int Lane, int Cars)[] readings)
    {
        var (min, max, total) = (int.MaxValue, 0, 0);
        foreach (var (_, cars) in readings)
        {
            (min, max, total) = (cars < min ? cars : min, cars > max ? cars : max, total + cars);
        }

        return (min, max, total);
    }

    static (Light, int) Next(Light light, int waiting) => (light, waiting) switch
    {
        (Light.Red, > 3) => (Light.Green, 8),
        (Light.Red, _) => (Light.Red, 2),
        (Light.Green, 0) => (Light.Yellow, 1),
        (Light.Green, _) => (Light.Green, 4),
        _ => (Light.Red, 6),
    };

    static void Main()
    {
        (int Lane, int Cars)[] readings = [(1, 12), (2, 31), (3, 7), (4, 19)];
        var busiest = Busiest(readings);
        Console.WriteLine($"busiest lane: {busiest.Lane} with {busiest.Cars} cars");
        var (min, max, total) = Summary(readings);
        Console.WriteLine($"min {min}, max {max}, total {total}");
        Console.WriteLine(Summary(readings));

        // the light, step by step: the switch on a tuple, and its result deconstructed into the same variables
        var (light, seconds) = (Light.Red, 0);
        int[] waiting = [0, 5, 2, 0, 1, 4];
        foreach (var cars in waiting)
        {
            (light, seconds) = Next(light, cars);
            Console.Write($"{light} {seconds}s; ");
        }

        Console.WriteLine();

        // swap two lanes' counts, and compare tuples element by element
        var (north, south) = (readings[0].Cars, readings[1].Cars);
        (north, south) = (south, north);
        Console.WriteLine($"north {north}, south {south}");
        Console.WriteLine((north, south) == (31, 12));
        Console.WriteLine(readings[2] != (3, 7));
        Console.WriteLine(readings[0].Equals((1, 12)) + " " + readings[0].CompareTo((1, 13)));
        Console.WriteLine(readings[3].GetHashCode() == (4, 19).GetHashCode());

        // a grid of sensors keyed by (row, column)
        var grid = new Dictionary<(int Row, int Column), int>();
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                grid[(row, column)] = row * 10 + column;
            }
        }

        Console.WriteLine(grid[(2, 1)] + " " + grid.ContainsKey((3, 0)));
        foreach (var ((row, column), value) in grid)
        {
            if (row == column)
            {
                Console.Write(value + " ");
            }
        }

        Console.WriteLine();

        // collection expressions of objects and of text, with spreads
        Lane[] main = [new Lane("north", 50), new Lane("south", 50)];
        List<Lane> lanes = [.. main, new Lane("east", 30)];
        List<string> names = ["bus", .. NamesOf(lanes)];
        Console.WriteLine(string.Join(", ", names.ToArray()) + " (" + names.Count + ")");

        // lanes sorted by how full they are, as (lane, share) tuples
        var load = new List<(int Percent, Lane Lane)>();
        for (var i = 0; i < lanes.Count; i++)
        {
            load.Add((readings[i].Cars * 100 / lanes[i].Limit, lanes[i]));
        }

        load.Sort((a, b) => b.Percent.CompareTo(a.Percent));
        foreach (var entry in load)
        {
            Console.WriteLine(entry);
        }

        var fullest = load[0];
        Console.WriteLine($"fullest: {fullest.Lane.Name} at {fullest.Percent}%");
    }

    static List<string> NamesOf(List<Lane> lanes)
    {
        List<string> names = [];
        foreach (var lane in lanes)
        {
            names.Add(lane.Name);
        }

        return names;
    }
}
