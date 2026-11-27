using System;

// Delegates, lambdas and events: a thermostat calls back whoever listens when the temperature crosses its limit (an
// event with two subscribers, one of which unsubscribes), a closure counts the alarms, lambdas made in a loop each keep
// their own loop variable, readings are sorted with a comparison and filtered with Array.FindAll, and a burst of
// short-lived closures makes the garbage collector run on a chip. Delegates are objects, so this needs a heap.
class Thermostat
{
    public event Action<int> TooHot;

    public int Limit;

    public Thermostat(int limit) => Limit = limit;

    public void Read(int temperature)
    {
        if (temperature > Limit)
        {
            TooHot?.Invoke(temperature);
        }
    }
}

class Fan
{
    public int Speed;

    public void Faster(int temperature) => Speed += temperature / 10;
}

class Program
{
    static Func<int> Counter()
    {
        var count = 0;
        return () => ++count;
    }

    static int Apply(Func<int, int> step, int value, int times)
    {
        for (int i = 0; i < times; i++)
        {
            value = step(value);
        }

        return value;
    }

    static void Main()
    {
        // callbacks: a static method, a lambda and an instance method, called through one type
        Func<int, int> twice = x => x * 2;
        Console.WriteLine(Apply(twice, 1, 10));
        Console.WriteLine(Apply(x => x + 3, 0, 5));

        // a closure counter: each counter keeps its own count
        var alarms = Counter();
        var other = Counter();
        alarms();
        alarms();
        Console.WriteLine("alarms: " + alarms() + ", other: " + other());

        // an event with two subscribers, then one
        var thermostat = new Thermostat(30);
        var fan = new Fan();
        Action<int> warn = t => Console.WriteLine("too hot: " + t);
        thermostat.TooHot += warn;
        thermostat.TooHot += fan.Faster;
        thermostat.Read(25);
        thermostat.Read(35);
        thermostat.Read(42);
        thermostat.TooHot -= warn;
        thermostat.Read(50);
        Console.WriteLine("fan speed: " + fan.Speed);

        // lambdas made in a loop: each keeps the variable of its own time round
        var steps = new Func<int, int>[4];
        for (int i = 0; i < steps.Length; i++)
        {
            int step = i + 1;
            steps[i] = x => x + step;
        }

        int total = 0;
        foreach (var step in steps)
        {
            total = step(total);
        }

        Console.WriteLine("steps: " + total);

        // readings sorted with a comparison (backwards), and those above 20 found
        int[] readings = { 18, 25, 21, 30, 19, 27, 22 };
        Array.Sort(readings, (a, b) => b - a);
        Console.WriteLine(string.Join(" ", Array.ConvertAll(readings, r => r.ToString())));
        var warm = Array.FindAll(readings, r => r > 20);
        Console.WriteLine("warm: " + warm.Length + ", first below 20 at " + Array.FindIndex(readings, r => r < 20));

        // many short-lived closures: on a chip the collector frees them as they go
        long sum = 0;
        for (int i = 0; i < 20000; i++)
        {
            int captured = i;
            Func<int> read = () => captured % 7;
            sum += read();
        }

        Console.WriteLine("sum: " + sum);
    }
}
