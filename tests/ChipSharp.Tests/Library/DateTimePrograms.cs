using System.Globalization;
using System.Text;

namespace ChipSharp.Tests.Library;

/// <summary>
/// C# programs that use DateTime, DateOnly and TimeOnly, for ChipSharp to compile and run next to .NET (with the
/// invariant culture): one that prints what each member gives for many dates, one that prints the text of every format
/// every way a program writes it, and one per way they stop the program where .NET throws.
/// </summary>
internal static class DateTimePrograms
{
    /// <summary>
    /// A program that prints the parts of the edge dates and <paramref name="random"/> random ones, and the results of
    /// arithmetic and comparisons on them, as numbers, bools and enum names: no text of a date, no heap.
    /// </summary>
    public static string Members(int random)
    {
        var ticks = DateTimeValues.EdgeTicks.Concat(DateTimeValues.RandomTicks(random, seed: 21)).ToList();
        var rng = new Random(22);
        double[] doubleEdges = [0, 1, -1, 0.5, -0.5, 1.5, 1e-7, 2.0 / 3, 365.2425, 1e6, -1e6, 123.456_789];
        var arithmetic = ticks.Select(t => (t, value: doubleEdges[rng.Next(doubleEdges.Length)] * (rng.Next(2) == 0 ? 1 : rng.NextDouble() * 100),
                count: rng.Next(-2_000, 2_000), span: rng.NextInt64(-TimeSpan.TicksPerDay * 3_000, TimeSpan.TicksPerDay * 3_000),
                other: ticks[rng.Next(ticks.Count)]))
            .Where(a => Accepts(() => new DateTime(a.t).AddDays(a.value)) && Accepts(() => new DateTime(a.t).AddHours(a.value))
                && Accepts(() => new DateTime(a.t).AddMinutes(a.value)) && Accepts(() => new DateTime(a.t).AddSeconds(a.value))
                && Accepts(() => new DateTime(a.t).AddMilliseconds(a.value)) && Accepts(() => new DateTime(a.t).AddMonths(a.count))
                && Accepts(() => new DateTime(a.t).AddYears(a.count / 10)) && Accepts(() => new DateTime(a.t).AddTicks(a.span))
                && Accepts(() => new DateTime(a.t) - new TimeSpan(a.span)))
            .ToList();
        var components = Enumerable.Range(0, Math.Max(12, random)).Select(_ => (year: rng.Next(1, 10000), month: rng.Next(1, 13),
                day: rng.Next(1, 32), hour: rng.Next(0, 24), minute: rng.Next(0, 60), second: rng.Next(0, 60), ms: rng.Next(0, 1000)))
            .Where(c => Accepts(() => new DateTime(c.year, c.month, c.day)))
            .ToList();

        var program = new StringBuilder();
        program.AppendLine("""
            using System;

            class Program
            {
                static void Main()
                {
                    Parts();
                    Arithmetic();
                    Components();
                    Console.WriteLine(DateTime.MinValue.Ticks);
                    Console.WriteLine(DateTime.MaxValue.Ticks);
                    Console.WriteLine(DateTime.UnixEpoch.Ticks);
                    Console.WriteLine(DateTime.UnixEpoch.Kind);
                    Console.WriteLine(DateOnly.MaxValue.DayNumber);
                    Console.WriteLine(TimeOnly.MaxValue.Ticks);
                    for (int year = 1896; year <= 1905; year++)
                    {
                        Console.WriteLine(DateTime.IsLeapYear(year));
                        Console.WriteLine(DateTime.DaysInMonth(year, 2));
                    }

                    var (y, m, d) = new DateTime(2024, 2, 29);
                    Console.WriteLine(y * 10000 + m * 100 + d);
                    var (date, time) = new DateTime(2026, 11, 27, 18, 5, 9);
                    Console.WriteLine(date.DayNumber);
                    Console.WriteLine(time.Ticks);
                }
            """);
        Method(program, "Parts", [Longs("t", ticks)], """
            var d = new DateTime(t[i], (DateTimeKind)(i % 3));
            Console.WriteLine(d.Year);
            Console.WriteLine(d.Month);
            Console.WriteLine(d.Day);
            Console.WriteLine(d.Hour);
            Console.WriteLine(d.Minute);
            Console.WriteLine(d.Second);
            Console.WriteLine(d.Millisecond);
            Console.WriteLine(d.Microsecond);
            Console.WriteLine(d.Nanosecond);
            Console.WriteLine(d.DayOfWeek);
            Console.WriteLine(d.DayOfYear);
            Console.WriteLine(d.Kind);
            Console.WriteLine(d.Date.Ticks);
            Console.WriteLine(d.TimeOfDay.Ticks);
            Console.WriteLine(d.GetHashCode());
            Console.WriteLine(DateTime.DaysInMonth(d.Year, d.Month));
            Console.WriteLine(DateTime.IsLeapYear(d.Year));
            Console.WriteLine(DateTime.SpecifyKind(d, DateTimeKind.Utc).Kind);
            var date = DateOnly.FromDateTime(d);
            Console.WriteLine(date.DayNumber);
            Console.WriteLine(date.DayOfWeek);
            Console.WriteLine(date.DayOfYear);
            Console.WriteLine(date.Year * 10000 + date.Month * 100 + date.Day);
            var time = TimeOnly.FromDateTime(d);
            Console.WriteLine(time.Hour * 10000 + time.Minute * 100 + time.Second);
            Console.WriteLine(time.Millisecond * 1000 + time.Microsecond);
            Console.WriteLine(date.ToDateTime(time) == d);
            """);
        Method(program, "Arithmetic",
            [Longs("t", arithmetic.Select(a => a.t)), Doubles("v", arithmetic.Select(a => a.value)),
                Ints("n", arithmetic.Select(a => a.count)), Longs("s", arithmetic.Select(a => a.span)),
                Longs("o", arithmetic.Select(a => a.other))],
            """
            var d = new DateTime(t[i]);
            var other = new DateTime(o[i]);
            var span = new TimeSpan(s[i]);
            Console.WriteLine(d.AddDays(v[i]).Ticks);
            Console.WriteLine(d.AddHours(v[i]).Ticks);
            Console.WriteLine(d.AddMinutes(v[i]).Ticks);
            Console.WriteLine(d.AddSeconds(v[i]).Ticks);
            Console.WriteLine(d.AddMilliseconds(v[i]).Ticks);
            Console.WriteLine(d.AddMonths(n[i]).Ticks);
            Console.WriteLine(d.AddYears(n[i] / 10).Ticks);
            Console.WriteLine(d.AddTicks(s[i]).Ticks);
            Console.WriteLine(d.Add(span).Ticks);
            Console.WriteLine((d + span).Ticks);
            Console.WriteLine((d - span).Ticks);
            Console.WriteLine(d.Subtract(span).Ticks);
            Console.WriteLine((d - other).Ticks);
            Console.WriteLine(d.Subtract(other).Ticks);
            Console.WriteLine(d == other);
            Console.WriteLine(d != other);
            Console.WriteLine(d < other);
            Console.WriteLine(d <= other);
            Console.WriteLine(d > other);
            Console.WriteLine(d >= other);
            Console.WriteLine(DateTime.Compare(d, other));
            Console.WriteLine(d.CompareTo(other));
            Console.WriteLine(d.Equals(other));
            var time = TimeOnly.FromDateTime(d);
            Console.WriteLine(time.Add(span, out var days).Ticks);
            Console.WriteLine(days);
            Console.WriteLine(time.AddHours(v[i]).Ticks);
            Console.WriteLine((time - TimeOnly.FromDateTime(other)).Ticks);
            Console.WriteLine(time.IsBetween(TimeOnly.FromDateTime(other), new TimeOnly(12, 0)));
            var date = DateOnly.FromDateTime(d);
            Console.WriteLine(date.AddMonths(n[i]).DayNumber);
            Console.WriteLine(date.CompareTo(DateOnly.FromDateTime(other)));
            """);
        Method(program, "Components",
            [Ints("y", components.Select(c => c.year)), Ints("mo", components.Select(c => c.month)),
                Ints("d", components.Select(c => c.day)), Ints("h", components.Select(c => c.hour)),
                Ints("mi", components.Select(c => c.minute)), Ints("s", components.Select(c => c.second)),
                Ints("ms", components.Select(c => c.ms))],
            """
            Console.WriteLine(new DateTime(y[i], mo[i], d[i]).Ticks);
            Console.WriteLine(new DateTime(y[i], mo[i], d[i], h[i], mi[i], s[i]).Ticks);
            Console.WriteLine(new DateTime(y[i], mo[i], d[i], h[i], mi[i], s[i], ms[i]).Ticks);
            Console.WriteLine(new DateTime(y[i], mo[i], d[i], h[i], mi[i], s[i], ms[i], DateTimeKind.Utc).Kind);
            Console.WriteLine(new DateTime(y[i], mo[i], d[i], h[i], mi[i], s[i], ms[i], 999).Ticks);
            Console.WriteLine(new DateOnly(y[i], mo[i], d[i]).DayNumber);
            Console.WriteLine(new TimeOnly(h[i], mi[i], s[i], ms[i]).Ticks);
            """);
        program.AppendLine("}");
        return program.ToString();
    }

