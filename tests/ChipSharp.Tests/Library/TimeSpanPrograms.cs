using System.Globalization;
using System.Text;

namespace ChipSharp.Tests.Library;

/// <summary>
/// C# programs that use TimeSpan, for ChipSharp to compile and run next to .NET: one that prints what every member
/// gives for many values, and one per way a TimeSpan stops the program where .NET throws.
/// </summary>
internal static class TimeSpanPrograms
{
    /// <summary>
    /// A program that prints, as numbers and bools, what each member of TimeSpan gives for the edge values and
    /// <paramref name="random"/> random ones of each kind: every value .NET accepts, so that it runs to its end. Each
    /// kind of value is a method with an array of them, so that the arrays need not all be on the stack at once.
    /// </summary>
    public static string Members(int random)
    {
        var ticks = TimeSpanValues.EdgeTicks.Concat(TimeSpanValues.RandomLongs(random, seed: 11)).ToList();
        var units = TimeSpanValues.EdgeUnits.Concat(TimeSpanValues.RandomLongs(random, seed: 12)).ToList();
        var doubles = TimeSpanValues.EdgeDoubles.Concat(TimeSpanValues.RandomDoubles(random, seed: 13)).ToList();
        var ints = TimeSpanValues.EdgeInts.Concat(TimeSpanValues.RandomInts(random, seed: 14)).ToList();
        var pairs = ticks.SelectMany((first, i) => ticks.Where((_, j) => (i * 7 + j) % 13 == 0).Select(second => (first, second)))
            .Where(pair => Accepts(() => new TimeSpan(pair.first) + new TimeSpan(pair.second))
                && Accepts(() => new TimeSpan(pair.first) - new TimeSpan(pair.second)))
            .ToList();
        var scaled = ticks.SelectMany((t, i) => doubles.Where((_, j) => (i + j) % 9 == 0).Select(factor => (t, factor))).ToList();
        var multiplied = scaled.Where(pair => Accepts(() => new TimeSpan(pair.t) * pair.factor)).ToList();
        var divided = scaled.Where(pair => Accepts(() => new TimeSpan(pair.t) / pair.factor)).ToList();
        var triples = ints.SelectMany((h, i) => ints.Select((m, j) => (h, m, s: ints[(i + j) % ints.Count]))
                .Where((_, j) => (i + 3 * j) % 5 == 0))
            .Where(triple => Accepts(() => new TimeSpan(triple.h, triple.m, triple.s)))
            .ToList();
        var sixes = Enumerable.Range(0, ints.Count * 4)
            .Select(i => (d: ints[i % ints.Count], h: ints[i * 3 % ints.Count], m: ints[i * 5 % ints.Count],
                s: ints[i * 7 % ints.Count], ms: ints[i * 11 % ints.Count], us: ints[i * 13 % ints.Count]))
            .Where(six => Accepts(() => new TimeSpan(six.d, six.h, six.m, six.s, six.ms, six.us)))
            .ToList();

        var program = new StringBuilder();
        program.AppendLine("""
            using System;

            class Program
            {
                static void Main()
                {
                    Parts();
                    Negation();
                    Pairs();
                    Units();
                    Doubles();
                    Scaled();
                    Constructors();
                    Console.WriteLine(TimeSpan.Zero.Ticks);
                    Console.WriteLine(TimeSpan.MaxValue.Ticks);
                    Console.WriteLine(TimeSpan.MinValue.Ticks);
                    Console.WriteLine((TimeSpan.MaxValue * 1.0).Ticks);
                }
            """);

        Method(program, "Parts", [Longs("t", ticks)], """
            var span = new TimeSpan(t[i]);
            Console.WriteLine(span.Ticks);
            Console.WriteLine(span.Days);
            Console.WriteLine(span.Hours);
            Console.WriteLine(span.Minutes);
            Console.WriteLine(span.Seconds);
            Console.WriteLine(span.Milliseconds);
            Console.WriteLine(span.Microseconds);
            Console.WriteLine(span.Nanoseconds);
            Console.WriteLine(span.TotalDays);
            Console.WriteLine(span.TotalHours);
            Console.WriteLine(span.TotalMinutes);
            Console.WriteLine(span.TotalSeconds);
            Console.WriteLine(span.TotalMilliseconds);
            Console.WriteLine(span.TotalMicroseconds);
            Console.WriteLine(span.TotalNanoseconds);
            Console.WriteLine(TimeSpan.FromTicks(t[i]) == span);
            """);
        Method(program, "Negation", [Longs("t", ticks.Where(t => t != long.MinValue))], """
            var span = new TimeSpan(t[i]);
            Console.WriteLine((-span).Ticks);
            Console.WriteLine((+span).Ticks);
            Console.WriteLine(span.Negate().Ticks);
            Console.WriteLine(span.Duration().Ticks);
            """);
        Method(program, "Pairs", [Longs("a", pairs.Select(pair => pair.first)), Longs("b", pairs.Select(pair => pair.second))], """
            var x = new TimeSpan(a[i]);
            var y = new TimeSpan(b[i]);
            Console.WriteLine((x + y).Ticks);
            Console.WriteLine((x - y).Ticks);
            Console.WriteLine(x.Add(y).Ticks);
            Console.WriteLine(x.Subtract(y).Ticks);
            Console.WriteLine(x / y);
            Console.WriteLine(x.Divide(y));
            Console.WriteLine(x == y);
            Console.WriteLine(x != y);
            Console.WriteLine(x < y);
            Console.WriteLine(x <= y);
            Console.WriteLine(x > y);
            Console.WriteLine(x >= y);
            Console.WriteLine(TimeSpan.Compare(x, y));
            Console.WriteLine(x.CompareTo(y));
            Console.WriteLine(x.Equals(y));
            Console.WriteLine(TimeSpan.Equals(x, y));
            """);

        program.AppendLine("    static void Units()");
        program.AppendLine("    {");
        foreach (var (factory, accepts) in new (string, Func<long, object>)[]
        {
            ("FromMinutes", v => TimeSpan.FromMinutes(v)), ("FromSeconds", v => TimeSpan.FromSeconds(v)),
            ("FromMilliseconds", v => TimeSpan.FromMilliseconds(v)), ("FromMicroseconds", v => TimeSpan.FromMicroseconds(v)),
        })
        {
            var values = units.Where(v => Accepts(() => accepts(v)));
            program.AppendLine($"        {factory}({string.Join(", ", values.Select(Long))});");
        }

        foreach (var (factory, accepts) in new (string, Func<int, object>)[]
        {
            ("FromDays", v => TimeSpan.FromDays(v)), ("FromHours", v => TimeSpan.FromHours(v)),
        })
        {
            var values = ints.Concat(units.Select(v => unchecked((int)v))).Where(v => Accepts(() => accepts(v)));
            program.AppendLine($"        {factory}({string.Join(", ", values.Select(Int))});");
        }

        program.AppendLine("    }");
        program.AppendLine("    static void Doubles()");
        program.AppendLine("    {");
        foreach (var (factory, accepts) in new (string, Func<double, object>)[]
        {
            ("FromDays", v => TimeSpan.FromDays(v)), ("FromHours", v => TimeSpan.FromHours(v)),
            ("FromMinutes", v => TimeSpan.FromMinutes(v)), ("FromSeconds", v => TimeSpan.FromSeconds(v)),
            ("FromMilliseconds", v => TimeSpan.FromMilliseconds(v)), ("FromMicroseconds", v => TimeSpan.FromMicroseconds(v)),
        })
        {
            var values = doubles.Where(v => Accepts(() => accepts(v)));
            program.AppendLine($"        {factory}({string.Join(", ", values.Select(Double))});");
        }

        program.AppendLine("    }");
        foreach (var factory in new[] { "FromMinutes", "FromSeconds", "FromMilliseconds", "FromMicroseconds" })
        {
            Params(program, factory, "long", $"TimeSpan.{factory}(values[i]).Ticks");
        }

        Params(program, "FromDays", "int", "TimeSpan.FromDays(values[i]).Ticks");
        Params(program, "FromHours", "int", "TimeSpan.FromHours(values[i]).Ticks");
        foreach (var factory in
            new[] { "FromDays", "FromHours", "FromMinutes", "FromSeconds", "FromMilliseconds", "FromMicroseconds" })
        {
            Params(program, factory, "double", $"TimeSpan.{factory}(values[i]).Ticks");
        }

        Method(program, "Scaled",
            [Longs("t", multiplied.Select(pair => pair.t)), Doubles("f", multiplied.Select(pair => pair.factor)),
                Longs("u", divided.Select(pair => pair.t)), Doubles("g", divided.Select(pair => pair.factor))],
            """
            var span = new TimeSpan(t[i]);
            Console.WriteLine((span * f[i]).Ticks);
            Console.WriteLine((f[i] * span).Ticks);
            Console.WriteLine(span.Multiply(f[i]).Ticks);
            """,
            """
            for (int i = 0; i < u.Length; i++)
            {
                var span = new TimeSpan(u[i]);
                Console.WriteLine((span / g[i]).Ticks);
                Console.WriteLine(span.Divide(g[i]).Ticks);
            }
            """);
        Method(program, "Constructors",
            [Ints("h", triples.Select(t => t.h)), Ints("m", triples.Select(t => t.m)), Ints("s", triples.Select(t => t.s)),
                Ints("d6", sixes.Select(x => x.d)), Ints("h6", sixes.Select(x => x.h)), Ints("m6", sixes.Select(x => x.m)),
                Ints("s6", sixes.Select(x => x.s)), Ints("ms6", sixes.Select(x => x.ms)), Ints("us6", sixes.Select(x => x.us))],
            """
            Console.WriteLine(new TimeSpan(h[i], m[i], s[i]).Ticks);
            """,
            """
            for (int i = 0; i < d6.Length; i++)
            {
                Console.WriteLine(new TimeSpan(d6[i], h6[i], m6[i], s6[i], ms6[i], us6[i]).Ticks);
            }
            """);

        program.AppendLine("}");
        return program.ToString();
    }

