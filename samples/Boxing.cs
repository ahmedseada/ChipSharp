// Boxing: values as objects. A log of readings of mixed kinds (numbers, an enum, a struct, text) kept in an object[],
// sorted out with a switch on types; a struct used through an interface (a box, which a call changes); Equals and
// GetHashCode on boxed values; nullable readings with lifted arithmetic and ??; objects in formatted text.
using System;

enum Alarm { None, Low, High }

interface ISensor
{
    string Name { get; }

    int Read();
}

struct Thermometer : ISensor
{
    public int Reads;

    public string Name => "thermometer";

    public int Read()
    {
        Reads++;
        return 18 + Reads;
    }
}

struct Sample
{
    public int Channel;
    public double Volts;

    public Sample(int channel, double volts)
    {
        Channel = channel;
        Volts = volts;
    }

    public override string ToString() => $"ch{Channel}={Volts}V";
}

class Program
{
    static string Describe(object entry) => entry switch
    {
        null => "(nothing)",
        int count when count > 100 => "many: " + count,
        int count => "count: " + count,
        double level => "level: " + level,
        Alarm alarm => "alarm: " + alarm,
        Sample sample => "sample on channel " + sample.Channel,
        string note => "note: " + note,
        _ => "other: " + entry,
    };

    static int? Average(int?[] readings)
    {
        int? total = 0;
        int count = 0;
        foreach (var reading in readings)
        {
            if (reading.HasValue)
            {
                total += reading;
                count++;
            }
        }

        return count == 0 ? null : total / count;
    }

    static void Main()
    {
        object[] log = { 3, 250, 1.5, Alarm.High, new Sample(2, 3.25), "door open", null, 'x' };
        foreach (var entry in log)
        {
            Console.WriteLine(Describe(entry));
        }

        // a struct through an interface: the box is what the calls change
        var thermometer = new Thermometer();
        ISensor sensor = thermometer;
        sensor.Read();
        Console.WriteLine($"{sensor.Name}: {sensor.Read()} after {((Thermometer)sensor).Reads} reads, the struct itself {thermometer.Reads}");

        // Equals of boxes compares values of the same type; GetHashCode is the same for equal values
        object a = 42, b = 42, c = 42L;
        Console.WriteLine($"{a.Equals(b)} {a.Equals(c)} {ReferenceEquals(a, b)} {Equals(a, b)} {a.GetHashCode() == b.GetHashCode()}");
        object s1 = new Sample(1, 0.5), s2 = new Sample(1, 0.5);
        Console.WriteLine($"{s1.Equals(s2)} {s1.Equals(new Sample(1, 0.25))} {s1.GetHashCode() == s2.GetHashCode()}");
        Console.WriteLine((int)(object)Alarm.Low + " " + (Alarm)(object)2 + " " + (log[3] is int) + " " + (log[3] is Alarm));

        // nullables: no heap, lifted arithmetic and comparisons, ??
        int?[] readings = { 20, null, 24, null, 25 };
        int? average = Average(readings);
        int? missing = Average(new int?[] { null });
        Console.WriteLine($"average {average}, missing [{missing}], or {missing ?? -1}, above 22: {average > 22}");
        object boxed = missing;
        Console.WriteLine($"{boxed == null} {(object)average} {((int?)(object)average).Value}");

        // objects in formatted text, as .NET writes them
        Console.WriteLine(string.Format("{0} | {1,6} | {2} | {3:X4} | {4}", log[4], Alarm.Low, average, 255, sensor.Name));
        Console.WriteLine("{0}, {1}, {2}, {3}", log[0], log[2], log[5], log[6]);
    }
}
