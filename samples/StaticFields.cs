using System;

// Static fields keep their values from one call to the next. Field initializers and static constructors set them up
// before they are used; a class whose initializer reads another class's fields gets them already set up.
class Program
{
    static int calls;
    static long total = 1_000_000_000_000;
    static bool ready = true;
    static byte small = 250;
    static volatile int ticks;

    static int Next()
    {
        calls++;
        return calls * 10;
    }

    static void Add(int amount) => total += amount;

    static void Main()
    {
        for (int i = 0; i < 5; i++)
        {
            Console.WriteLine(Next());
        }

        Console.WriteLine(calls);
        Add(5);
        Add(-7);
        Console.WriteLine(total);
        Console.WriteLine(ready);
        ready = !ready;
        Console.WriteLine(ready);
        small += 10;
        Console.WriteLine(small);
        for (int i = 0; i < 3; i++)
        {
            ticks++;
        }

        Console.WriteLine(ticks);

        // Table's initializer uses Settings, so Settings is set up first
        Console.WriteLine(Table.Size);
        Console.WriteLine(Settings.Limit);
        Console.WriteLine(Settings.Mask);

        Console.WriteLine(Counter.Value);
        Counter.Increment();
        Counter.Increment();
        Console.WriteLine(Counter.Value);
    }
}

static class Settings
{
    public static readonly int Limit;
    public static readonly ulong Mask;

    static Settings()
    {
        int limit = 1;
        for (int i = 0; i < 10; i++)
        {
            limit *= 2;
        }

        Limit = limit;
        Mask = (1UL << 40) - 1;
    }
}

static class Table
{
    public static readonly int Size = Settings.Limit / 4 + Square(3);

    static int Square(int x) => x * x;
}

static class Counter
{
    public static int Value { get; private set; } = 100;

    public static void Increment() => Value++;
}
