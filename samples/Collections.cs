using System;
using System.Collections.Generic;

// Commands as they come over a serial line, counted, queued, undone and logged: the library's collections (List,
// Dictionary, HashSet, Queue, Stack), foreach, a using of a class that says when it closes, and many short-lived
// collections, which a small heap's garbage collector frees again and again.

class Sensor : IComparable<Sensor>
{
    public readonly string Name;
    public readonly int Reading;

    public Sensor(string name, int reading)
    {
        Name = name;
        Reading = reading;
    }

    public int CompareTo(Sensor other) => Reading.CompareTo(other.Reading);
}

class Session : IDisposable
{
    readonly string name;

    public Session(string name)
    {
        this.name = name;
        Console.WriteLine("session " + name + " open");
    }

    public void Dispose() => Console.WriteLine("session " + name + " closed");
}

class Program
{
    // what came over the line, a command after each ';'
    const string Received = "led on;temp;led off;led on;beep 3;temp;undo;fan 2;undo;temp;led off;beep 1;reset";

    static void Main()
    {
        var counts = new Dictionary<string, int>();
        var jobs = new Queue<string>();
        var history = new Stack<string>();
        var known = new HashSet<string> { "led", "temp", "beep", "fan", "undo" };

        using (new Session("commands"))
        {
            foreach (var line in Received.Split(';'))
            {
                var word = line.Split(' ')[0];
                if (!known.Contains(word))
                {
                    continue;
                }

                counts[word] = counts.GetValueOrDefault(word) + 1;
                if (word == "undo")
                {
                    Console.WriteLine(history.TryPop(out var undone) ? "undo " + undone : "nothing to undo");
                    continue;
                }

                history.Push(line);
                jobs.Enqueue(line);
            }
        }

        foreach (var (word, count) in counts)
        {
            Console.WriteLine(word + ": " + count);
        }

        Console.Write("jobs:");
        while (jobs.TryDequeue(out var job))
        {
            Console.Write(" [" + job + "]");
        }

        Console.WriteLine();
        Console.WriteLine("history " + history.Count + ", last " + history.Peek());

        var sensors = new List<Sensor> { new Sensor("north", 21), new Sensor("south", 17), new Sensor("roof", 30) };
        sensors.Add(new Sensor("cellar", 12));
        sensors.Sort();
        foreach (var sensor in sensors)
        {
            Console.WriteLine(sensor.Name + " " + sensor.Reading);
        }

        Console.WriteLine("warm: " + sensors.FindAll(sensor => sensor.Reading > 20).Count);

        // a day of readings, an hour at a time: each hour's collections are garbage the next hour
        long total = 0;
        for (var hour = 0; hour < 2000; hour++)
        {
            var readings = new List<int>();
            var byLevel = new Dictionary<int, int>();
            for (var minute = 0; minute < 12; minute++)
            {
                var reading = (hour * 31 + minute * 7) % 50;
                readings.Add(reading);
                byLevel[reading / 10] = byLevel.GetValueOrDefault(reading / 10) + 1;
            }

            readings.Sort();
            total += readings[6] + byLevel.Count;
        }

        Console.WriteLine("total " + total);
    }
}
