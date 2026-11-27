using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Tuples;

/// <summary>
/// Programs of tuples (System.ValueTuple), of collection expressions of text and objects (lists, arrays and the
/// collections' interfaces through spans of references) and of simple iterators, each printing what it does, so that
/// ChipSharp's output can be compared with .NET's line by line (see <see cref="TupleProgramTests"/> and
/// <see cref="TupleSimulatorTests"/>). A tuple's hash code is compared, not printed: .NET's mixes in a number that
/// changes from run to run. <see cref="Failures"/> stop where .NET throws.
/// </summary>
internal static class TuplePrograms
{
    /// <summary>
    /// Tuples that need no heap: made, named, read and written element by element, deconstructed (into new and existing
    /// variables, swaps), passed, returned, by ref, in fields, static fields, arrays and a struct, compared (==, !=,
    /// Equals, CompareTo, GetHashCode), switched on, past seven elements, of ValueTuple.Create and in generic methods.
    /// Printed straight away, so it runs on a target without a heap too.
    /// </summary>
    public const string Basics = """
        using System;

        enum Mode { Off, On, Blink }

        struct Sensor
        {
            public int Pin;
            public (int Low, int High) Range;

            public bool Holds(int value) => value >= Range.Low && value <= Range.High;
        }

        class Program
        {
            static (int, long) last = (1, 2);
            static (byte Pin, Mode Mode)[] pins = new (byte, Mode)[3];

            static (int Min, int Max) MinMax(Span<int> values)
            {
                int min = values[0], max = values[0];
                foreach (var value in values)
                {
                    if (value < min) min = value;
                    if (value > max) max = value;
                }

                return (min, max);
            }

            static int Sum((int A, int B) pair) => pair.A + pair.B;

            static void Scale(ref (int X, int Y) point, int by)
            {
                point.X *= by;
                point.Y *= by;
            }

            static (Mode, int) Step((Mode Mode, int Count) state) => state switch
            {
                (Mode.Off, _) => (Mode.On, state.Count + 1),
                (Mode.On, > 2) => (Mode.Blink, 0),
                (Mode.On, var count) => (Mode.On, count + 1),
                _ => (Mode.Off, state.Count),
            };

            static string Quadrant((int X, int Y) p) => p switch
            {
                (0, 0) => "origin",
                ( > 0, > 0) => "first",
                ( < 0, > 0) => "second",
                var (x, y) when x == y => "diagonal",
                _ => "other",
            };

            static T Pick<T>((T First, T Second) pair, bool first) => first ? pair.First : pair.Second;

            static (U, T) Flip<T, U>((T, U) pair) => (pair.Item2, pair.Item1);

            static void Main()
            {
                var t = (1, 2);
                Console.WriteLine(t.Item1 + t.Item2);
                (int Count, bool Done) named = (3, true);
                Console.WriteLine(named.Count + " " + named.Done);
                var inferred = (named.Count, t.Item2);
                Console.WriteLine(inferred.Count * inferred.Item2);

                Span<int> values = stackalloc int[] { 3, 1, 4, 1, 5, 9, 2, 6 };
                var (lo, hi) = MinMax(values);
                Console.WriteLine(lo + " " + hi);
                int a = 10, b = 20;
                (a, b) = (b, a);
                Console.WriteLine(a + " " + b);
                (a, b) = MinMax(values.Slice(2, 3));
                Console.WriteLine(a + " " + b);
                var (_, onlySecond) = (7, 8);
                Console.WriteLine(onlySecond);
                Console.WriteLine(Sum((4, 5)));
                var point = (X: 2, Y: 3);
                Scale(ref point, 10);
                Console.WriteLine(point.X + point.Y);

                var state = (Mode.Off, 0);
                for (var i = 0; i < 7; i++)
                {
                    state = Step(state);
                    Console.Write((int)state.Item1 + ":" + state.Item2 + " ");
                }

                Console.WriteLine();
                Console.WriteLine(Quadrant((0, 0)) + " " + Quadrant((1, 2)) + " " + Quadrant((-1, 2)) + " " + Quadrant((-3, -3))
                    + " " + Quadrant((2, -1)));

                last = (5, 6L);
                last.Item2 += 10;
                Console.WriteLine(last.Item1 + last.Item2);
                pins[1] = (25, Mode.Blink);
                pins[2].Pin = 7;
                Console.WriteLine(pins[1].Pin + " " + (int)pins[1].Mode + " " + pins[2].Pin + " " + (int)pins[0].Mode);
                var local = new (int, int)[4];
                for (var i = 0; i < local.Length; i++) local[i] = (i, i * i);
                var squares = 0;
                foreach (var (n, square) in local) squares += square - n;
                Console.WriteLine(squares);

                var sensor = new Sensor { Pin = 3, Range = (10, 20) };
                Console.WriteLine(sensor.Holds(15) + " " + sensor.Holds(25) + " " + (sensor.Range.High - sensor.Range.Low));

                Console.WriteLine((1, 2) == (1, 2));
                Console.WriteLine((1, 2) != (1, 3));
                Console.WriteLine((1, 2L) == (1L, 2));
                var x = (1, 2.5, 'c', true, Mode.On);
                var y = (1, 2.5, 'c', true, Mode.On);
                Console.WriteLine(x == y);
                Console.WriteLine(x.Equals(y));
                Console.WriteLine(x.Equals((1, 2.5, 'c', false, Mode.On)));
                Console.WriteLine(x.GetHashCode() == y.GetHashCode());
                Console.WriteLine((double.NaN, 1).Equals((double.NaN, 1)) + " " + ((double.NaN, 1) == (double.NaN, 1)));
                Console.WriteLine((1, 2).CompareTo((1, 3)) + " " + (2, 0).CompareTo((1, 9)) + " " + (1, 1).CompareTo((1, 1)));
                Console.WriteLine((Mode.Blink, 1).CompareTo((Mode.On, 5)) + " " + (true, 'a').CompareTo((false, 'z')));

                var big = (1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
                Console.WriteLine(big.Item8 + big.Item9 + big.Item10 + big.Rest.Item1);
                Console.WriteLine(big == (1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
                Console.WriteLine(big.Equals((1, 2, 3, 4, 5, 6, 7, 8, 9, 11)));
                Console.WriteLine(big.CompareTo((1, 2, 3, 4, 5, 6, 7, 8, 9, 11)));
                Console.WriteLine(big.GetHashCode() == (1, 2, 3, 4, 5, 6, 7, 8, 9, 10).GetHashCode());
                var seven = (1, 2, 3, 4, 5, 6, 7);
                Console.WriteLine(seven.Item7 + seven.CompareTo((1, 2, 3, 4, 5, 6, 8)));
                var one = ValueTuple.Create(42);
                Console.WriteLine(one.Item1 + " " + one.Equals(ValueTuple.Create(42)) + " " + one.GetHashCode());
                var created = ValueTuple.Create(1, 2L, (short)3);
                Console.WriteLine(created.Item1 + created.Item2 + created.Item3);

                Console.WriteLine(Pick((4, 5), false) + " " + Pick((1.5, 2.5), true));
                var flipped = Flip((1, 'z'));
                Console.WriteLine(flipped.Item1 + " " + flipped.Item2);
                var nested = (Outer: 1, Inner: (A: 2, B: (3, 4)));
                Console.WriteLine(nested.Inner.B.Item2 + nested.Outer);
                var (outer, (inA, (inB, inC))) = nested;
                Console.WriteLine(outer + inA + inB + inC);
            }
        }
        """;