    /// <summary>
    /// For each way TimeSpan stops a program where .NET throws: a statement that does, and what .NET throws for it, which
    /// it prints first when nothing catches it ("Unhandled exception. System.OverflowException: ...").
    /// </summary>
    public static readonly (string Statement, Func<object> Dotnet)[] Failures =
    [
        ("Console.WriteLine((TimeSpan.MaxValue + TimeSpan.FromTicks(1)).Ticks);", () => TimeSpan.MaxValue + TimeSpan.FromTicks(1)),
        ("Console.WriteLine(TimeSpan.FromDays(10675200).Ticks);", () => TimeSpan.FromDays(10675200)),
        ("Console.WriteLine(TimeSpan.FromSeconds(double.NaN).Ticks);", () => TimeSpan.FromSeconds(double.NaN)),
        ("Console.WriteLine((TimeSpan.FromTicks(5) * double.NaN).Ticks);", () => TimeSpan.FromTicks(5) * double.NaN),
        ("Console.WriteLine((TimeSpan.FromTicks(5) / double.NaN).Ticks);", () => TimeSpan.FromTicks(5) / double.NaN),
        ("Console.WriteLine(TimeSpan.MinValue.Duration().Ticks);", () => TimeSpan.MinValue.Duration()),
        ("Console.WriteLine((-TimeSpan.MinValue).Ticks);", () => -TimeSpan.MinValue),
    ];