    /// <summary>
    /// The custom formats a program writes in a hole: those of <see cref="DateTimeValues.CustomFormats"/> without the
    /// quotes, backslashes and braces a hole's format cannot hold.
    /// </summary>
    public static IEnumerable<string> HoleFormats =>
        DateTimeValues.CustomFormats.Where(format => !format.Contains('"') && !format.Contains('\\') && !format.Contains('{')
            && !format.Contains('}') && format.Trim().Length == format.Length && !format.StartsWith('%'));

    /// <summary>
    /// A program that writes each of <paramref name="ticks"/> (Kind Utc for every other) in every standard and custom
    /// format, every way: printed straight away (a hole, aligned too; ToString; a composite format; Console.WriteLine of
    /// the date), with TryFormat into a span of the stack, and, when <paramref name="kept"/>, kept in a string (a heap).
    /// DateOnly and TimeOnly too.
    /// </summary>
    public static string Text(IEnumerable<long> ticks, bool kept)
    {
        var program = new StringBuilder();
        program.AppendLine("""
            using System;

            class Program
            {
                static void Main()
                {
            """);
        foreach (var (t, i) in ticks.Select((t, i) => (t, i)))
        {
            program.AppendLine($"        Write(new DateTime({Long(t)}, DateTimeKind.{(i % 2 == 0 ? "Unspecified" : "Utc")}));");
        }

        program.AppendLine("    }");
        program.AppendLine("    static void Write(DateTime d)");
        program.AppendLine("    {");
        foreach (var format in DateTimeValues.StandardFormats.Where(f => f.Length > 0).Concat(HoleFormats))
        {
            program.AppendLine($"        Console.WriteLine($\"[{{d:{format}}}]\");");
        }

        foreach (var format in DateTimeValues.CustomFormats)
        {
            program.AppendLine($"        Console.WriteLine(d.ToString({Literal(format)}));");
            program.AppendLine($"        Print(d, {Literal(format)});");
        }

        program.AppendLine("""
                    Console.WriteLine(d);
                    Console.WriteLine(d.ToString());
                    Console.WriteLine($"{d} [{d,30}] [{d,-22:t}] [{d,8:yyyy}]");
                    Console.WriteLine("{0:D} | {0:T} | {1,12:d}", d, d.AddDays(1.25));
                    Console.WriteLine("at " + d.ToString("HH:mm") + "!");
                    Console.WriteLine(d.ToShortDateString() + " " + d.ToShortTimeString());
                    Console.WriteLine(d.ToLongDateString());
                    Console.WriteLine(d.ToLongTimeString());
                    var date = DateOnly.FromDateTime(d);
                    var time = TimeOnly.FromDateTime(d);
                    Console.WriteLine($"{date} {date:D} {date:o} {date:R} {date:M} {date:y} {date:dd.MM.yy}");
                    Console.WriteLine($"{time} {time:T} {time:o} {time:R} {time:HH:mm:ss.FFF} {time:h:mm tt}");
                    Console.WriteLine(date.ToString("dddd") + " " + time.ToString("t") + " " + date.ToLongDateString());
                    Console.WriteLine(date);
                    Console.WriteLine(time);
                    Span<char> buffer = stackalloc char[40];
                    if (date.TryFormat(buffer, out int n, "yyyy/MM/dd"))
                    {
                        Console.WriteLine(n);
                    }

                    if (time.TryFormat(buffer, out n, "T"))
                    {
                        Console.WriteLine(n);
                    }
            """);
        if (kept)
        {
            program.AppendLine("""
                        string text = d.ToString("dddd, d MMMM yyyy 'at' h:mm tt");
                        Console.WriteLine(text.Length);
                        Console.WriteLine(text);
                        string hole = $"<{d:yyyyMMdd}|{d,-20:o}|{date,12}|{time:HH:mm}>";
                        Console.WriteLine(hole.Length);
                        Console.WriteLine(hole);
                        string plain = d.ToString();
                        Console.WriteLine(plain);
                        Console.WriteLine(string.Format("{0:s}", d).Length);
                """);
        }

        program.AppendLine("""
                }

                static void Print(DateTime d, ReadOnlySpan<char> format)
                {
                    Span<char> buffer = stackalloc char[60];
                    for (int size = 0; size <= 60; size += 30)
                    {
                        bool done = d.TryFormat(buffer.Slice(0, size), out int written, format);
                        Console.Write(done);
                        Console.Write(' ');
                        Console.Write(written);
                        Console.Write(' ');
                        for (int i = 0; i < written; i++)
                        {
                            Console.Write(buffer[i]);
                        }

                        Console.WriteLine();
                    }
                }
            }
            """);
        return program.ToString();
    }