    /// <summary>
    /// The text of tuples (ToString, printed, kept, in interpolations), of every kind of element (null as nothing),
    /// nested and past seven elements; tuples boxed: printed, compared, unboxed, tested, switched on, their type's
    /// name, IComparable and IEquatable through the box.
    /// </summary>
    public const string Text = """
        using System;

        enum Color { Red, Green }

        class Lane
        {
            public string Name;
            public Lane(string name) { Name = name; }
            public override string ToString() => "Lane " + Name;
        }

        struct Point
        {
            public int X, Y;
            public override string ToString() => X + "/" + Y;
        }

        class Program
        {
            static string Describe((string Name, int Count) item) => item.Name + "=" + item.Count;

            static void Main()
            {
                var t = (1, "a");
                Console.WriteLine(t);
                Console.WriteLine(t.ToString());
                Console.WriteLine($"t = {t}, again {t,12}|");
                string kept = "kept " + t;
                Console.WriteLine(kept);
                Console.WriteLine(ValueTuple.Create(5));
                Console.WriteLine((1, 2.5, 'c', true, Color.Green, (string)null, -7L));
                Console.WriteLine((new Lane("north"), new Point { X = 1, Y = 2 }, (Lane)null));
                Console.WriteLine(((1, 2), (3, (4, 5)), "x"));
                Console.WriteLine((1, 2, 3, 4, 5, 6, 7));
                Console.WriteLine((1, 2, 3, 4, 5, 6, 7, 8));
                Console.WriteLine((1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17));
                Console.WriteLine(("a", "b", "c", "d", "e", "f", "g", "h", "i"));
                Console.WriteLine(Describe(("cars", 3)));

                object boxed = (3, "three");
                Console.WriteLine(boxed);
                Console.WriteLine(boxed.Equals((3, "three")) + " " + boxed.Equals((3, "four")) + " " + boxed.Equals(3));
                Console.WriteLine(boxed is ValueTuple<int, string> unboxed ? unboxed.Item2 : "no");
                Console.WriteLine(boxed is ValueTuple<int, int>);
                Console.WriteLine(boxed.GetType().Name + " " + boxed.GetType().FullName);
                Console.WriteLine(boxed.GetHashCode() == (3, "three").GetHashCode());
                var back = ((int, string))boxed;
                Console.WriteLine(back.Item1 + 1);
                object[] mixed = [(1, 2), (Color.Red, "r"), 5, "text"];
                foreach (var item in mixed)
                {
                    var text = item switch
                    {
                        ValueTuple<int, int> pair => "pair " + (pair.Item1 + pair.Item2),
                        ValueTuple<Color, string> (var color, var name) => "color " + color + " " + name,
                        int n => "int " + n,
                        _ => "other " + item,
                    };
                    Console.WriteLine(text);
                }

                IComparable<(int, int)> comparable = (1, 2);
                Console.WriteLine(comparable.CompareTo((1, 1)));
                IEquatable<(int, int)> equatable = (1, 2);
                Console.WriteLine(equatable.Equals((1, 2)));
            }
        }
        """;