    /// <summary>A program that prints 1, then does <paramref name="statement"/>, then would print 2.</summary>
    public static string Failing(string statement) => $$"""
        using System;

        class Program
        {
            static void Main()
            {
                Console.WriteLine(1);
                {{statement}}
                Console.WriteLine(2);
            }
        }
        """;

    /// <summary>The first line .NET prints for the exception <paramref name="dotnet"/> throws, nothing catching it.</summary>
    public static string Unhandled(Func<object> dotnet)
    {
        var exception = Record.Exception(dotnet) ?? throw new InvalidOperationException(".NET did not throw.");
        return $"Unhandled exception. {exception.GetType().FullName}: {exception.Message}\r\n";
    }

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

    /// <summary>
    /// A static method <paramref name="name"/> with the <paramref name="arrays"/>, which runs <paramref name="body"/> for
    /// each element of the first, then <paramref name="more"/>.
    /// </summary>
    private static void Method(StringBuilder program, string name, string[] arrays, string body, string? more = null)
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
        program.AppendLine(Indent(body, 12));
        program.AppendLine("        }");
        if (more is not null)
        {
            program.AppendLine(Indent(more, 8));
        }

        program.AppendLine("    }");
    }

    /// <summary>A method that prints <paramref name="print"/> for each of its params values of <paramref name="type"/>.</summary>
    private static void Params(StringBuilder program, string name, string type, string print)
    {
        program.AppendLine($"    static void {name}(params {type}[] values)");
        program.AppendLine("    {");
        program.AppendLine("        for (int i = 0; i < values.Length; i++)");
        program.AppendLine("        {");
        program.AppendLine($"            Console.WriteLine({print});");
        program.AppendLine("        }");
        program.AppendLine("    }");
    }

    private static string Indent(string text, int spaces) =>
        string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Select(line => new string(' ', spaces) + line));

    private static string Longs(string name, IEnumerable<long> values) =>
        $"long[] {name} = {{ {string.Join(", ", values.Select(Long))} }};";

    private static string Doubles(string name, IEnumerable<double> values) =>
        $"double[] {name} = {{ {string.Join(", ", values.Select(Double))} }};";

    private static string Ints(string name, IEnumerable<int> values) =>
        $"int[] {name} = {{ {string.Join(", ", values.Select(Int))} }};";

    private static string Long(long value) =>
        value == long.MinValue ? "long.MinValue" : value.ToString(CultureInfo.InvariantCulture) + "L";

    private static string Int(int value) =>
        value == int.MinValue ? "int.MinValue" : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A double as C# writes exactly that double: -0.0 and the infinities too (NaN always fails).</summary>
    private static string Double(double value)
    {
        if (double.IsInfinity(value))
        {
            return value > 0 ? "double.PositiveInfinity" : "double.NegativeInfinity";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }
}
