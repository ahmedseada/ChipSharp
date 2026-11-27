using System.Globalization;
using SubstituteDateOnly = global::ChipSharp.Library.DateOnly;
using SubstituteDateTime = global::ChipSharp.Library.DateTime;
using SubstituteKind = global::ChipSharp.Library.DateTimeKind;
using SubstituteTimeOnly = global::ChipSharp.Library.TimeOnly;

namespace ChipSharp.Tests.Library;

/// <summary>
/// ChipSharp.Library's DateTime, DateOnly and TimeOnly, run on .NET next to .NET's own: every member gives the same
/// result for the edges of the calendar and thousands of random dates, the text of every standard and custom format is
/// the invariant culture's, and where .NET throws, the substitute throws the same exception with the same message (on a
/// chip its <c>Runtime</c> function stops the program with that message instead). This checks the substitutes' C#; the
/// program tests check that ChipSharp compiles it to the same results.
/// </summary>
public sealed class DateTimeSubstituteTests
{
    private static readonly long[] Ticks = [.. DateTimeValues.EdgeTicks, .. DateTimeValues.RandomTicks(3_000, seed: 1)];

    [Fact]
    public void A_DateTime_of_any_ticks_has_the_parts_of_dotnets()
    {
        var wrong = new List<string>();
        foreach (var ticks in Ticks)
        {
            foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Utc, DateTimeKind.Local })
            {
                var (dotnet, substitute) = (new DateTime(ticks, kind), new SubstituteDateTime(ticks, (SubstituteKind)kind));
                var what = $"{ticks} {kind}";
                Compare(wrong, $"{what}.Year", () => dotnet.Year, () => substitute.Year);
                Compare(wrong, $"{what}.Month", () => dotnet.Month, () => substitute.Month);
                Compare(wrong, $"{what}.Day", () => dotnet.Day, () => substitute.Day);
                Compare(wrong, $"{what}.Hour", () => dotnet.Hour, () => substitute.Hour);
                Compare(wrong, $"{what}.Minute", () => dotnet.Minute, () => substitute.Minute);
                Compare(wrong, $"{what}.Second", () => dotnet.Second, () => substitute.Second);
                Compare(wrong, $"{what}.Millisecond", () => dotnet.Millisecond, () => substitute.Millisecond);
                Compare(wrong, $"{what}.Microsecond", () => dotnet.Microsecond, () => substitute.Microsecond);
                Compare(wrong, $"{what}.Nanosecond", () => dotnet.Nanosecond, () => substitute.Nanosecond);
                Compare(wrong, $"{what}.DayOfWeek", () => (int)dotnet.DayOfWeek, () => (int)substitute.DayOfWeek);
                Compare(wrong, $"{what}.DayOfYear", () => dotnet.DayOfYear, () => substitute.DayOfYear);
                Compare(wrong, $"{what}.Ticks", () => dotnet.Ticks, () => substitute.Ticks);
                Compare(wrong, $"{what}.Kind", () => (int)dotnet.Kind, () => (int)substitute.Kind);
                Compare(wrong, $"{what}.Date", () => Of(dotnet.Date), () => Of(substitute.Date));
                Compare(wrong, $"{what}.TimeOfDay", () => dotnet.TimeOfDay, () => substitute.TimeOfDay);
                Compare(wrong, $"{what}.GetHashCode()", () => dotnet.GetHashCode(), () => substitute.GetHashCode());
                Compare(wrong, $"{what} Deconstruct", () => Parts(dotnet), () => Parts(substitute));
                Compare(wrong, $"{what} DateOnly", () => DateOnly.FromDateTime(dotnet).DayNumber,
                    () => SubstituteDateOnly.FromDateTime(substitute).DayNumber);
                Compare(wrong, $"{what} TimeOnly", () => TimeOnly.FromDateTime(dotnet).Ticks,
                    () => SubstituteTimeOnly.FromDateTime(substitute).Ticks);
            }
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void The_constructors_make_dotnets_dates_and_refuse_what_dotnet_refuses()
    {
        var wrong = new List<string>();
        var random = new Random(2);
        int[] years = [0, 1, 2, 4, 100, 400, 1900, 2000, 2023, 2024, 9999, 10000, -1];
        for (var i = 0; i < 4_000; i++)
        {
            var year = i % 3 == 0 ? years[random.Next(years.Length)] : random.Next(1, 10000);
            var (month, day) = (random.Next(-1, 14), random.Next(-1, 33));
            var (hour, minute, second) = (random.Next(-1, 25), random.Next(-1, 61), random.Next(-1, 61));
            var (ms, us) = (random.Next(-1, 1001), random.Next(-1, 1001));
            var kind = (DateTimeKind)random.Next(0, 4);
            var what = $"{year}-{month}-{day} {hour}:{minute}:{second}.{ms}.{us} {kind}";
            Compare(wrong, $"new ({what}) ymd", () => Of(new DateTime(year, month, day)),
                () => Of(new SubstituteDateTime(year, month, day)));
            Compare(wrong, $"new ({what}) ymdhms", () => Of(new DateTime(year, month, day, hour, minute, second)),
                () => Of(new SubstituteDateTime(year, month, day, hour, minute, second)));
            Compare(wrong, $"new ({what}) ymdhms kind", () => Of(new DateTime(year, month, day, hour, minute, second, kind)),
                () => Of(new SubstituteDateTime(year, month, day, hour, minute, second, (SubstituteKind)kind)));
            Compare(wrong, $"new ({what}) ms", () => Of(new DateTime(year, month, day, hour, minute, second, ms)),
                () => Of(new SubstituteDateTime(year, month, day, hour, minute, second, ms)));
            Compare(wrong, $"new ({what}) ms kind", () => Of(new DateTime(year, month, day, hour, minute, second, ms, kind)),
                () => Of(new SubstituteDateTime(year, month, day, hour, minute, second, ms, (SubstituteKind)kind)));
            Compare(wrong, $"new ({what}) us", () => Of(new DateTime(year, month, day, hour, minute, second, ms, us)),
                () => Of(new SubstituteDateTime(year, month, day, hour, minute, second, ms, us)));
            Compare(wrong, $"new ({what}) us kind",
                () => Of(new DateTime(year, month, day, hour, minute, second, ms, us, kind)),
                () => Of(new SubstituteDateTime(year, month, day, hour, minute, second, ms, us, (SubstituteKind)kind)));
            Compare(wrong, $"DaysInMonth({year}, {month})", () => DateTime.DaysInMonth(year, month),
                () => SubstituteDateTime.DaysInMonth(year, month));
            Compare(wrong, $"IsLeapYear({year})", () => DateTime.IsLeapYear(year), () => SubstituteDateTime.IsLeapYear(year));
            Compare(wrong, $"new DateOnly({what})", () => new DateOnly(year, month, day).DayNumber,
                () => new SubstituteDateOnly(year, month, day).DayNumber);
            Compare(wrong, $"new TimeOnly({what})", () => new TimeOnly(hour, minute, second, ms, us).Ticks,
                () => new SubstituteTimeOnly(hour, minute, second, ms, us).Ticks);
            Compare(wrong, $"new TimeOnly({what}) hm", () => new TimeOnly(hour, minute).Ticks,
                () => new SubstituteTimeOnly(hour, minute).Ticks);
        }

        foreach (var ticks in Ticks.Concat([-1, DateTime.MaxValue.Ticks + 1, long.MaxValue, long.MinValue]))
        {
            Compare(wrong, $"new ({ticks})", () => Of(new DateTime(ticks)), () => Of(new SubstituteDateTime(ticks)));
            Compare(wrong, $"new ({ticks}, kind 3)", () => Of(new DateTime(ticks, (DateTimeKind)3)),
                () => Of(new SubstituteDateTime(ticks, (SubstituteKind)3)));
            Compare(wrong, $"new TimeOnly({ticks})", () => new TimeOnly(ticks % (TimeSpan.TicksPerDay + 3)).Ticks,
                () => new SubstituteTimeOnly(ticks % (TimeSpan.TicksPerDay + 3)).Ticks);
            Compare(wrong, $"SpecifyKind({ticks})", () => Of(DateTime.SpecifyKind(new DateTime(ticks), DateTimeKind.Utc)),
                () => Of(SubstituteDateTime.SpecifyKind(new SubstituteDateTime(ticks), SubstituteKind.Utc)));
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void Arithmetic_and_comparisons_give_dotnets_dates_and_refuse_what_dotnet_refuses()
    {
        var wrong = new List<string>();
        var random = new Random(3);
        double[] doubles = [0, 1, -1, 0.5, -0.5, 1.5, 1e-7, 2.0 / 3, 365.2425, 1e6, -1e6, 3_652_058, 3_652_059, 1e10,
            double.NaN, double.PositiveInfinity, double.NegativeInfinity, 123.456_789];
        int[] ints = [0, 1, -1, 12, -12, 13, 11, 120_000, 120_001, -120_000, 10_000, 10_001, -10_000, 9_998, 3_652_058];
        foreach (var ticks in Ticks)
        {
            var (dotnet, substitute) = (new DateTime(ticks, DateTimeKind.Utc), new SubstituteDateTime(ticks, SubstituteKind.Utc));
            var value = doubles[random.Next(doubles.Length)] * (random.Next(2) == 0 ? 1 : random.NextDouble() * 1000);
            var count = random.Next(3) == 0 ? ints[random.Next(ints.Length)] : random.Next(-20_000, 20_000);
            var span = random.NextInt64(-DateTime.MaxValue.Ticks / 2, DateTime.MaxValue.Ticks / 2);
            var other = Ticks[random.Next(Ticks.Length)];
            var what = $"{ticks} {value.ToString("R", CultureInfo.InvariantCulture)} {count} {span} {other}";
            Compare(wrong, $"{what} AddDays", () => Of(dotnet.AddDays(value)), () => Of(substitute.AddDays(value)));
            Compare(wrong, $"{what} AddHours", () => Of(dotnet.AddHours(value)), () => Of(substitute.AddHours(value)));
            Compare(wrong, $"{what} AddMinutes", () => Of(dotnet.AddMinutes(value)), () => Of(substitute.AddMinutes(value)));
            Compare(wrong, $"{what} AddSeconds", () => Of(dotnet.AddSeconds(value)), () => Of(substitute.AddSeconds(value)));
            Compare(wrong, $"{what} AddMilliseconds", () => Of(dotnet.AddMilliseconds(value)),
                () => Of(substitute.AddMilliseconds(value)));
            Compare(wrong, $"{what} AddMicroseconds", () => Of(dotnet.AddMicroseconds(value)),
                () => Of(substitute.AddMicroseconds(value)));
            Compare(wrong, $"{what} AddMonths", () => Of(dotnet.AddMonths(count)), () => Of(substitute.AddMonths(count)));
            Compare(wrong, $"{what} AddYears", () => Of(dotnet.AddYears(count)), () => Of(substitute.AddYears(count)));
            Compare(wrong, $"{what} AddTicks", () => Of(dotnet.AddTicks(span)), () => Of(substitute.AddTicks(span)));
            Compare(wrong, $"{what} Add", () => Of(dotnet.Add(new TimeSpan(span))), () => Of(substitute.Add(new TimeSpan(span))));
            Compare(wrong, $"{what} +", () => Of(dotnet + new TimeSpan(span)), () => Of(substitute + new TimeSpan(span)));
            Compare(wrong, $"{what} -", () => Of(dotnet - new TimeSpan(span)), () => Of(substitute - new TimeSpan(span)));
            Compare(wrong, $"{what} Subtract", () => Of(dotnet.Subtract(new TimeSpan(span))),
                () => Of(substitute.Subtract(new TimeSpan(span))));

            var (b, c) = (new DateTime(other), new SubstituteDateTime(other));
            Compare(wrong, $"{what} - date", () => dotnet - b, () => substitute - c);
            Compare(wrong, $"{what} Subtract(date)", () => dotnet.Subtract(b), () => substitute.Subtract(c));
            Compare(wrong, $"{what} ==", () => dotnet == b, () => substitute == c);
            Compare(wrong, $"{what} !=", () => dotnet != b, () => substitute != c);
            Compare(wrong, $"{what} <", () => dotnet < b, () => substitute < c);
            Compare(wrong, $"{what} <=", () => dotnet <= b, () => substitute <= c);
            Compare(wrong, $"{what} >", () => dotnet > b, () => substitute > c);
            Compare(wrong, $"{what} >=", () => dotnet >= b, () => substitute >= c);
            Compare(wrong, $"{what} Compare", () => DateTime.Compare(dotnet, b), () => SubstituteDateTime.Compare(substitute, c));
            Compare(wrong, $"{what} CompareTo", () => dotnet.CompareTo(b), () => substitute.CompareTo(c));
            Compare(wrong, $"{what} Equals", () => dotnet.Equals(b), () => substitute.Equals(c));

            var (date, dateOnly) = (DateOnly.FromDateTime(dotnet), SubstituteDateOnly.FromDateTime(substitute));
            Compare(wrong, $"{what} DateOnly.AddDays", () => date.AddDays(count * 100).DayNumber,
                () => dateOnly.AddDays(count * 100).DayNumber);
            Compare(wrong, $"{what} DateOnly.AddMonths", () => date.AddMonths(count).DayNumber,
                () => dateOnly.AddMonths(count).DayNumber);
            Compare(wrong, $"{what} DateOnly.AddYears", () => date.AddYears(count).DayNumber,
                () => dateOnly.AddYears(count).DayNumber);
            Compare(wrong, $"{what} DateOnly parts", () => (date.Year, date.Month, date.Day, (int)date.DayOfWeek, date.DayOfYear),
                () => (dateOnly.Year, dateOnly.Month, dateOnly.Day, (int)dateOnly.DayOfWeek, dateOnly.DayOfYear));
            Compare(wrong, $"{what} DateOnly.FromDayNumber", () => DateOnly.FromDayNumber(count * 200).DayNumber,
                () => SubstituteDateOnly.FromDayNumber(count * 200).DayNumber);

            var (time, timeOnly) = (TimeOnly.FromDateTime(dotnet), SubstituteTimeOnly.FromDateTime(substitute));
            var (time2, timeOnly2) = (TimeOnly.FromDateTime(b), SubstituteTimeOnly.FromDateTime(c));
            Compare(wrong, $"{what} TimeOnly.Add", () => time.Add(new TimeSpan(span)).Ticks,
                () => timeOnly.Add(new TimeSpan(span)).Ticks);
            Compare(wrong, $"{what} TimeOnly.Add wrapped", () => (time.Add(new TimeSpan(span), out var d1).Ticks, d1),
                () => (timeOnly.Add(new TimeSpan(span), out var d2).Ticks, d2));
            Compare(wrong, $"{what} TimeOnly.AddHours", () => time.AddHours(value).Ticks, () => timeOnly.AddHours(value).Ticks);
            Compare(wrong, $"{what} TimeOnly.AddMinutes", () => (time.AddMinutes(value, out var d3).Ticks, d3),
                () => (timeOnly.AddMinutes(value, out var d4).Ticks, d4));
            Compare(wrong, $"{what} TimeOnly -", () => time - time2, () => timeOnly - timeOnly2);
            Compare(wrong, $"{what} TimeOnly.IsBetween", () => time.IsBetween(time2, TimeOnly.FromDateTime(dotnet.AddHours(1))),
                () => timeOnly.IsBetween(timeOnly2, SubstituteTimeOnly.FromDateTime(substitute.AddHours(1))));
            Compare(wrong, $"{what} TimeOnly parts", () => (time.Hour, time.Minute, time.Second, time.Millisecond, time.Microsecond),
                () => (timeOnly.Hour, timeOnly.Minute, timeOnly.Second, timeOnly.Millisecond, timeOnly.Microsecond));
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void Every_format_writes_the_invariant_cultures_text_and_TryFormat_writes_it_into_a_span()
    {
        var wrong = new List<string>();
        string[] formats = [.. DateTimeValues.StandardFormats, .. DateTimeValues.CustomFormats, .. DateTimeValues.BadFormats];
        foreach (var ticks in Ticks.Take(1_200))
        {
            // a local time's offset is the PC's now in ChipSharp, the date's in .NET: those the z tests leave out
            foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Utc })
            {
                var (dotnet, substitute) = (new DateTime(ticks, kind), new SubstituteDateTime(ticks, (SubstituteKind)kind));
                IEnumerable<string> all = kind == DateTimeKind.Utc ? [.. formats, "z", "zz", "zzz", "HH:mm zzz", "U"] : formats;
                foreach (var format in all)
                {
                    Compare(wrong, $"{ticks} {kind} \"{format}\"", () => dotnet.ToString(format, CultureInfo.InvariantCulture),
                        () => substitute.ToString(format));
                    Compare(wrong, $"{ticks} {kind} TryFormat \"{format}\"", () => TryFormat(dotnet, format),
                        () => TryFormat(substitute, format));
                }

                Compare(wrong, $"{ticks} {kind} ToString()", () => dotnet.ToString(CultureInfo.InvariantCulture),
                    () => substitute.ToString());
                Compare(wrong, $"{ticks} {kind} ToLongDateString()", () => dotnet.ToString("D", CultureInfo.InvariantCulture),
                    () => substitute.ToLongDateString());
                Compare(wrong, $"{ticks} {kind} ToShortTimeString()", () => dotnet.ToString("t", CultureInfo.InvariantCulture),
                    () => substitute.ToShortTimeString());
            }
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void DateOnly_and_TimeOnly_write_their_formats_and_refuse_the_others_as_dotnet_does()
    {
        var wrong = new List<string>();
        foreach (var ticks in Ticks.Take(800))
        {
            var (date, dateOnly) = (DateOnly.FromDateTime(new DateTime(ticks)), SubstituteDateOnly.FromDateTime(new SubstituteDateTime(ticks)));
            foreach (var format in DateTimeValues.DateOnlyFormats)
            {
                Compare(wrong, $"{ticks} DateOnly \"{format}\"", () => date.ToString(format, CultureInfo.InvariantCulture),
                    () => dateOnly.ToString(format));
                Compare(wrong, $"{ticks} DateOnly TryFormat \"{format}\"", () => TryFormat(date, format), () => TryFormat(dateOnly, format));
            }

            Compare(wrong, $"{ticks} DateOnly ToString()", () => date.ToString(CultureInfo.InvariantCulture), () => dateOnly.ToString());

            var (time, timeOnly) = (TimeOnly.FromDateTime(new DateTime(ticks)), SubstituteTimeOnly.FromDateTime(new SubstituteDateTime(ticks)));
            foreach (var format in DateTimeValues.TimeOnlyFormats)
            {
                Compare(wrong, $"{ticks} TimeOnly \"{format}\"", () => time.ToString(format, CultureInfo.InvariantCulture),
                    () => timeOnly.ToString(format));
                Compare(wrong, $"{ticks} TimeOnly TryFormat \"{format}\"", () => TryFormat(time, format), () => TryFormat(timeOnly, format));
            }

            Compare(wrong, $"{ticks} TimeOnly ToString()", () => time.ToString(CultureInfo.InvariantCulture), () => timeOnly.ToString());
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    /// <summary>TryFormat into spans of the text's length, one less and none: whether it fits, how much it wrote, what.</summary>
    private static string TryFormat(DateTime value, string format) =>
        TryFormat((Span<char> span, out int n) => value.TryFormat(span, out n, format, CultureInfo.InvariantCulture),
            value.ToString(format, CultureInfo.InvariantCulture).Length);

    private static string TryFormat(SubstituteDateTime value, string format) =>
        TryFormat((Span<char> span, out int n) => value.TryFormat(span, out n, format), value.ToString(format).Length);

    private static string TryFormat(DateOnly value, string format) =>
        TryFormat((Span<char> span, out int n) => value.TryFormat(span, out n, format, CultureInfo.InvariantCulture), 40);

    private static string TryFormat(SubstituteDateOnly value, string format) =>
        TryFormat((Span<char> span, out int n) => value.TryFormat(span, out n, format), 40);

    private static string TryFormat(TimeOnly value, string format) =>
        TryFormat((Span<char> span, out int n) => value.TryFormat(span, out n, format, CultureInfo.InvariantCulture), 40);

    private static string TryFormat(SubstituteTimeOnly value, string format) =>
        TryFormat((Span<char> span, out int n) => value.TryFormat(span, out n, format), 40);

    private delegate bool Formatter(Span<char> destination, out int written);

    private static string TryFormat(Formatter format, int length)
    {
        var results = new List<string>();
        foreach (var size in new[] { length + 5, length, length - 1, 0 }.Where(size => size >= 0))
        {
            var buffer = new char[size];
            var fits = format(buffer, out var written);
            results.Add(fits ? $"{written} '{new string(buffer, 0, written)}'" : $"no ({written})");
        }

        return string.Join(", ", results);
    }

    private static string Of(DateTime value) => $"{value.Ticks} {value.Kind}";

    private static string Of(SubstituteDateTime value) => $"{value.Ticks} {(DateTimeKind)value.Kind}";

    private static (int, int, int) Parts(DateTime value)
    {
        var (year, month, day) = value;
        return (year, month, day);
    }

    private static (int, int, int) Parts(SubstituteDateTime value)
    {
        value.Deconstruct(out int year, out int month, out int day);
        return (year, month, day);
    }

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
            return Convert.ToString(run(), CultureInfo.InvariantCulture)!;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or ArithmeticException)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    private static string Report(List<string> wrong) =>
        $"{wrong.Count} results differ:\n{string.Join("\n", wrong.Take(30))}";
}