    /// <summary>
    /// Tuples in the collections: a list sorted (by the default comparer and a comparison), searched, a dictionary
    /// keyed by tuples and of tuple values, a set, a stack and a queue of them; deconstructed in foreach (a
    /// KeyValuePair of a tuple too); delegates that take and return tuples; the default comparers of tuples; an array
    /// of tuples sorted.
    /// </summary>
    public const string Collections = """
        using System;
        using System.Collections.Generic;

        class Lane
        {
            public string Name;
            public Lane(string name) { Name = name; }
            public override string ToString() => "Lane " + Name;
        }

        class Program
        {
            static (string, int) Parse(string line)
            {
                var parts = line.Split('=');
                return (parts[0], int.Parse(parts[1]));
            }

            static void Main()
            {
                var queue = new List<(int Priority, string Name)>();
                queue.Add((3, "c"));
                queue.Add((1, "a"));
                queue.Add((2, "b"));
                queue.Add((1, "0"));
                queue.Sort();
                foreach (var (priority, name) in queue) Console.Write(priority + ":" + name + " ");
                Console.WriteLine();
                Console.WriteLine(queue.Contains((2, "b")) + " " + queue.IndexOf((3, "c")) + " " + queue.Contains((2, "x")));
                queue.Sort((x, y) => string.CompareOrdinal(y.Name, x.Name));
                Console.WriteLine(queue[0] + " " + queue[3]);
                Console.WriteLine(queue.BinarySearch((5, "z")) + " " + queue.Remove((1, "a")) + " " + queue.Count);

                var grid = new Dictionary<(int Row, int Column), string>();
                grid[(0, 0)] = "origin";
                grid[(1, 2)] = "a";
                grid[(2, 1)] = "b";
                grid.Add((5, 5), "far");
                Console.WriteLine(grid[(1, 2)] + grid[(2, 1)] + " " + grid.ContainsKey((3, 3)) + " " + grid.Count);
                grid.Remove((0, 0));
                foreach (var ((row, column), value) in grid) Console.Write(row + "," + column + "=" + value + " ");
                Console.WriteLine();

                var stats = new Dictionary<string, (int Count, double Sum)>();
                foreach (var line in new[] { "a=1", "b=2", "a=3", "c=4", "b=5", "a=8" })
                {
                    var (key, value) = Parse(line);
                    stats[key] = stats.TryGetValue(key, out var s) ? (s.Count + 1, s.Sum + value) : (1, value);
                }

                foreach (var (key, (count, sum)) in stats) Console.WriteLine(key + ": " + count + " " + sum / count);
                foreach (var pair in stats) Console.WriteLine(pair);

                var seen = new HashSet<(string, int)> { ("a", 1), ("a", 1), ("b", 2), ("a", 2) };
                Console.WriteLine(seen.Count + " " + seen.Contains(("b", 2)) + " " + seen.Contains(("b", 3)));
                var stack = new Stack<(Lane Lane, int Cars)>();
                stack.Push((new Lane("n"), 4));
                stack.Push((new Lane("s"), 9));
                var (top, cars) = stack.Pop();
                Console.WriteLine(top.Name + cars + " " + stack.Peek());
                var waiting = new Queue<(int, char)>();
                waiting.Enqueue((1, 'x'));
                waiting.Enqueue((2, 'y'));
                Console.WriteLine(waiting.Dequeue().Item2 + " " + waiting.Count);

                Func<int, (int Square, int Cube)> powers = x => (x * x, x * x * x);
                Console.WriteLine(powers(3) + " " + powers(2).Cube);
                Action<(int, string)> show = t => Console.WriteLine(t.Item2 + t.Item1);
                show((1, "one"));
                Func<(int A, int B), int> add = p => p.A + p.B;
                Console.WriteLine(add((20, 22)));
                Comparison<(string Name, int Score)> byScore = (p, q) => p.Score.CompareTo(q.Score);
                var scores = new List<(string Name, int Score)> { ("b", 2), ("a", 3), ("c", 1) };
                scores.Sort(byScore);
                foreach (var score in scores) Console.Write(score.Name);
                Console.WriteLine();

                Console.WriteLine(EqualityComparer<(int, string)>.Default.Equals((1, "a"), (1, "a")));
                Console.WriteLine(EqualityComparer<(int, string)>.Default.GetHashCode((1, "a")) == (1, "a").GetHashCode());
                Console.WriteLine(Comparer<(int, string)>.Default.Compare((1, "a"), (1, "b")));
                Console.WriteLine(Comparer<(int, int)>.Default.Compare((2, 0), (1, 5)));
                var lanes = new (Lane, int)[] { (new Lane("e"), 1), (null, 2) };
                Console.WriteLine(lanes[0] + " " + lanes[1] + " " + (lanes[1] == (null, 2)));
                var array = new[] { (3, 'c'), (1, 'a'), (2, 'b') };
                Array.Sort(array);
                Console.WriteLine(array[0].Item2 + "" + array[1].Item2 + array[2].Item2);
            }
        }
        """;

