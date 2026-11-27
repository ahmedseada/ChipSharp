using System;
using System.Text;

// Records for the readings of a small weather station: a positional record per sensor, records derived from a base
// record, a record struct for a range that needs no heap to compare or copy, readings changed with "with", compared by
// value, printed as C# prints records (nested records too), and a report table built with a StringBuilder.

abstract record Sensor(string Name, int Pin);

record Thermometer(string Name, int Pin, double Offset) : Sensor(Name, Pin);

record Hygrometer(string Name, int Pin) : Sensor(Name, Pin);

readonly record struct Range(int Low, int High)
{
    public bool Contains(int value) => value >= Low && value <= High;
}

record Reading(Sensor Source, int Value, Range Expected)
{
    public bool InRange => Expected.Contains(Value);
}

class Program
{
    static void Main()
    {
        var outside = new Thermometer("outside", 4, -0.5);
        var inside = outside with { Name = "inside", Pin = 5 };
        var air = new Hygrometer("air", 6);
        Console.WriteLine(outside);
        Console.WriteLine(inside);
        Console.WriteLine(air);

        // value equality: the same values are equal records, of the same type only
        Console.WriteLine(outside == new Thermometer("outside", 4, -0.5));
        Console.WriteLine(outside == inside);
        Console.WriteLine(new Hygrometer("x", 1).Equals(new Thermometer("x", 1, 0)));
        Console.WriteLine(outside.GetHashCode() == new Thermometer("outside", 4, -0.5).GetHashCode());

        var comfort = new Range(18, 24);
        var damp = new Range(30, 60);
        Console.WriteLine(comfort);
        Console.WriteLine(comfort with { High = 26 });
        Console.WriteLine(comfort == new Range(18, 24));
        var (low, high) = damp;
        Console.WriteLine(high - low);

        Reading[] readings =
        [
            new(outside, 12, comfort),
            new(inside, 21, comfort),
            new(air, 72, damp),
            new(inside, 25, comfort),
        ];
        Console.WriteLine(readings[0]);
        var corrected = readings[3] with { Value = 23 };
        Console.WriteLine(corrected.InRange + " " + readings[3].InRange);

        var report = new StringBuilder();
        report.AppendLine("sensor      pin  value  ok");
        foreach (var reading in readings)
        {
            report.Append($"{reading.Source.Name,-10}{reading.Source.Pin,5}{reading.Value,7}  ")
                .AppendLine(reading.InRange ? "yes" : "no");
        }

        report.AppendFormat("{0} readings, {1} of them out of range", readings.Length, Count(readings, false));
        Console.WriteLine(report);
        Console.WriteLine(report.Length);

        foreach (var sensor in new Sensor[] { outside, air })
        {
            Console.WriteLine(sensor.GetType().Name + " on pin " + sensor.Pin);
        }
    }

    static int Count(Reading[] readings, bool inRange)
    {
        var count = 0;
        foreach (var reading in readings)
        {
            if (reading.InRange == inRange)
            {
                count++;
            }
        }

        return count;
    }
}
