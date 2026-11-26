using System.Globalization;
using Substitute = global::ChipSharp.Library.TimeSpan;

namespace ChipSharp.Tests.Library;

/// <summary>
/// ChipSharp.Library's TimeSpan, run on .NET next to .NET's own: every member gives the same result for the edge values
/// and thousands of random ones, and where .NET throws, the substitute throws the same exception with the same message
/// (on a chip its <c>Runtime</c> function stops the program with that message instead). This checks the substitute's
/// C#; the program tests check that ChipSharp compiles it to the same results.
/// </summary>
public sealed class TimeSpanSubstituteTests
{
    private static readonly long[] Ticks = [.. TimeSpanValues.EdgeTicks, .. TimeSpanValues.RandomLongs(3_000, seed: 1)];
    private static readonly long[] Units = [.. TimeSpanValues.EdgeUnits, .. TimeSpanValues.RandomLongs(3_000, seed: 2)];
    private static readonly double[] Doubles = [.. TimeSpanValues.EdgeDoubles, .. TimeSpanValues.RandomDoubles(3_000, seed: 3)];
    private static readonly int[] Ints = [.. TimeSpanValues.EdgeInts, .. TimeSpanValues.RandomInts(200, seed: 4)];

    [Fact]
    public void A_TimeSpan_of_any_ticks_has_the_parts_and_totals_of_dotnets()
    {
        var wrong = new List<string>();
        foreach (var ticks in Ticks)
        {
            var (dotnet, substitute) = (new TimeSpan(ticks), new Substitute(ticks));
            Compare(wrong, $"FromTicks({ticks})", () => TimeSpan.FromTicks(ticks), () => Substitute.FromTicks(ticks));
            Compare(wrong, $"{ticks}.Ticks", () => dotnet.Ticks, () => substitute.Ticks);
            Compare(wrong, $"{ticks}.Days", () => dotnet.Days, () => substitute.Days);
            Compare(wrong, $"{ticks}.Hours", () => dotnet.Hours, () => substitute.Hours);
            Compare(wrong, $"{ticks}.Minutes", () => dotnet.Minutes, () => substitute.Minutes);
            Compare(wrong, $"{ticks}.Seconds", () => dotnet.Seconds, () => substitute.Seconds);
            Compare(wrong, $"{ticks}.Milliseconds", () => dotnet.Milliseconds, () => substitute.Milliseconds);
            Compare(wrong, $"{ticks}.Microseconds", () => dotnet.Microseconds, () => substitute.Microseconds);
            Compare(wrong, $"{ticks}.Nanoseconds", () => dotnet.Nanoseconds, () => substitute.Nanoseconds);
            Compare(wrong, $"{ticks}.TotalDays", () => dotnet.TotalDays, () => substitute.TotalDays);
            Compare(wrong, $"{ticks}.TotalHours", () => dotnet.TotalHours, () => substitute.TotalHours);
            Compare(wrong, $"{ticks}.TotalMinutes", () => dotnet.TotalMinutes, () => substitute.TotalMinutes);
            Compare(wrong, $"{ticks}.TotalSeconds", () => dotnet.TotalSeconds, () => substitute.TotalSeconds);
            Compare(wrong, $"{ticks}.TotalMilliseconds", () => dotnet.TotalMilliseconds, () => substitute.TotalMilliseconds);
            Compare(wrong, $"{ticks}.TotalMicroseconds", () => dotnet.TotalMicroseconds, () => substitute.TotalMicroseconds);
            Compare(wrong, $"{ticks}.TotalNanoseconds", () => dotnet.TotalNanoseconds, () => substitute.TotalNanoseconds);
            Compare(wrong, $"{ticks}.Duration()", () => dotnet.Duration(), () => substitute.Duration());
            Compare(wrong, $"{ticks}.Negate()", () => dotnet.Negate(), () => substitute.Negate());
            Compare(wrong, $"-{ticks}", () => -dotnet, () => -substitute);
            Compare(wrong, $"+{ticks}", () => +dotnet, () => +substitute);
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void The_whole_number_factories_give_dotnets_ticks_and_refuse_what_dotnet_refuses()
    {
        var wrong = new List<string>();
        foreach (var units in Units)
        {
            Compare(wrong, $"FromMinutes({units})", () => TimeSpan.FromMinutes(units), () => Substitute.FromMinutes(units));
            Compare(wrong, $"FromSeconds({units})", () => TimeSpan.FromSeconds(units), () => Substitute.FromSeconds(units));
            Compare(wrong, $"FromMilliseconds({units})", () => TimeSpan.FromMilliseconds(units),
                () => Substitute.FromMilliseconds(units));
            Compare(wrong, $"FromMicroseconds({units})", () => TimeSpan.FromMicroseconds(units),
                () => Substitute.FromMicroseconds(units));
        }

        foreach (var units in Ints.Concat(Units.Select(units => unchecked((int)units))))
        {
            Compare(wrong, $"FromDays({units})", () => TimeSpan.FromDays(units), () => Substitute.FromDays(units));
            Compare(wrong, $"FromHours({units})", () => TimeSpan.FromHours(units), () => Substitute.FromHours(units));
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void The_double_factories_drop_the_fraction_of_a_tick_and_overflow_and_refuse_NaN_like_dotnet()
    {
        var wrong = new List<string>();
        foreach (var value in Doubles)
        {
            var text = value.ToString("R", CultureInfo.InvariantCulture);
            Compare(wrong, $"FromDays({text})", () => TimeSpan.FromDays(value), () => Substitute.FromDays(value));
            Compare(wrong, $"FromHours({text})", () => TimeSpan.FromHours(value), () => Substitute.FromHours(value));
            Compare(wrong, $"FromMinutes({text})", () => TimeSpan.FromMinutes(value), () => Substitute.FromMinutes(value));
            Compare(wrong, $"FromSeconds({text})", () => TimeSpan.FromSeconds(value), () => Substitute.FromSeconds(value));
            Compare(wrong, $"FromMilliseconds({text})", () => TimeSpan.FromMilliseconds(value),
                () => Substitute.FromMilliseconds(value));
            Compare(wrong, $"FromMicroseconds({text})", () => TimeSpan.FromMicroseconds(value),
                () => Substitute.FromMicroseconds(value));
        }

        // 1.5 ms is 15 000 ticks; 0.00015 ms is 1.5 ticks, of which the fraction is dropped, towards zero
        Assert.Equal(15_000, Substitute.FromMilliseconds(1.5).Ticks);
        Assert.Equal(1, Substitute.FromMilliseconds(0.00015).Ticks);
        Assert.Equal(-1, Substitute.FromMilliseconds(-0.00015).Ticks);
        Assert.Equal(long.MaxValue, Substitute.FromMicroseconds(922_337_203_685_477_580.8).Ticks);
        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void Arithmetic_and_comparisons_of_two_TimeSpans_match_dotnet_overflow_included()
    {
        var wrong = new List<string>();
        var pairs = TimeSpanValues.EdgeTicks.SelectMany(first => TimeSpanValues.EdgeTicks.Select(second => (first, second)))
            .Concat(Ticks.Zip(Ticks.Skip(1)));
        foreach (var (first, second) in pairs)
        {
            var (a, b) = (new TimeSpan(first), new TimeSpan(second));
            var (x, y) = (new Substitute(first), new Substitute(second));
            var name = $"({first}, {second})";
            Compare(wrong, $"+{name}", () => a + b, () => x + y);
            Compare(wrong, $"-{name}", () => a - b, () => x - y);
            Compare(wrong, $"Add{name}", () => a.Add(b), () => x.Add(y));
            Compare(wrong, $"Subtract{name}", () => a.Subtract(b), () => x.Subtract(y));
            Compare(wrong, $"/{name}", () => a / b, () => x / y);
            Compare(wrong, $"Divide{name}", () => a.Divide(b), () => x.Divide(y));
            Compare(wrong, $"=={name}", () => a == b, () => x == y);
            Compare(wrong, $"!={name}", () => a != b, () => x != y);
            Compare(wrong, $"<{name}", () => a < b, () => x < y);
            Compare(wrong, $"<={name}", () => a <= b, () => x <= y);
            Compare(wrong, $">{name}", () => a > b, () => x > y);
            Compare(wrong, $">={name}", () => a >= b, () => x >= y);
            Compare(wrong, $"Compare{name}", () => TimeSpan.Compare(a, b), () => Substitute.Compare(x, y));
            Compare(wrong, $"CompareTo{name}", () => a.CompareTo(b), () => x.CompareTo(y));
            Compare(wrong, $"Equals{name}", () => a.Equals(b), () => x.Equals(y));
            Compare(wrong, $"static Equals{name}", () => TimeSpan.Equals(a, b), () => Substitute.Equals(x, y));
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void Multiplying_and_dividing_by_a_double_round_to_the_nearest_tick_halfway_to_even_like_dotnet()
    {
        var wrong = new List<string>();
        var ticks = TimeSpanValues.EdgeTicks.Concat(Ticks.Take(300)).ToList();
        var factors = TimeSpanValues.EdgeDoubles.Concat(Doubles.Take(300)).ToList();
        foreach (var t in ticks)
        {
            foreach (var factor in factors)
            {
                var (a, x) = (new TimeSpan(t), new Substitute(t));
                var name = $"({t}, {factor.ToString("R", CultureInfo.InvariantCulture)})";
                Compare(wrong, $"*{name}", () => a * factor, () => x * factor);
                Compare(wrong, $"factor *{name}", () => factor * a, () => factor * x);
                Compare(wrong, $"/{name}", () => a / factor, () => x / factor);
                Compare(wrong, $"Multiply{name}", () => a.Multiply(factor), () => x.Multiply(factor));
                Compare(wrong, $"Divide{name}", () => a.Divide(factor), () => x.Divide(factor));
            }
        }

        Assert.Equal(2, (new Substitute(5) / 2).Ticks);
        Assert.Equal(4, (new Substitute(7) / 2).Ticks);
        Assert.Equal(-2, (new Substitute(-5) / 2).Ticks);
        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void The_constructors_of_days_hours_minutes_and_seconds_match_dotnet_the_wrapping_sum_included()
    {
        var wrong = new List<string>();
        var random = new Random(5);
        foreach (var h in TimeSpanValues.EdgeInts)
        {
            foreach (var m in TimeSpanValues.EdgeInts)
            {
                foreach (var s in TimeSpanValues.EdgeInts)
                {
                    Compare(wrong, $"new({h}, {m}, {s})", () => new TimeSpan(h, m, s), () => new Substitute(h, m, s));
                    Compare(wrong, $"new({h}, {m}, {s}, 7)", () => new TimeSpan(h, m, s, 7), () => new Substitute(h, m, s, 7));
                    Compare(wrong, $"new(1, {h}, {m}, {s}, -3)", () => new TimeSpan(1, h, m, s, -3),
                        () => new Substitute(1, h, m, s, -3));
                }
            }
        }

        for (var i = 0; i < 20_000; i++)
        {
            int[] parts = [.. Enumerable.Range(0, 6).Select(_ => Ints[random.Next(Ints.Length)])];
            Compare(wrong, $"new({string.Join(", ", parts)})",
                () => new TimeSpan(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]),
                () => new Substitute(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]));
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void Zero_MaxValue_and_MinValue_are_dotnets()
    {
        Assert.Equal(TimeSpan.Zero.Ticks, Substitute.Zero.Ticks);
        Assert.Equal(TimeSpan.MaxValue.Ticks, Substitute.MaxValue.Ticks);
        Assert.Equal(TimeSpan.MinValue.Ticks, Substitute.MinValue.Ticks);
    }

    /// <summary>
    /// Adds a line to <paramref name="wrong"/> when the two give different results: the same value (a TimeSpan by its
    /// ticks, a double by its bits), or the same exception with the same message.
    /// </summary>
    private static void Compare(List<string> wrong, string what, Func<object> dotnet, Func<object> substitute)
    {
        var (expected, actual) = (Outcome(dotnet), Outcome(substitute));
        if (expected != actual)
        {
            wrong.Add($"{what}: .NET {expected}, substitute {actual}");
        }
    }

    private static string Outcome(Func<object> run)
    {
        try
        {
            return run() switch
            {
                TimeSpan span => $"ticks {span.Ticks}",
                Substitute span => $"ticks {span.Ticks}",
                double value => $"{value.ToString("R", CultureInfo.InvariantCulture)} (0x{BitConverter.DoubleToInt64Bits(value):X16})",
                var value => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            };
        }
        catch (Exception e) when (e is ArithmeticException or ArgumentException)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    private static string Report(List<string> wrong) =>
        $"{wrong.Count} results differ:\n{string.Join("\n", wrong.Take(30))}";
}