    /// <summary>
    /// Collection expressions of text and objects: into lists, arrays, IEnumerable, IReadOnlyList, IReadOnlyCollection,
    /// ICollection and IList; spreads of lists, arrays, sequences and of an iterator (which C# makes with
    /// Enumerable.ToList and ToArray); empty ones, of objects of any kind, nested, of tuples, and lists' capacities.
    /// </summary>
    public const string CollectionExpressions = """
        using System;
        using System.Collections.Generic;

        class Lane
        {
            public string Name;
            public Lane(string name) { Name = name; }
            public override string ToString() => "Lane " + Name;
        }

        class Program
        {
            static IEnumerable<string> Words() => ["one", "two"];

            static IEnumerable<string> Generated(int count)
            {
                for (var i = 0; i < count; i++)
                {
                    yield return "g" + i;
                }
            }

            static void Show<T>(IEnumerable<T> items)
            {
                var line = "";
                foreach (var item in items) line += item + " ";
                Console.WriteLine(line + "|");
            }

            static void Main()
            {
                List<string> names = ["a", "b"];
                Console.WriteLine(names.Count + " " + names[1] + " " + names.Capacity);
                var north = new Lane("north");
                var south = new Lane("south");
                List<Lane> lanes = [north, south];
                Console.WriteLine(lanes[1] + " " + ReferenceEquals(lanes[0], north));
                string[] letters = ["x", "y"];
                Console.WriteLine(letters[0] + letters.Length);
                Lane[] laneArray = [south, north, null];
                Console.WriteLine(laneArray[0].Name + " " + (laneArray[2] == null));
                IEnumerable<int> numbers = [1, 2];
                Show(numbers);
                IEnumerable<string> words = ["p", "q"];
                Show(words);
                IReadOnlyList<Lane> readOnly = [north, south];
                Console.WriteLine(readOnly[1] + " " + readOnly.Count);
                IReadOnlyCollection<string> collection = ["k"];
                Console.WriteLine(collection.Count);
                ICollection<string> growing = ["x"];
                growing.Add("y");
                Console.WriteLine(growing.Count);
                IList<string> list = ["p", "q"];
                list[0] = "z";
                Console.WriteLine(list[0] + list[1]);

                List<string> more = [.. names, "c"];
                Console.WriteLine(more.Count + more[2] + " " + more.Capacity);
                Lane[] around = [north, .. lanes, south];
                Console.WriteLine(around.Length + " " + around[2].Name);
                Show([.. words, .. names, "end"]);
                string[] copied = [.. names, .. letters, "!"];
                Console.WriteLine(string.Join("", copied));
                List<Lane> fromReadOnly = [.. readOnly, new Lane("east")];
                Show(fromReadOnly);
                List<string> empty = [];
                string[] none = [];
                Console.WriteLine(empty.Count + none.Length + " " + empty.Capacity);
                List<string> spread = [.. Words()];
                string[] spreadArray = [.. Generated(3)];
                List<string> generated = [.. Generated(5)];
                Console.WriteLine(spread.Count + spreadArray[2] + generated[4] + " " + generated.Capacity);
                List<string> mixedSpread = [.. Generated(2), "x", .. Words()];
                Show(mixedSpread);
                List<object> objects = [1, "two", north, 3.5, 'c', null];
                Show(objects);
                List<List<string>> nested = [["a"], ["b", "c"], []];
                Console.WriteLine(nested.Count + " " + nested[1][1] + " " + nested[2].Count);
                List<(string Name, int Count)> tuples = [("a", 1), ("b", 2)];
                Console.WriteLine(tuples[1].Name + tuples[1].Count);
                int[] values = [1, 2, 3];
                List<int> doubled = [.. values, .. values];
                Console.WriteLine(doubled.Count + " " + doubled.Capacity);
                string[][] jagged = [["a", "b"], ["c"]];
                Console.WriteLine(jagged[0][1] + jagged[1][0] + jagged.Length);
            }
        }
        """;

