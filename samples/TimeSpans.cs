using System;

// TimeSpan, a duration in ticks of 100 nanoseconds, in any unit: its factories, its parts and totals, arithmetic and
// comparisons. ChipSharp compiles its own C# for TimeSpan (ChipSharp.Library), which gives what .NET gives.
class Program
{
    static readonly TimeSpan Period = TimeSpan.FromMilliseconds(250);

    static void Main()
    {
        var half = TimeSpan.FromMilliseconds(500);
        Console.WriteLine(half.Ticks);
        Console.WriteLine(TimeSpan.FromSeconds(2).Ticks);
        Console.WriteLine(TimeSpan.FromMicroseconds(250).Ticks);
        Console.WriteLine(TimeSpan.FromMinutes(1.5).TotalSeconds);
        Console.WriteLine(TimeSpan.FromMilliseconds(0.00015).Ticks);
        Console.WriteLine(TimeSpan.FromDays(-0.5).Hours);

        // 1 day, 2 hours, 3 minutes, 4 seconds and 5 milliseconds
        var span = new TimeSpan(1, 2, 3, 4, 5);
        Console.WriteLine(span.Days);
        Console.WriteLine(span.Hours);
        Console.WriteLine(span.Minutes);
        Console.WriteLine(span.Seconds);
        Console.WriteLine(span.Milliseconds);
        Console.WriteLine(span.TotalHours);
        Console.WriteLine(span.TotalMilliseconds);

        var difference = half - span;
        Console.WriteLine((half + span).Ticks);
        Console.WriteLine(difference.Ticks);
        Console.WriteLine(difference.Hours);
        Console.WriteLine(difference.Duration().Minutes);
        Console.WriteLine((-difference).TotalSeconds);

        Console.WriteLine(half > Period);
        Console.WriteLine(half == TimeSpan.FromSeconds(0.5));
        Console.WriteLine(half != Period * 2);
        Console.WriteLine(Period.CompareTo(half));
        Console.WriteLine((Period / 3).Ticks);
        Console.WriteLine(span / half);

        Console.WriteLine(TimeSpan.Zero.Ticks);
        Console.WriteLine(TimeSpan.MaxValue.Days);
        Console.WriteLine(TimeSpan.MinValue.Ticks);

        // TimeSpans in an array and in a struct
        TimeSpan[] laps = { TimeSpan.FromSeconds(61), TimeSpan.FromMinutes(1.5), new TimeSpan(0, 0, 59) };
        var total = TimeSpan.Zero;
        foreach (var lap in laps)
        {
            total += lap;
        }

        Console.WriteLine(total.Minutes);
        Console.WriteLine(total.Seconds);

        var blink = new Blink(25, TimeSpan.FromMilliseconds(500));
        blink.Slower();
        Console.WriteLine(blink.Pin);
        Console.WriteLine(blink.Period.TotalSeconds);
    }
}

struct Blink
{
    public int Pin;
    public TimeSpan Period;

    public Blink(int pin, TimeSpan period)
    {
        Pin = pin;
        Period = period;
    }

    public void Slower() => Period += Period / 2;
}
