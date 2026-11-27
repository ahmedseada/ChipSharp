using System.Text;
using Substitute = global::ChipSharp.Library.StringBuilder;

namespace ChipSharp.Tests.Library;

/// <summary>
/// ChipSharp.Library's StringBuilder, run on .NET next to .NET's own: the same calls, from the edge values and
/// thousands of random sequences of them, give the same text and length, and the same exception with the same message
/// where .NET throws (on a chip its <c>Runtime</c> function stops the program with that message instead); a builder
/// that only appends, or is given a capacity, has .NET's capacity too. This checks the substitute's C#; the program
/// tests check that ChipSharp compiles it to the same results.
/// </summary>
public sealed class StringBuilderSubstituteTests
{
    private static readonly int[] Edges = [-2, -1, 0, 1, 2, 3, 5, 16, 17, 33];

    [Fact]
    public void Every_constructor_gives_dotnets_text_capacity_and_messages()
    {
        var wrong = new List<string>();
        Compare(wrong, "new()", () => Describe(new StringBuilder()), () => Describe(new Substitute()));
        foreach (var value in new[] { null, "", "ab", "hello there, a text longer than sixteen chars" })
        {
            Compare(wrong, $"new({value})", () => Describe(new StringBuilder(value)), () => Describe(new Substitute(value)));
            foreach (var capacity in Edges)
            {
                Compare(wrong, $"new({value}, {capacity})", () => Describe(new StringBuilder(value, capacity)),
                    () => Describe(new Substitute(value, capacity)));
                foreach (var start in Edges)
                {
                    foreach (var length in Edges)
                    {
                        Compare(wrong, $"new({value}, {start}, {length}, {capacity})",
                            () => Describe(new StringBuilder(value, start, length, capacity)),
                            () => Describe(new Substitute(value, start, length, capacity)));
                    }
                }
            }
        }

        foreach (var capacity in Edges)
        {
            Compare(wrong, $"new({capacity})", () => Describe(new StringBuilder(capacity)),
                () => Describe(new Substitute(capacity)));
            foreach (var max in Edges)
            {
                Compare(wrong, $"new({capacity}, {max})", () => Describe(new StringBuilder(capacity, max)),
                    () => Describe(new Substitute(capacity, max)));
            }
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void Each_member_on_each_edge_gives_dotnets_result_and_messages()
    {
        var wrong = new List<string>();
        foreach (var text in new[] { "", "abcabc", "a long text that has grown past its first sixteen chars" })
        {
            foreach (var a in Edges)
            {
                Check(wrong, text, $"[{a}]", sb => sb[a], sb => sb[a]);
                Check(wrong, text, $"[{a}] = 'x'", sb => sb[a] = 'x', sb => sb[a] = 'x');
                Check(wrong, text, $"Length = {a}", sb => sb.Length = a, sb => sb.Length = a);
                Check(wrong, text, $"Capacity = {a}", sb => sb.Capacity = a, sb => sb.Capacity = a);
                Check(wrong, text, $"Capacity = Length + {a}", sb => sb.Capacity = sb.Length + a, sb => sb.Capacity = sb.Length + a);
                Check(wrong, text, $"EnsureCapacity({a})", sb => sb.EnsureCapacity(a), sb => sb.EnsureCapacity(a));
                Check(wrong, text, $"EnsureCapacity(Length + {a} * 9)", sb => sb.EnsureCapacity(sb.Length + a * 9),
                    sb => sb.EnsureCapacity(sb.Length + a * 9));
                Check(wrong, text, $"Append('q', {a})", sb => sb.Append('q', a), sb => sb.Append('q', a));
                Check(wrong, text, $"Insert({a}, \"xy\")", sb => sb.Insert(a, "xy"), sb => sb.Insert(a, "xy"));
                Check(wrong, text, $"Insert({a}, 'z')", sb => sb.Insert(a, 'z'), sb => sb.Insert(a, 'z'));
                Check(wrong, text, $"Insert({a}, 42)", sb => sb.Insert(a, 42), sb => sb.Insert(a, 42));
                Check(wrong, text, $"Insert({a}, (object)null)", sb => sb.Insert(a, (object?)null), sb => sb.Insert(a, (object?)null));
                Check(wrong, text, $"Insert({a}, null)", sb => sb.Insert(a, (string?)null), sb => sb.Insert(a, (string?)null));
                Check(wrong, text, $"Insert({a}, ReadOnlySpan)", sb => sb.Insert(a, "rs".AsSpan()), sb => sb.Insert(a, "rs".AsSpan()));
                foreach (var b in Edges)
                {
                    Check(wrong, text, $"Append(\"hello\", {a}, {b})", sb => sb.Append("hello", a, b), sb => sb.Append("hello", a, b));
                    Check(wrong, text, $"Append(null, {a}, {b})", sb => sb.Append((string?)null, a, b),
                        sb => sb.Append((string?)null, a, b));
                    Check(wrong, text, $"Append(chars, {a}, {b})", sb => sb.Append("hello".ToCharArray(), a, b),
                        sb => sb.Append("hello".ToCharArray(), a, b));
                    Check(wrong, text, $"Append((char[])null, {a}, {b})", sb => sb.Append((char[]?)null, a, b),
                        sb => sb.Append((char[]?)null, a, b));
                    Check(wrong, text, $"Append(builder, {a}, {b})", sb => sb.Append(new StringBuilder("hello"), a, b),
                        sb => sb.Append(new Substitute("hello"), a, b));
                    Check(wrong, text, $"Append((builder)null, {a}, {b})", sb => sb.Append((StringBuilder?)null, a, b),
                        sb => sb.Append((Substitute?)null, a, b));
                    Check(wrong, text, $"Insert({a}, \"ab\", {b})", sb => sb.Insert(a, "ab", b), sb => sb.Insert(a, "ab", b));
                    Check(wrong, text, $"Insert({a}, null, {b})", sb => sb.Insert(a, null, b), sb => sb.Insert(a, null, b));
                    Check(wrong, text, $"Remove({a}, {b})", sb => sb.Remove(a, b), sb => sb.Remove(a, b));
                    Check(wrong, text, $"ToString({a}, {b})", sb => sb.ToString(a, b), sb => sb.ToString(a, b));
                    Check(wrong, text, $"Replace('a', 'X', {a}, {b})", sb => sb.Replace('a', 'X', a, b), sb => sb.Replace('a', 'X', a, b));
                    Check(wrong, text, $"Replace(\"bc\", \"BCD\", {a}, {b})", sb => sb.Replace("bc", "BCD", a, b),
                        sb => sb.Replace("bc", "BCD", a, b));
                    Check(wrong, text, $"Replace(\"\", \"x\", {a}, {b})", sb => sb.Replace("", "x", a, b), sb => sb.Replace("", "x", a, b));
                    Check(wrong, text, $"Replace(null, \"x\", {a}, {b})", sb => sb.Replace(null!, "x", a, b),
                        sb => sb.Replace(null!, "x", a, b));
                    Check(wrong, text, $"CopyTo({a}, Span, {b})", sb => CopyTo(sb, a, b), sb => CopyTo(sb, a, b));
                    foreach (var c in Edges)
                    {
                        Check(wrong, text, $"Insert({a}, chars, {b}, {c})", sb => sb.Insert(a, "hello".ToCharArray(), b, c),
                            sb => sb.Insert(a, "hello".ToCharArray(), b, c));
                        Check(wrong, text, $"Insert({a}, (char[])null, {b}, {c})", sb => sb.Insert(a, null, b, c),
                            sb => sb.Insert(a, null, b, c));
                        Check(wrong, text, $"CopyTo({a}, char[4], {b}, {c})", sb => CopyTo(sb, a, b, c), sb => CopyTo(sb, a, b, c));
                    }
                }
            }

            Check(wrong, text, "CopyTo(0, null, 0, 1)", sb => Repeat(1, () => sb.CopyTo(0, null!, 0, 1)),
                sb => Repeat(1, () => sb.CopyTo(0, null!, 0, 1)));
            Check(wrong, text, "Replace('a', 'X')", sb => sb.Replace('a', 'X'), sb => sb.Replace('a', 'X'));
            Check(wrong, text, "Replace(\"abc\", \"\")", sb => sb.Replace("abc", ""), sb => sb.Replace("abc", ""));
            Check(wrong, text, "Replace(\"abc\", null)", sb => sb.Replace("abc", null), sb => sb.Replace("abc", null));
            Check(wrong, text, "Replace(\"a\", \"aaa\")", sb => sb.Replace("a", "aaa"), sb => sb.Replace("a", "aaa"));
            Check(wrong, text, "Replace(\"ca\", \"!\")", sb => sb.Replace("ca", "!"), sb => sb.Replace("ca", "!"));
            Check(wrong, text, "Clear()", sb => sb.Clear(), sb => sb.Clear());
            Check(wrong, text, "AppendLine()", sb => sb.AppendLine(), sb => sb.AppendLine());
            Check(wrong, text, "AppendLine(\"x\")", sb => sb.AppendLine("x"), sb => sb.AppendLine("x"));
            Check(wrong, text, "AppendJoin(\", \", strings)", sb => sb.AppendJoin(", ", "a", null, "c"),
                sb => sb.AppendJoin(", ", ["a", null, "c"]));
            Check(wrong, text, "AppendJoin('-', strings)", sb => sb.AppendJoin('-', "a", null, "c"),
                sb => sb.AppendJoin('-', ["a", null, "c"]));
            Check(wrong, text, "AppendJoin(null, objects)", sb => sb.AppendJoin(null, 1, null, 'c'),
                sb => sb.AppendJoin(null, [1, null, 'c']));
            Check(wrong, text, "AppendJoin(\",\", (string[])null)", sb => sb.AppendJoin(",", (string[])null!),
                sb => sb.AppendJoin(",", (string[])null!));
            Check(wrong, text, "AppendJoin(',', (object[])null)", sb => sb.AppendJoin(',', (object[])null!),
                sb => sb.AppendJoin(',', (object[])null!));
            Check(wrong, text, "Append(itself)", sb => sb.Append(sb), sb => sb.Append(sb));
            Check(wrong, text, "Equals(same)", sb => sb.Equals(new StringBuilder(text, 99)), sb => sb.Equals(new Substitute(text, 99)));
            Check(wrong, text, "Equals(other)", sb => sb.Equals(new StringBuilder("abcab")), sb => sb.Equals(new Substitute("abcab")));
            Check(wrong, text, "Equals(null)", sb => sb.Equals((StringBuilder?)null), sb => sb.Equals((Substitute?)null));
            Check(wrong, text, "Equals(span)", sb => sb.Equals(text.AsSpan()), sb => sb.Equals(text.AsSpan()));
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void Numbers_bools_chars_and_objects_append_as_dotnet_appends_them()
    {
        var wrong = new List<string>();
        Check(wrong, "", "numbers", sb => sb.Append((sbyte)-5).Append((byte)250).Append((short)-300).Append((ushort)60000)
                .Append(-70000).Append(4000000000u).Append(-5000000000L).Append(18000000000000000000ul).Append(1.5f)
                .Append(-2.25).Append(double.NaN).Append(float.PositiveInfinity).Append(1e21).Append(true).Append(false)
                .Append('c').Append((object?)null).Append((object)7).Append("é€😀").Append("x".ToCharArray()).Append((char[]?)null)
                .Append("span".AsSpan()).Append((string?)null).Append(new StringBuilder("sb")),
            sb => sb.Append((sbyte)-5).Append((byte)250).Append((short)-300).Append((ushort)60000)
                .Append(-70000).Append(4000000000u).Append(-5000000000L).Append(18000000000000000000ul).Append(1.5f)
                .Append(-2.25).Append(double.NaN).Append(float.PositiveInfinity).Append(1e21).Append(true).Append(false)
                .Append('c').Append((object?)null).Append((object)7).Append("é€😀").Append("x".ToCharArray()).Append((char[]?)null)
                .Append("span".AsSpan()).Append((string?)null).Append(new Substitute("sb")));
        Check(wrong, "0123", "inserted numbers", sb => sb.Insert(1, (sbyte)-5).Insert(2, (byte)250).Insert(0, (short)-3)
                .Insert(3, (ushort)6).Insert(4, -7).Insert(5, 4u).Insert(6, -5L).Insert(7, 18ul).Insert(8, 1.5f).Insert(9, -2.25)
                .Insert(0, true).Insert(1, 'c').Insert(2, (object)8).Insert(1, "x".ToCharArray()),
            sb => sb.Insert(1, (sbyte)-5).Insert(2, (byte)250).Insert(0, (short)-3)
                .Insert(3, (ushort)6).Insert(4, -7).Insert(5, 4u).Insert(6, -5L).Insert(7, 18ul).Insert(8, 1.5f).Insert(9, -2.25)
                .Insert(0, true).Insert(1, 'c').Insert(2, (object)8).Insert(1, "x".ToCharArray()));
        Assert.True(wrong.Count == 0, Report(wrong));
    }

    [Fact]
    public void A_builder_with_a_maximum_capacity_refuses_what_dotnets_refuses()
    {
        var wrong = new List<string>();
        foreach (var max in new[] { 1, 4, 20 })
        {
            foreach (var count in new[] { 1, 3, 4, 5, 19, 21 })
            {
                var text = new string('t', count);
                CheckMax(wrong, max, $"Append(\"{text}\")", sb => sb.Append(text), sb => sb.Append(text));
                CheckMax(wrong, max, $"Append('x', {count})", sb => sb.Append('x', count), sb => sb.Append('x', count));
                CheckMax(wrong, max, $"Append(x) * {count}", sb => Repeat(count, () => sb.Append('x')), sb => Repeat(count, () => sb.Append('x')));
                CheckMax(wrong, max, $"Append(chars[{count}])", sb => sb.Append(text.ToCharArray()), sb => sb.Append(text.ToCharArray()));
                CheckMax(wrong, max, $"Append(builder of {count})", sb => sb.Append(new StringBuilder(text)),
                    sb => sb.Append(new Substitute(text)));
                CheckMax(wrong, max, $"Insert(0, \"{text}\")", sb => sb.Insert(0, text), sb => sb.Insert(0, text));
                CheckMax(wrong, max, $"Insert(0, \"{text}\", 2)", sb => sb.Insert(0, text, 2), sb => sb.Insert(0, text, 2));
                CheckMax(wrong, max, $"Insert(0, {count})", sb => sb.Insert(0, count), sb => sb.Insert(0, count));
                CheckMax(wrong, max, $"Length = {count}", sb => sb.Length = count, sb => sb.Length = count);
                CheckMax(wrong, max, $"Capacity = {count}", sb => sb.Capacity = count, sb => sb.Capacity = count);
                CheckMax(wrong, max, $"EnsureCapacity({count})", sb => sb.EnsureCapacity(count), sb => sb.EnsureCapacity(count));
                CheckMax(wrong, max, $"Replace(\"t\", \"{text}\")", sb => sb.Append('t').Replace("t", text),
                    sb => sb.Append('t').Replace("t", text));
            }
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    /// <summary>
    /// Thousands of random sequences of appends (of every kind), capacities and lengths set, and of the other members:
    /// the text is always .NET's, and the capacity too while the builder only appends (see StringBuilder's remarks).
    /// </summary>
    [Fact]
    public void Random_sequences_of_calls_build_dotnets_text()
    {
        var wrong = new List<string>();
        var random = new Random(7);
        for (var run = 0; run < 3000 && wrong.Count == 0; run++)
        {
            var appendsOnly = run % 2 == 0;
            var dotnet = run % 3 == 0 ? new StringBuilder() : new StringBuilder(random.Next(0, 40));
            var substitute = new Substitute(dotnet.Capacity);
            var steps = new List<string>();
            for (var step = 0; step < 40 && wrong.Count == 0; step++)
            {
                var (what, onDotnet, onSubstitute) = RandomCall(random, appendsOnly);
                steps.Add(what);
                var (expected, actual) = (Outcome(() => onDotnet(dotnet)), Outcome(() => onSubstitute(substitute)));
                var (expectedText, actualText) = (dotnet.ToString(), substitute.ToString());
                if (expected != actual || expectedText != actualText || dotnet.Length != substitute.Length
                    || (appendsOnly && dotnet.Capacity != substitute.Capacity))
                {
                    wrong.Add($"after {string.Join("; ", steps)}: .NET {expected} \"{expectedText}\" ({dotnet.Length} of " +
                        $"{dotnet.Capacity}), substitute {actual} \"{actualText}\" ({substitute.Length} of {substitute.Capacity})");
                }
            }
        }

        Assert.True(wrong.Count == 0, Report(wrong));
    }

    private static (string What, Func<StringBuilder, object?> OnDotnet, Func<Substitute, object?> OnSubstitute) RandomCall(
        Random random, bool appendsOnly)
    {
        var text = new string((char)('a' + random.Next(26)), random.Next(0, 30));
        var c = (char)random.Next(0x20, 0x3000);
        var n = random.Next(-1, 40);
        switch (random.Next(appendsOnly ? 8 : 16))
        {
            case 0:
                return ($"Append(\"{text}\")", sb => sb.Append(text), sb => sb.Append(text));
            case 1:
                return ($"Append('{c}')", sb => sb.Append(c), sb => sb.Append(c));
            case 2:
                return ($"Append('{c}', {n})", sb => sb.Append(c, n), sb => sb.Append(c, n));
            case 3:
                return ($"Append({n * 12345L})", sb => sb.Append(n * 12345L), sb => sb.Append(n * 12345L));
            case 4:
                return ($"Length = Length + {Math.Max(n, 0)}", sb => sb.Length += Math.Max(n, 0), sb => sb.Length += Math.Max(n, 0));
            case 5:
                return ($"Capacity = Length + {n}", sb => sb.Capacity = sb.Length + n, sb => sb.Capacity = sb.Length + n);
            case 6:
                // (what it returns is the capacity, which is .NET's only while the builder appends)
                return ($"EnsureCapacity({n * 3})", sb => appendsOnly ? sb.EnsureCapacity(n * 3) : Ignore(sb.EnsureCapacity(n * 3)),
                    sb => appendsOnly ? sb.EnsureCapacity(n * 3) : Ignore(sb.EnsureCapacity(n * 3)));
            case 7:
                return ($"Append(itself)", sb => sb.Append(sb), sb => sb.Append(sb));
            case 8:
                return ($"Insert({n}, \"{text}\")", sb => sb.Insert(Math.Min(n, sb.Length), text), sb => sb.Insert(Math.Min(n, sb.Length), text));
            case 9:
                return ($"Remove({n}, 3)", sb => sb.Remove(Math.Min(n, sb.Length), Math.Min(3, sb.Length - Math.Min(n, sb.Length))),
                    sb => sb.Remove(Math.Min(n, sb.Length), Math.Min(3, sb.Length - Math.Min(n, sb.Length))));
            case 10:
                return ($"Replace('{text.FirstOrDefault('a')}', '{c}')", sb => sb.Replace(text.FirstOrDefault('a'), c),
                    sb => sb.Replace(text.FirstOrDefault('a'), c));
            case 11:
                var old = text.Length > 2 ? text[..2] : "aa";
                return ($"Replace(\"{old}\", \"{text}\")", sb => sb.Replace(old, text), sb => sb.Replace(old, text));
            case 12:
                return ("Clear()", sb => sb.Clear(), sb => sb.Clear());
            case 13:
                return ($"Length = {n}", sb => sb.Length = Math.Max(n, 0), sb => sb.Length = Math.Max(n, 0));
            case 14:
                return ($"[{n}] = '{c}'", sb => sb[Math.Min(n, sb.Length - 1)] = c, sb => sb[Math.Min(n, sb.Length - 1)] = c);
            default:
                return ($"Insert({n}, '{c}')", sb => sb.Insert(Math.Min(Math.Max(n, 0), sb.Length), c),
                    sb => sb.Insert(Math.Min(Math.Max(n, 0), sb.Length), c));
        }
    }

    private static object? Ignore(int value) => value >= 0 ? null : value;

    private static object? Repeat(int count, Action action)
    {
        for (var i = 0; i < count; i++)
        {
            action();
        }

        return null;
    }

    private static string CopyTo(StringBuilder sb, int sourceIndex, int count)
    {
        var destination = new char[8];
        sb.CopyTo(sourceIndex, destination.AsSpan(), count);
        return new string(destination);
    }

    private static string CopyTo(Substitute sb, int sourceIndex, int count)
    {
        var destination = new char[8];
        sb.CopyTo(sourceIndex, destination.AsSpan(), count);
        return new string(destination);
    }

    private static string CopyTo(StringBuilder sb, int sourceIndex, int destinationIndex, int count)
    {
        var destination = new char[4];
        sb.CopyTo(sourceIndex, destination, destinationIndex, count);
        return new string(destination);
    }

    private static string CopyTo(Substitute sb, int sourceIndex, int destinationIndex, int count)
    {
        var destination = new char[4];
        sb.CopyTo(sourceIndex, destination, destinationIndex, count);
        return new string(destination);
    }

    /// <summary>One call on a builder of <paramref name="text"/>: its result, then the builder's text, length and capacity.</summary>
    private static void Check(List<string> wrong, string text, string what, Func<StringBuilder, object?> dotnet,
        Func<Substitute, object?> substitute)
    {
        // a builder made with room for its text, which keeps one chunk in .NET while it fits
        var (a, b) = (new StringBuilder(text, 64), new Substitute(text, 64));
        Compare(wrong, $"\"{text}\".{what}", () => $"{Outcome(() => dotnet(a))} {Describe(a)}",
            () => $"{Outcome(() => substitute(b))} {Describe(b)}");
    }

    /// <summary>One call on a builder of capacity 2 and at most <paramref name="max"/> chars.</summary>
    private static void CheckMax(List<string> wrong, int max, string what, Func<StringBuilder, object?> dotnet,
        Func<Substitute, object?> substitute)
    {
        var (a, b) = (new StringBuilder(1, max), new Substitute(1, max));
        Compare(wrong, $"(max {max}).{what}", () => $"{Outcome(() => dotnet(a))} {Describe(a)} {a.MaxCapacity}",
            () => $"{Outcome(() => substitute(b))} {Describe(b)} {b.MaxCapacity}");
    }

    private static string Describe(StringBuilder sb) => $"\"{sb}\" ({sb.Length} of {sb.Capacity})";

    private static string Describe(Substitute sb) => $"\"{sb}\" ({sb.Length} of {sb.Capacity})";

    private static void Compare(List<string> wrong, string what, Func<object?> dotnet, Func<object?> substitute)
    {
        var (expected, actual) = (Outcome(dotnet), Outcome(substitute));
        if (expected != actual)
        {
            wrong.Add($"{what}: .NET {expected}, substitute {actual}");
        }
    }

    /// <summary>What a call gives: its result as text (a builder as its text), or the exception it throws, with its message.</summary>
    private static string Outcome(Func<object?> call)
    {
        try
        {
            return call() switch
            {
                StringBuilder sb => $"\"{sb}\"",
                Substitute sb => $"\"{sb}\"",
                var result => $"{result}",
            };
        }
        catch (Exception e)
        {
            return $"{e.GetType().FullName}: {e.Message}";
        }
    }

    private static string Report(List<string> wrong) => $"{wrong.Count} results differ:\n{string.Join("\n", wrong.Take(30))}";
}