    /// <summary>
    /// Iterators: yield return and yield break, a generic one over another sequence (a foreach inside), one with a try
    /// and finally inside, left early (its finally runs when the foreach disposes it), a collection's GetEnumerator,
    /// one that is an IEnumerator, enumerated twice, spread into a list and an array, and thousands made and dropped.
    /// </summary>
    public const string Iterators = """
        using System;
        using System.Collections;
        using System.Collections.Generic;

        class Lane
        {
            public string Name;
            public Lane(string name) { Name = name; }
        }

        class Road : IEnumerable<Lane>
        {
            private readonly List<Lane> lanes = new();
            public void Add(Lane lane) => lanes.Add(lane);
            public IEnumerator<Lane> GetEnumerator()
            {
                foreach (var lane in lanes) yield return lane;
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        class Program
        {
            static IEnumerable<int> Range(int from, int count)
            {
                for (var i = 0; i < count; i++)
                {
                    if (i == 5) yield break;
                    yield return from + i;
                }
            }

            static IEnumerable<T> Twice<T>(IEnumerable<T> items)
            {
                foreach (var item in items)
                {
                    yield return item;
                    yield return item;
                }
            }

            static IEnumerable<string> Guarded()
            {
                try
                {
                    yield return "a";
                    yield return "b";
                }
                finally
                {
                    Console.WriteLine("cleanup");
                }
            }

            static IEnumerable<T> Pair<T>(T a, T b)
            {
                yield return a;
                yield return b;
            }

            static IEnumerator<int> Countdown(int n)
            {
                while (n > 0) yield return n--;
            }

            static void Main()
            {
                foreach (var x in Range(10, 3)) Console.Write(x + " ");
                Console.WriteLine();
                foreach (var x in Range(0, 9)) Console.Write(x + " ");
                Console.WriteLine();
                foreach (var x in Twice(Range(1, 2))) Console.Write(x);
                Console.WriteLine();
                foreach (var s in Guarded())
                {
                    Console.WriteLine(s);
                    break;
                }
                foreach (var s in Guarded()) Console.WriteLine(s);
                var road = new Road { new Lane("n"), new Lane("s") };
                foreach (var lane in road) Console.Write(lane.Name);
                Console.WriteLine();
                var e = Countdown(3);
                while (e.MoveNext()) Console.Write(e.Current);
                Console.WriteLine();
                var r = Range(1, 3);
                var total = 0;
                foreach (var x in r) total += x;
                foreach (var x in r) total += x;
                Console.WriteLine(total);
                List<string> words = [.. Twice(new[] { "p", "q" })];
                Console.WriteLine(words.Count + words[3]);
                List<string> pairs = [.. Pair("p", "q"), "r"];
                string[] array = [.. Pair("s", "t")];
                Console.WriteLine(pairs.Count + pairs[2] + array[1]);
                long sum = 0;
                for (var round = 0; round < 3000; round++)
                    foreach (var lane in Twice(new[] { new Lane("x" + round) })) sum += lane.Name.Length;
                Console.WriteLine(sum);
            }
        }
        """;