    /// <summary>
    /// For each way DateTime stops a program where .NET throws: a statement that does, and what .NET throws for it, which
    /// it prints first when nothing catches it ("Unhandled exception. System.ArgumentOutOfRangeException: ...").
    /// </summary>
    public static readonly (string Statement, Func<object> Dotnet)[] Failures =
    [
        ("Console.WriteLine(new DateTime(2023, 2, 29).Ticks);", () => new DateTime(2023, 2, 29)),
        ("Console.WriteLine(new DateTime(-1).Ticks);", () => new DateTime(-1)),
        ("Console.WriteLine(new DateTime(2026, 1, 1, 24, 0, 0).Ticks);", () => new DateTime(2026, 1, 1, 24, 0, 0)),
        ("Console.WriteLine(new DateTime(2026, 1, 1, 0, 0, 0, 1000).Ticks);", () => new DateTime(2026, 1, 1, 0, 0, 0, 1000)),
        ("Console.WriteLine(new DateTime(0, (DateTimeKind)5).Ticks);", () => new DateTime(0, (DateTimeKind)5)),
        ("Console.WriteLine(DateTime.MaxValue.AddDays(1).Ticks);", () => DateTime.MaxValue.AddDays(1)),
        ("Console.WriteLine(DateTime.MinValue.AddMonths(-1).Ticks);", () => DateTime.MinValue.AddMonths(-1)),
        ("Console.WriteLine(DateTime.MinValue.AddYears(10001).Ticks);", () => DateTime.MinValue.AddYears(10001)),
        ("Console.WriteLine((DateTime.MaxValue + TimeSpan.FromDays(1)).Ticks);", () => DateTime.MaxValue + TimeSpan.FromDays(1)),
        ("Console.WriteLine(DateTime.DaysInMonth(2026, 13));", () => DateTime.DaysInMonth(2026, 13)),
        ("Console.WriteLine(DateTime.IsLeapYear(0));", () => DateTime.IsLeapYear(0)),
        ("Console.WriteLine(DateTime.MinValue.AddDays(1e300).Ticks);", () => DateTime.MinValue.AddDays(1e300)),
        ("Console.WriteLine(DateOnly.FromDayNumber(-5).DayNumber);", () => DateOnly.FromDayNumber(-5)),
        ("Console.WriteLine(new TimeOnly(25, 0).Ticks);", () => new TimeOnly(25, 0)),
        ("Console.WriteLine($\"{DateTime.MinValue:ffffffff}\");", () => DateTime.MinValue.ToString("ffffffff", CultureInfo.InvariantCulture)),
        ("Console.WriteLine($\"{DateTime.MinValue:yyyy'}\");", () => DateTime.MinValue.ToString("yyyy'", CultureInfo.InvariantCulture)),
        ("Console.WriteLine(DateTime.MinValue.ToString(\"Q\"));", () => DateTime.MinValue.ToString("Q", CultureInfo.InvariantCulture)),
        ("Console.WriteLine(DateOnly.MinValue.ToString(\"HH\"));", () => DateOnly.MinValue.ToString("HH", CultureInfo.InvariantCulture)),
        ("Console.WriteLine($\"{DateOnly.MinValue:HH}\");", () => DateOnly.MinValue.TryFormat(new char[10], out _, "HH", CultureInfo.InvariantCulture)),
    ];

