using System;
using System.Numerics;

// Generics over values, which need no heap: ChipSharp makes a copy of a generic method or struct for each type it is
// used with, like C++ templates, so Max<int> and Max<float> are two methods, and Pair<int> a struct of two ints. A
// struct that implements an interface is used through a constraint (where T : struct, IDevice), which calls the
// struct's own method; generic math (INumber<T>) is the plain operation on each number type.
class Program
{
    static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;

    static void Swap<T>(ref T a, ref T b)
    {
        T old = a;
        a = b;
        b = old;
    }

    static T Sum<T>(T[] values) where T : INumber<T>
    {
        T sum = T.Zero;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum;
    }

    static T Clamp<T>(T value, T low, T high) where T : INumber<T> => value < low ? low : value > high ? high : value;

    // runs any device the same way, without boxing it: each T gets its own Run
    static void Run<T>(ref T device, int ticks) where T : struct, IDevice
    {
        for (int i = 0; i < ticks; i++)
        {
            device.Tick();
        }

        Console.WriteLine($"{device.Name}: {device.Count}");
    }

    static void Show<T>(Pair<T> pair) => Console.WriteLine($"({pair.First}, {pair.Second})");

    static void Main()
    {
        Console.WriteLine(Max(3, 7));
        Console.WriteLine(Max(2.5f, -1.5f));
        Console.WriteLine(Max(new Reading(12), new Reading(9)).Value);

        int a = 1, b = 2;
        Swap(ref a, ref b);
        Console.WriteLine($"{a} {b}");
        var p = new Point(1, 2);
        var q = new Point(3, 4);
        Swap(ref p, ref q);
        Console.WriteLine($"{p.X} {q.X}");

        var pair = new Pair<int>(5, 8);
        pair.Swap();
        Show(pair);
        var points = new Pair<Point>(p, q);
        Console.WriteLine(points.First.Y + points.Second.Y);
        Show(new Pair<double>(0.5, 1.25));

        Console.WriteLine(Sum(new[] { 1, 2, 3, 4 }));
        Console.WriteLine(Sum(new[] { 0.5, 0.25, 0.125 }));
        Console.WriteLine(Sum(new byte[] { 200, 100 }));
        Console.WriteLine(Clamp(140, 0, 100));
        Console.WriteLine(Clamp(-0.5f, 0f, 1f));

        var ring = new Ring<long>();
        for (long tick = 1; tick <= 6; tick++)
        {
            ring.Add(tick * tick);
        }

        Console.WriteLine($"{ring.Count} {ring.Oldest} {ring.Newest}");

        var counter = new Counter();
        var blinker = new Blinker();
        Run(ref counter, 3);
        Run(ref blinker, 3);
        counter.Tick();
        Console.WriteLine(counter.Count);
    }
}

interface IDevice
{
    string Name { get; }

    int Count { get; }

    void Tick();
}

struct Counter : IDevice
{
    public int Ticks;

    public string Name => "counter";

    public int Count => Ticks;

    public void Tick() => Ticks++;
}

struct Blinker : IDevice
{
    private int _changes;
    private bool _on;

    string IDevice.Name => "blinker";

    int IDevice.Count => _changes;

    void IDevice.Tick()
    {
        _on = !_on;
        _changes++;
    }
}

struct Reading : IComparable<Reading>
{
    public int Value;

    public Reading(int value) => Value = value;

    public int CompareTo(Reading other) => Value.CompareTo(other.Value);
}

struct Point
{
    public int X, Y;

    public Point(int x, int y)
    {
        X = x;
        Y = y;
    }
}

struct Pair<T>
{
    public T First, Second;

    public Pair(T first, T second)
    {
        First = first;
        Second = second;
    }

    public void Swap() => (First, Second) = (Second, First);
}

// the last four values added, in fields: a ring buffer with no array, so it can live anywhere a struct can
struct Ring<T>
{
    private T _a, _b, _c, _d;
    private int _next;

    public int Count { get; private set; }

    public T Newest => Get((_next + 3) % 4);

    public T Oldest => Get(Count < 4 ? 0 : _next);

    public void Add(T value)
    {
        switch (_next)
        {
            case 0: _a = value; break;
            case 1: _b = value; break;
            case 2: _c = value; break;
            default: _d = value; break;
        }

        _next = (_next + 1) % 4;
        if (Count < 4)
        {
            Count++;
        }
    }

    private T Get(int index) => index switch
    {
        0 => _a,
        1 => _b,
        2 => _c,
        _ => _d,
    };
}