    /// <summary>
    /// Thousands of short-lived readings in tuples and in collection expressions of text and objects, spread into new
    /// lists and arrays, a few kept in a static list of tuples and a dictionary keyed by tuples: they live through many
    /// collections of a small heap.
    /// </summary>
    public const string Garbage = """
        using System;
        using System.Collections.Generic;

        class Reading
        {
            public string Sensor;
            public int Value;
            public Reading(string sensor, int value) { Sensor = sensor; Value = value; }
        }

        class Program
        {
            static List<(string Key, Reading Reading)> kept = [];
            static Dictionary<(int, int), List<string>> cells = new();

            static (Reading Low, Reading High) Range(List<Reading> readings)
            {
                var (low, high) = (readings[0], readings[0]);
                foreach (var reading in readings)
                {
                    if (reading.Value < low.Value) low = reading;
                    if (reading.Value > high.Value) high = reading;
                }

                return (low, high);
            }

            static void Main()
            {
                string[] sensors = ["north", "south", "east", "west"];
                long total = 0;
                for (var round = 0; round < 3000; round++)
                {
                    var name = sensors[round % 4] + round;
                    List<Reading> readings = [new Reading(name, round % 97), new Reading("b" + round, round % 89), new Reading("c", 50)];
                    var (low, high) = Range(readings);
                    total += high.Value - low.Value;
                    List<string> names = [.. sensors, name, low.Sensor];
                    string[] copied = [.. names, "end"];
                    total += copied.Length + names[4].Length;
                    var pair = (name, readings[1]);
                    if (round % 150 == 0)
                    {
                        kept.Add(pair);
                        var cell = (round % 7, round % 5);
                        if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = [];
                        list.Add(name);
                    }

                    if (copied[4] != name || pair.Item2.Sensor != "b" + round)
                    {
                        Console.WriteLine("lost at " + round);
                    }
                }

                Console.WriteLine(total);
                foreach (var (key, reading) in kept) Console.Write(key + "/" + reading.Sensor + ":" + reading.Value + " ");
                Console.WriteLine();
                foreach (var ((row, column), names) in cells) Console.Write(row + "," + column + "=" + names.Count + " ");
                Console.WriteLine();
            }
        }
        """;