    /// <summary>A program that prints 1, then does <paramref name="statement"/>, then would print 2.</summary>
    public static string Failing(string statement) => TimeSpanPrograms.Failing(statement);

    /// <summary>A string literal of C# that is <paramref name="text"/>.</summary>
    public static string Literal(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool Accepts(Func<object> run)
    {
        try
        {
            run();
            return true;
        }
        catch (Exception e) when (e is ArithmeticException or ArgumentException)
        {
            return false;
        }
    }

    private static void Method(StringBuilder program, string name, string[] arrays, string body)
    {
        program.AppendLine($"    static void {name}()");
        program.AppendLine("    {");
        foreach (var array in arrays)
        {
            program.AppendLine($"        {array}");
        }

        var first = arrays[0].Split(' ')[1];
        program.AppendLine($"        for (int i = 0; i < {first}.Length; i++)");
        program.AppendLine("        {");
        foreach (var line in body.ReplaceLineEndings("\n").Split('\n'))
        {
            program.AppendLine("            " + line);
        }

        program.AppendLine("        }");
        program.AppendLine("    }");
    }

    private static string Longs(string name, IEnumerable<long> values) =>
        $"long[] {name} = {{ {string.Join(", ", values.Select(Long))} }};";

    private static string Ints(string name, IEnumerable<int> values) =>
        $"int[] {name} = {{ {string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)))} }};";

    private static string Doubles(string name, IEnumerable<double> values) =>
        $"double[] {name} = {{ {string.Join(", ", values.Select(Double))} }};";

    private static string Long(long value) => value.ToString(CultureInfo.InvariantCulture) + "L";

    private static string Double(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }
}