    /// <summary>Programs that stop as .NET's do where it throws: a name, the declarations, the statement after "1" is printed.</summary>
    public static IReadOnlyList<(string Name, string Declarations, string Statement)> Failures { get; } =
    [
        // Comparer<T>.Default of a class that implements neither IComparable<T> nor IComparable (the same object is 0)
        ("ComparerOfAClassThatIsNotComparable", Lane,
            "var a = new Lane(); Console.WriteLine(Comparer<Lane>.Default.Compare(a, a) + Comparer<Lane>.Default.Compare(null, a)); " +
            "Console.WriteLine(Comparer<Lane>.Default.Compare(a, new Lane()));"),

        // a sort with it wraps that in .NET's InvalidOperationException
        ("SortOfAClassThatIsNotComparable", Lane, "new List<Lane> { new Lane(), new Lane() }.Sort();"),

        // a tuple's CompareTo compares its elements with that comparer
        ("CompareToOfATupleOfAClassThatIsNotComparable", Lane,
            "var a = new Lane(); Console.WriteLine((1, a).CompareTo((1, a))); Console.WriteLine((1, a).CompareTo((1, new Lane())));"),
    ];

    private const string Lane = "class Lane { public int Cars; }";

    /// <summary>Every program but <see cref="Garbage"/>, which runs in a small heap, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Basics), Basics),
        (nameof(Text), Text),
        (nameof(Collections), Collections),
        (nameof(CollectionExpressions), CollectionExpressions),
        (nameof(Iterators), Iterators),
    ];

    public static string Source(string name) => All.Single(program => program.Name == name).Source;

    /// <summary>The program of the failure <paramref name="name"/>: it prints 1, then does what fails.</summary>
    public static string FailureSource(string name)
    {
        var (_, declarations, statement) = Failures.Single(failure => failure.Name == name);
        return $$"""
            using System;
            using System.Collections.Generic;

            {{declarations}}

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(1);
                    {{statement}}
                }
            }
            """;
    }

    /// <summary>Each program's name, compiled in Release (false) and in Debug (true).</summary>
    public static TheoryData<string, bool> InReleaseAndDebug()
    {
        var data = new TheoryData<string, bool>();
        foreach (var (name, _) in All)
        {
            data.Add(name, false);
            data.Add(name, true);
        }

        return data;
    }

    /// <summary>Each failure's name.</summary>
    public static TheoryData<string> FailureNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _, _) in Failures)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>Compiles a program in Release, or in Debug, and reads it back.</summary>
    public static IlAssembly Compile(string source, string name, bool debug, IEnumerable<string>? references = null) =>
        IlAssembly.Load(CSharpCompiler.Compile([source], references ?? [], name, OutputKind.ConsoleApplication,
            debug ? OptimizationLevel.Debug : OptimizationLevel.Release), name + ".dll");
}
