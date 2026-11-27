using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Collections;

/// <summary>
/// C# programs of M3's collections, for the PC and the Pico tests: try/finally and using, exceptions as values, arrays as
/// interfaces, and the library's List, Dictionary, HashSet, Queue and Stack, each printing what it computes, which must be
/// what .NET prints, compiled in Release and in Debug. <see cref="Failures"/> are programs that stop where .NET throws.
/// </summary>
internal static class CollectionPrograms
{
    /// <summary>
    /// try/finally as plain branches: nested regions, a return out of two of them, break and continue out of a try in a
    /// loop, a try inside a finally, using statements and declarations of a class and of a struct, a null in a using, and
    /// foreach over enumerators that are disposed (a class's through IEnumerable&lt;T&gt;, a struct's).
    /// </summary>
    public const string Finally = """
        using System;
        using System.Collections;
        using System.Collections.Generic;

        class Resource : IDisposable
        {
            public static int Open;
            readonly string name;
            public Resource(string name) { this.name = name; Open++; Console.WriteLine("open " + name); }
            public void Dispose() { Open--; Console.WriteLine("close " + name); }
        }

        struct Lock : IDisposable
        {
            public int Id;
            public void Dispose() => Console.WriteLine("unlock " + Id);
        }

        class Countdown : IEnumerable<int>
        {
            readonly int from;
            public Countdown(int from) => this.from = from;
            public IEnumerator<int> GetEnumerator() => new Walker(from);
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            sealed class Walker : IEnumerator<int>
            {
                int value;
                public Walker(int from) => value = from + 1;
                public int Current => value;
                object IEnumerator.Current => null;
                public bool MoveNext() => --value > 0;
                public void Reset() { }
                public void Dispose() => Console.WriteLine("walker disposed at " + value);
            }
        }

        struct Steps
        {
            readonly int count;
            public Steps(int count) => this.count = count;
            public StepEnumerator GetEnumerator() => new StepEnumerator(count);
        }

        struct StepEnumerator : IDisposable
        {
            readonly int count;
            int index;
            public StepEnumerator(int count) { this.count = count; index = 0; }
            public int Current => index * 10;
            public bool MoveNext() => ++index <= count;
            public void Dispose() => Console.WriteLine("steps done at " + index);
        }

        class Program
        {
            static int Nested(int k)
            {
                using var outer = new Resource("outer" + k);
                using (var inner = new Resource("inner" + k))
                {
                    try
                    {
                        if (k == 1)
                        {
                            return 10;
                        }

                        try
                        {
                            if (k == 2)
                            {
                                return 20;
                            }

                            Console.WriteLine("body " + k);
                        }
                        finally
                        {
                            Console.WriteLine("inner finally " + k);
                            try
                            {
                                Console.WriteLine("try in a finally");
                            }
                            finally
                            {
                                Console.WriteLine("finally in a finally");
                            }
                        }
                    }
                    finally
                    {
                        Console.WriteLine("middle finally " + k);
                    }
                }

                Console.WriteLine("after the using " + k);
                return 30;
            }

            static int Loop()
            {
                var total = 0;
                for (var i = 0; i < 8; i++)
                {
                    try
                    {
                        if (i == 2)
                        {
                            continue;
                        }

                        if (i == 6)
                        {
                            break;
                        }

                        total += i;
                    }
                    finally
                    {
                        total += 100;
                    }
                }

                return total;
            }

            static int Find(int[] values, int wanted)
            {
                var index = 0;
                foreach (var value in new Countdown(values.Length))
                {
                    using (new Lock { Id = value })
                    {
                        if (values[value - 1] == wanted)
                        {
                            return value - 1;
                        }
                    }

                    index++;
                }

                return -1 - index;
            }

            static void Main()
            {
                Console.WriteLine(Nested(1));
                Console.WriteLine(Nested(2));
                Console.WriteLine(Nested(3));
                Console.WriteLine(Resource.Open);
                Console.WriteLine(Loop());
                Console.WriteLine(Find(new[] { 4, 8, 15, 16 }, 8));
                Console.WriteLine(Find(new[] { 4, 8 }, 99));
                foreach (var step in new Steps(3))
                {
                    Console.WriteLine("step " + step);
                }

                IEnumerable<int> countdown = new Countdown(3);
                foreach (var value in countdown)
                {
                    if (value == 2)
                    {
                        break;
                    }

                    Console.WriteLine("count " + value);
                }

                Resource nothing = null;
                using (nothing)
                {
                    Console.WriteLine("a null in a using");
                }
            }
        }
        """;

    /// <summary>
    /// Exceptions as values (none thrown): the message and ToString of the library's exceptions and of the program's own
    /// (derived from Exception and from ArgumentException, with an override of Message and of ToString), their parameter
    /// names, inner exceptions and default messages, as .NET 10 writes them.
    /// </summary>
    public const string Exceptions = """
        using System;
        using System.Collections.Generic;

        class Overheated : Exception
        {
            public readonly int Degrees;
            public Overheated(int degrees) : base("too hot: " + degrees) => Degrees = degrees;
        }

        class Quiet : Exception
        {
        }

        class Range : ArgumentException
        {
            public Range(string parameter) : base("out of range", parameter) { }
            public override string Message => "[" + base.Message + "]";
        }

        class Shown : Exception
        {
            public override string ToString() => "shown!";
        }

        class Program
        {
            static void Show(Exception e)
            {
                Console.WriteLine(e.ToString());
                Console.WriteLine(e.Message);
            }

            static void Main()
            {
                Show(new Exception());
                Show(new Exception(""));
                Show(new InvalidOperationException());
                Show(new InvalidOperationException(null));
                Show(new ArgumentException("bad", "value"));
                Show(new ArgumentException(null, "value"));
                Show(new ArgumentNullException("name"));
                Show(new ArgumentNullException("name", "given nothing"));
                Show(new ArgumentOutOfRangeException("index"));
                Show(new ArgumentOutOfRangeException("index", "too big"));
                Show(new ArgumentOutOfRangeException("index", "seven", "too big"));
                Show(new KeyNotFoundException());
                Show(new NotSupportedException());
                Show(new NotImplementedException());
                Show(new IndexOutOfRangeException());
                Show(new FormatException("not a number"));
                Show(new OverflowException());
                Show(new DivideByZeroException());
                Show(new NullReferenceException());
                Show(new InvalidCastException());
                Show(new ObjectDisposedException("sensor"));
                Show(new TimeoutException());
                Show(new ApplicationException());
                Show(new SystemException(null));
                Show(new Overheated(90));
                Show(new Quiet());
                Show(new Range("speed"));
                Show(new Shown());
                var outer = new InvalidOperationException("outer", new ArgumentException("inner", "p"));
                Show(outer);
                Console.WriteLine(outer.InnerException.Message);
                Console.WriteLine(outer.GetBaseException().Message);
                Console.WriteLine(new ArgumentException("x", "y").ParamName);
                Console.WriteLine(new Overheated(70).Degrees);
                Exception kept = new Range("left");
                Console.WriteLine(kept is ArgumentException);
                Console.WriteLine(kept is Range range ? range.ParamName : "none");
            }
        }
        """;

    /// <summary>
    /// Arrays as the interfaces of collections: parameters, locals and fields of IEnumerable&lt;T&gt;, IList&lt;T&gt;,
    /// IReadOnlyList&lt;T&gt;, ICollection&lt;T&gt; and IReadOnlyCollection&lt;T&gt; given arrays (of numbers, of
    /// strings, of a class), foreach over them, the indexer through IList&lt;T&gt;, type tests of objects that are arrays,
    /// variance (an IEnumerable&lt;object&gt; that is a string[]), and the collections given arrays.
    /// </summary>
    public const string Arrays = """
        using System;
        using System.Collections;
        using System.Collections.Generic;

        class Reading
        {
            public int Value;
            public Reading(int value) => Value = value;
            public override string ToString() => "r" + Value;
        }

        class Program
        {
            static IEnumerable<int> kept;

            static int Sum(IEnumerable<int> values)
            {
                var sum = 0;
                foreach (var value in values)
                {
                    sum += value;
                }

                return sum;
            }

            static int Count<T>(IReadOnlyCollection<T> values) => values.Count;

            static string Join(IReadOnlyList<string> words)
            {
                var text = "";
                for (var i = 0; i < words.Count; i++)
                {
                    text += words[i] + (i + 1 < words.Count ? "," : "");
                }

                return text;
            }

            static void Main()
            {
                int[] numbers = { 3, 1, 4, 1, 5, 9, 2, 6 };
                Console.WriteLine(Sum(numbers));
                Console.WriteLine(Sum(new List<int> { 10, 20 }));
                Console.WriteLine(Count(numbers));
                IList<int> list = numbers;
                list[0] = 7;
                Console.WriteLine(numbers[0] + " " + list.Count + " " + list.IsReadOnly + " " + list.IndexOf(9) + " " +
                    list.Contains(42));
                ICollection<int> collection = numbers;
                var copy = new int[10];
                collection.CopyTo(copy, 1);
                Console.WriteLine(copy[1] + copy[8]);
                string[] words = { "red", "green", "blue" };
                Console.WriteLine(Join(words));
                IEnumerable<object> objects = words;
                foreach (var thing in objects)
                {
                    Console.Write(thing + ";");
                }

                Console.WriteLine();
                Reading[] readings = { new Reading(5), new Reading(6) };
                IEnumerable<Reading> each = readings;
                foreach (var reading in each)
                {
                    Console.Write(reading + " ");
                }

                Console.WriteLine();
                object o = numbers;
                Console.WriteLine((o is IList<int>) + " " + (o is ICollection<int>) + " " + (o is IEnumerable<int>) + " " +
                    (o is IList<string>) + " " + (o is int[]) + " " + (o is IEnumerable));
                IReadOnlyList<int> read = (IReadOnlyList<int>)o;
                Console.WriteLine(read[read.Count - 1]);
                kept = new[] { 100, 200 };
                Console.WriteLine(Sum(kept));
                var fromArray = new List<int>(numbers);
                fromArray.AddRange(new[] { 11, 12 });
                fromArray.InsertRange(1, new[] { 0 });
                Console.WriteLine(fromArray.Count + " " + fromArray[1] + " " + fromArray[fromArray.Count - 1]);
                var set = new HashSet<string>(words);
                set.UnionWith(new[] { "cyan", "red" });
                Console.WriteLine(set.Count + " " + set.Contains("cyan"));
                var queue = new Queue<int>(numbers);
                var stack = new Stack<int>(numbers);
                Console.WriteLine(queue.Peek() + " " + stack.Peek());
                var e = ((IEnumerable<int>)numbers).GetEnumerator();
                e.MoveNext();
                e.MoveNext();
                Console.WriteLine(e.Current);
                IEnumerable plain = words;
                foreach (var thing in plain)
                {
                    Console.Write(thing);
                }

                Console.WriteLine();
            }
        }
        """;

    /// <summary>
    /// List&lt;T&gt;: every member, of numbers, text and a class sorted by IComparable&lt;T&gt;, its capacity as it grows
    /// and shrinks, the methods that take a delegate, a collection initializer and collection expressions.
    /// </summary>
    public const string Lists = """
        using System;
        using System.Collections.Generic;

        class Task : IComparable<Task>
        {
            public string Name;
            public int Priority;
            public Task(string name, int priority) { Name = name; Priority = priority; }
            public int CompareTo(Task other) => Priority.CompareTo(other.Priority);
            public override string ToString() => Name + ":" + Priority;
        }

        class ByName : IComparer<Task>
        {
            public int Compare(Task x, Task y) => string.CompareOrdinal(x.Name, y.Name);
        }

        class Program
        {
            static void Show<T>(List<T> list)
            {
                Console.Write(list.Count + "/" + list.Capacity + ":");
                foreach (var item in list)
                {
                    Console.Write(" " + item);
                }

                Console.WriteLine();
            }

            static void Main()
            {
                var numbers = new List<int>();
                for (var i = 0; i < 13; i++)
                {
                    numbers.Add(i * 7 % 11);
                }

                Show(numbers);
                numbers.Insert(0, 42);
                numbers.Insert(numbers.Count, 43);
                numbers.RemoveAt(2);
                Console.WriteLine(numbers.Remove(6) + " " + numbers.Remove(99));
                Show(numbers);
                Console.WriteLine(numbers.IndexOf(3) + " " + numbers.IndexOf(3, 6) + " " + numbers.IndexOf(3, 2, 3) + " " +
                    numbers.LastIndexOf(3) + " " + numbers.LastIndexOf(3, 5) + " " + numbers.Contains(10));
                numbers.Sort();
                Show(numbers);
                Console.WriteLine(numbers.BinarySearch(5) + " " + numbers.BinarySearch(100) + " " + numbers.BinarySearch(-1));
                numbers.Reverse();
                numbers.Reverse(1, 3);
                Show(numbers);
                numbers.RemoveRange(2, 4);
                Show(numbers);
                numbers.AddRange(new List<int> { 70, 80 });
                numbers.InsertRange(1, new List<int> { 1, 2, 3 });
                numbers.InsertRange(0, numbers);
                Show(numbers);
                var range = numbers.GetRange(3, 4);
                Show(range);
                var array = numbers.ToArray();
                Console.WriteLine(array.Length + " " + array[0] + " " + array[array.Length - 1]);
                var target = new int[numbers.Count + 2];
                numbers.CopyTo(target, 2);
                numbers.CopyTo(1, target, 0, 2);
                Console.WriteLine(target[0] + " " + target[1] + " " + target[2]);
                numbers.Capacity = 40;
                Console.WriteLine(numbers.Capacity + " " + numbers.EnsureCapacity(100) + " " + numbers.EnsureCapacity(3));
                numbers.TrimExcess();
                Show(numbers);
                numbers[0] = -1;
                Console.WriteLine(numbers[0] + numbers[numbers.Count - 1]);
                numbers.Clear();
                Show(numbers);
                numbers.TrimExcess();
                Show(numbers);

                var tasks = new List<Task> { new Task("wash", 3), new Task("cook", 1), new Task("read", 2), new Task("nap", 2) };
                tasks.Sort();
                Show(tasks);
                tasks.Sort(new ByName());
                Show(tasks);
                tasks.Sort((a, b) => b.Priority - a.Priority);
                Show(tasks);
                Console.WriteLine(tasks.Find(t => t.Priority == 2) + " " + tasks.FindLast(t => t.Priority == 2) + " " +
                    tasks.FindIndex(t => t.Name == "cook") + " " + tasks.FindLastIndex(t => t.Priority > 5) + " " +
                    (tasks.Find(t => t.Priority > 5) == null));
                Show(tasks.FindAll(t => t.Priority >= 2));
                Console.WriteLine(tasks.Exists(t => t.Name == "nap") + " " + tasks.TrueForAll(t => t.Priority > 0));
                var names = tasks.ConvertAll(t => t.Name.ToUpper());
                Show(names);
                var total = 0;
                tasks.ForEach(t => total += t.Priority);
                Console.WriteLine(total);
                Console.WriteLine(tasks.RemoveAll(t => t.Priority == 2));
                Show(tasks);
                Console.WriteLine(tasks.BinarySearch(new Task("x", 3), Comparer<Task>.Default));

                var words = new List<string> { "pear", "apple", "fig", null, "kiwi" };
                words.Sort();
                Show(words);
                Console.WriteLine(words.IndexOf(null) + " " + words.Contains("fig") + " " + words.IndexOf("kiwi"));
                List<int> fromExpression = [5, 3, 9, 1];
                Show(fromExpression);
                List<double> doubles = [2.5, -1.0, 0.0, 10.25];
                doubles.Sort();
                Show(doubles);
                List<int> empty = [];
                Show(empty);
                var copied = new List<int>(fromExpression);
                copied.Add(4);
                Show(copied);
                Console.WriteLine(fromExpression.Count);
            }
        }
        """;

    /// <summary>
    /// Dictionary&lt;TKey, TValue&gt;: a word count, its order after removals (the free entries reused, the last freed
    /// first), the indexer, Add, TryAdd, TryGetValue, ContainsKey, ContainsValue, Remove (both), Keys, Values,
    /// GetValueOrDefault, a custom IEqualityComparer, the initializers, deconstruction, KeyValuePair, the capacity.
    /// </summary>
    public const string Dictionaries = """
        using System;
        using System.Collections.Generic;

        class IgnoreCase : IEqualityComparer<string>
        {
            public bool Equals(string x, string y) => string.Equals(x.ToLower(), y.ToLower());
            public int GetHashCode(string text) => text.ToLower().GetHashCode();
        }

        class Point
        {
            public int X, Y;
            public Point(int x, int y) { X = x; Y = y; }
            public override bool Equals(object other) => other is Point p && p.X == X && p.Y == Y;
            public override int GetHashCode() => X * 31 + Y;
        }

        class Program
        {
            static void Show<TKey, TValue>(Dictionary<TKey, TValue> map)
            {
                Console.Write(map.Count + ":");
                foreach (var (key, value) in map)
                {
                    Console.Write(" " + key + "=" + value);
                }

                Console.WriteLine();
            }

            static void Main()
            {
                var text = "the quick brown fox jumps over the lazy dog and the quick cat";
                var counts = new Dictionary<string, int>();
                foreach (var word in text.Split(' '))
                {
                    counts[word] = counts.GetValueOrDefault(word) + 1;
                }

                Show(counts);
                Console.WriteLine(counts.Remove("brown") + " " + counts.Remove("brown") + " " + counts.Remove("lazy", out var lazy) + lazy);
                counts.Add("emu", 1);
                counts["yak"] = 2;
                counts.TryAdd("ox", 3);
                Console.WriteLine(counts.TryAdd("ox", 4) + " " + counts["ox"]);
                Show(counts);
                Console.WriteLine(counts.ContainsKey("fox") + " " + counts.ContainsKey("wolf") + " " + counts.ContainsValue(3) + " " +
                    counts.ContainsValue(30));
                Console.WriteLine(counts.TryGetValue("the", out var the) + " " + the + " " + counts.TryGetValue("wolf", out var wolf) +
                    " " + wolf);
                foreach (var key in counts.Keys)
                {
                    Console.Write(key + ",");
                }

                Console.WriteLine();
                var values = 0;
                foreach (var value in counts.Values)
                {
                    values += value;
                }

                Console.WriteLine(values + " " + counts.Keys.Count + " " + counts.Values.Count);
                var keys = new string[counts.Count];
                counts.Keys.CopyTo(keys, 0);
                Console.WriteLine(keys[0] + " " + keys[keys.Length - 1]);
                foreach (var pair in counts)
                {
                    if (pair.Value > 1)
                    {
                        counts[pair.Key] = pair.Value * 10;
                    }
                }

                Show(counts);
                foreach (var pair in counts)
                {
                    if (pair.Key.Length == 3)
                    {
                        counts.Remove(pair.Key);
                    }
                }

                Show(counts);
                Console.WriteLine(counts.EnsureCapacity(0) + " " + counts.Capacity);
                counts.TrimExcess();
                Console.WriteLine(counts.Capacity);
                Show(counts);
                counts.Clear();
                Show(counts);
                counts["again"] = 1;
                Show(counts);

                var squares = new Dictionary<int, int>();
                for (var i = 0; i < 40; i++)
                {
                    squares[i] = i * i;
                    if (i % 3 == 0)
                    {
                        squares.Remove(i / 2);
                    }
                }

                Show(squares);
                Console.WriteLine(squares.Capacity + " " + squares[39]);
                var named = new Dictionary<string, int> { ["one"] = 1, ["two"] = 2 };
                var listed = new Dictionary<int, string> { { 1, "uno" }, { 2, "dos" } };
                Show(named);
                Show(listed);
                var ignoring = new Dictionary<string, int>(new IgnoreCase()) { ["Hello"] = 1 };
                ignoring["HELLO"] += 5;
                Show(ignoring);
                var points = new Dictionary<Point, string> { [new Point(1, 2)] = "a" };
                Console.WriteLine(points[new Point(1, 2)] + " " + points.ContainsKey(new Point(2, 1)));
                var copy = new Dictionary<string, int>(named);
                copy["three"] = 3;
                Show(copy);
                var pairs = new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>("k", 9), KeyValuePair.Create("m", 8) };
                var fromPairs = new Dictionary<string, int>(pairs);
                Show(fromPairs);
                foreach (var pair in fromPairs)
                {
                    Console.WriteLine(pair.Key + "->" + pair.Value + " " + pair.ToString());
                }
            }
        }
        """;

    /// <summary>
    /// HashSet&lt;T&gt;: Add, Remove, Contains, its order after removals, UnionWith, IntersectWith, ExceptWith,
    /// SymmetricExceptWith, the set tests, RemoveWhere, CopyTo, a null item, a custom comparer.
    /// </summary>
    public const string Sets = """
        using System;
        using System.Collections.Generic;

        class ByLength : IEqualityComparer<string>
        {
            public bool Equals(string x, string y) => x.Length == y.Length;
            public int GetHashCode(string text) => text.Length;
        }

        class Program
        {
            static void Show<T>(HashSet<T> set)
            {
                Console.Write(set.Count + ":");
                foreach (var item in set)
                {
                    Console.Write(" " + (item == null ? "null" : item.ToString()));
                }

                Console.WriteLine();
            }

            static void Main()
            {
                var set = new HashSet<int> { 5, 3, 9, 3, 1, 12, 7 };
                Show(set);
                Console.WriteLine(set.Add(3) + " " + set.Add(4) + " " + set.Remove(9) + " " + set.Remove(9) + " " + set.Contains(12));
                set.Add(20);
                Show(set);
                set.UnionWith(new List<int> { 9, 30, 1 });
                Show(set);
                set.IntersectWith(new List<int> { 1, 4, 30, 99, 7 });
                Show(set);
                set.ExceptWith(new List<int> { 4 });
                Show(set);
                set.SymmetricExceptWith(new List<int> { 1, 2, 3 });
                Show(set);
                var other = new HashSet<int> { 2, 3 };
                Console.WriteLine(other.IsSubsetOf(set) + " " + set.IsSupersetOf(other) + " " + other.IsProperSubsetOf(set) + " " +
                    set.Overlaps(new[] { 100, 3 }) + " " + set.SetEquals(new[] { 2, 3, 7, 30 }) + " " + set.IsProperSupersetOf(other));
                set.IntersectWith(other);
                Show(set);
                var evens = new HashSet<int>();
                for (var i = 0; i < 30; i++)
                {
                    evens.Add(i);
                }

                Console.WriteLine(evens.RemoveWhere(i => i % 2 == 1));
                Show(evens);
                var target = new int[evens.Count + 1];
                evens.CopyTo(target, 1);
                Console.WriteLine(target[1] + " " + target[target.Length - 1] + " " + evens.EnsureCapacity(0));
                var words = new HashSet<string> { "a", null, "b" };
                Console.WriteLine(words.Contains(null) + " " + words.Remove(null) + " " + words.Contains(null));
                Show(words);
                var lengths = new HashSet<string>(new ByLength()) { "one", "two", "three", "four" };
                Show(lengths);
                var copy = new HashSet<int>(other);
                copy.Add(10);
                Show(copy);
                Show(other);
                copy.Clear();
                Show(copy);
            }
        }
        """;

    /// <summary>
    /// Queue&lt;T&gt; and Stack&lt;T&gt;: a queue of tasks that wraps round its array, a stack of undo steps,
    /// Enqueue, Dequeue, Peek, TryDequeue, TryPeek, Push, Pop, TryPop, Contains, ToArray, CopyTo, Clear, their growth.
    /// </summary>
    public const string QueuesAndStacks = """
        using System;
        using System.Collections.Generic;

        class Program
        {
            static void Main()
            {
                var jobs = new Queue<string>();
                jobs.Enqueue("print");
                jobs.Enqueue("scan");
                jobs.Enqueue("copy");
                Console.WriteLine(jobs.Dequeue() + " " + jobs.Peek() + " " + jobs.Count);
                for (var i = 0; i < 5; i++)
                {
                    jobs.Enqueue("job" + i);
                }

                foreach (var job in jobs)
                {
                    Console.Write(job + " ");
                }

                Console.WriteLine();
                Console.WriteLine(string.Join(",", jobs.ToArray()) + " " + jobs.Contains("job3") + " " + jobs.Contains("print"));
                while (jobs.TryDequeue(out var job))
                {
                    Console.Write(job + ";");
                    if (job == "job1")
                    {
                        jobs.Enqueue("late");
                    }
                }

                Console.WriteLine();
                Console.WriteLine(jobs.TryPeek(out var none) + " " + (none == null) + " " + jobs.Count);
                var numbers = new Queue<int>(new[] { 1, 2, 3 });
                numbers.Dequeue();
                numbers.Enqueue(4);
                numbers.Enqueue(5);
                var copy = new int[6];
                numbers.CopyTo(copy, 1);
                Console.WriteLine(copy[1] + " " + copy[4] + " " + numbers.EnsureCapacity(0));
                numbers.Clear();
                Console.WriteLine(numbers.Count);

                var undo = new Stack<string>();
                undo.Push("type a");
                undo.Push("type b");
                undo.Push("delete");
                Console.WriteLine(undo.Pop() + " " + undo.Peek() + " " + undo.Count);
                undo.Push("bold");
                foreach (var step in undo)
                {
                    Console.Write(step + " | ");
                }

                Console.WriteLine();
                var steps = undo.ToArray();
                Console.WriteLine(steps[0] + " " + steps[steps.Length - 1] + " " + undo.Contains("type a"));
                while (undo.TryPop(out var step))
                {
                    Console.Write(step + ";");
                }

                Console.WriteLine();
                Console.WriteLine(undo.TryPeek(out var top) + " " + (top == null));
                var stack = new Stack<int>(new[] { 7, 8, 9 });
                var target = new int[4];
                stack.CopyTo(target, 1);
                Console.WriteLine(target[1] + " " + target[3] + " " + stack.Pop());
                for (var i = 0; i < 10; i++)
                {
                    stack.Push(i);
                }

                Console.WriteLine(stack.Count + " " + stack.EnsureCapacity(0) + " " + stack.Peek());
                stack.TrimExcess();
                Console.WriteLine(stack.EnsureCapacity(0));
            }
        }
        """;

    /// <summary>
    /// The collections holding objects: boxed numbers in a List&lt;object&gt; and a Dictionary&lt;string, object&gt;,
    /// an array through the non-generic IEnumerable, KeyValuePair printed (boxed).
    /// </summary>
    public const string Objects = """
        using System;
        using System.Collections;
        using System.Collections.Generic;

        class Program
        {
            static void Main()
            {
                var settings = new Dictionary<string, object> { ["volume"] = 7, ["name"] = "radio", ["on"] = true, ["gain"] = 1.5 };
                foreach (var pair in settings)
                {
                    Console.WriteLine(pair.Key + " = " + pair.Value);
                }

                Console.WriteLine(settings.TryGetValue("name", out var name) + " " + name);
                var things = new List<object> { 1, "two", 3L, 'c' };
                foreach (var thing in things)
                {
                    Console.WriteLine(thing);
                }

                Console.WriteLine(things.Contains(1) + " " + things.Contains(2) + " " + things.IndexOf("two") + " " + things.IndexOf(3L));
                IEnumerable plain = new[] { 4, 5, 6 };
                foreach (var number in plain)
                {
                    Console.Write(number);
                }

                Console.WriteLine();
                foreach (var pair in new Dictionary<int, string> { [1] = "a", [2] = "b" })
                {
                    Console.WriteLine(pair);
                }
            }
        }
        """;

    /// <summary>
    /// Tens of thousands of short-lived collections, in a small heap: lists, dictionaries, sets, queues and stacks made,
    /// filled and dropped, while some are kept in static fields; a collection that lost an object, or one that read its
    /// arrays wrongly, would change what it prints.
    /// </summary>
    public const string Garbage = """
        using System;
        using System.Collections.Generic;

        class Sample
        {
            public int Value;
            public Sample(int value) => Value = value;
        }

        class Program
        {
            static readonly Dictionary<int, List<Sample>> kept = new Dictionary<int, List<Sample>>();
            static readonly Queue<Sample> recent = new Queue<Sample>();

            static void Main()
            {
                long total = 0;
                for (var round = 0; round < 3000; round++)
                {
                    var list = new List<Sample>();
                    for (var i = 0; i < 12; i++)
                    {
                        list.Add(new Sample(round * 12 + i));
                    }

                    var map = new Dictionary<string, int>();
                    foreach (var sample in list)
                    {
                        map["s" + sample.Value % 7] = sample.Value;
                    }

                    var set = new HashSet<int>(map.Values);
                    var stack = new Stack<Sample>(list);
                    total += set.Count + stack.Pop().Value % 13 + map["s3"] % 5;
                    recent.Enqueue(list[round % 12]);
                    if (recent.Count > 20)
                    {
                        total += recent.Dequeue().Value % 3;
                    }

                    if (round % 250 == 0)
                    {
                        kept[round / 250] = list;
                    }
                }

                long values = 0;
                foreach (var (key, list) in kept)
                {
                    values += key * list.Count + list[5].Value;
                }

                Console.WriteLine(total);
                Console.WriteLine(kept.Count);
                Console.WriteLine(values);
                Console.WriteLine(recent.Count + " " + recent.Peek().Value);
            }
        }
        """;

    /// <summary>Every program that runs to its end, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Finally), Finally),
        (nameof(Exceptions), Exceptions),
        (nameof(Arrays), Arrays),
        (nameof(Lists), Lists),
        (nameof(Dictionaries), Dictionaries),
        (nameof(Sets), Sets),
        (nameof(QueuesAndStacks), QueuesAndStacks),
        (nameof(Objects), Objects),
        (nameof(Garbage), Garbage),
    ];

    /// <summary>
    /// Programs that print a line and then stop where .NET throws, each a statement of Main after Console.WriteLine(1),
    /// with what they need declared before: what they print on standard error must be .NET's first lines (its stack
    /// trace aside).
    /// </summary>
    public static IReadOnlyList<(string Name, string Declarations, string Statement)> Failures { get; } =
    [
        ("ListIndex", "", "Console.WriteLine(new List<int> { 1 }[1]);"),
        ("ListInsert", "", "new List<int>().Insert(1, 1);"),
        ("ListRange", "", "new List<int> { 1, 2 }.RemoveRange(1, 2);"),
        ("ListModified", "", "var list = new List<int> { 1, 2 }; foreach (var x in list) { list.Add(x); }"),
        ("ListNegativeCapacity", "", "Console.WriteLine(new List<int>(-1).Count);"),
        ("MissingKey", "", "Console.WriteLine(new Dictionary<string, int> { [\"a\"] = 1 }[\"b\"]);"),
        ("MissingNumber", "", "Console.WriteLine(new Dictionary<int, int>()[42]);"),
        ("DuplicateKey", "", "var map = new Dictionary<string, int> { { \"a\", 1 } }; map.Add(\"a\", 2);"),
        ("NullKey", "", "string key = null; new Dictionary<string, int>()[key] = 1;"),
        ("DictionaryModified", "", "var map = new Dictionary<int, int> { [1] = 1 }; foreach (var pair in map) { map[2] = 2; }"),
        ("SetModified", "", "var set = new HashSet<int> { 1 }; foreach (var x in set) { set.Add(2); }"),
        ("QueueEmpty", "", "Console.WriteLine(new Queue<int>().Dequeue());"),
        ("QueueModified", "", "var queue = new Queue<int>(new[] { 1 }); foreach (var x in queue) { queue.Enqueue(2); }"),
        ("StackEmpty", "", "Console.WriteLine(new Stack<string>().Peek());"),
        ("StackModified", "", "var stack = new Stack<int>(new[] { 1 }); foreach (var x in stack) { stack.Push(2); }"),
        ("ArrayFixedSize", "", "IList<int> list = new int[2]; list.Add(1);"),
        ("ArrayInterfaceIndex", "", "IList<int> list = new int[2]; Console.WriteLine(list[2]);"),
        ("Thrown", "", "throw new InvalidOperationException(\"the valve is stuck\");"),
        ("ThrownArgument", "static int Check(int speed) => speed < 0 ? throw new ArgumentOutOfRangeException(nameof(speed), \"must not be negative\") : speed;", "Console.WriteLine(Check(-3));"),
        ("ThrownNull", "static string Name(string name) => name ?? throw new ArgumentNullException(nameof(name));", "Console.WriteLine(Name(null));"),
        ("ThrownOwn", "class Overheated : Exception { public Overheated(int degrees) : base(\"too hot: \" + degrees) { } }", "throw new Overheated(90);"),
        ("ThrownInner", "", "throw new InvalidOperationException(\"outer\", new FormatException(\"inner\"));"),
        ("ThrownNothing", "", "throw null;"),
        ("ThrownNotImplemented", "static void Later() => throw new NotImplementedException();", "Later();"),
    ];

    /// <summary>The source of the failure program <paramref name="name"/>.</summary>
    public static string FailureSource(string name)
    {
        var (_, declarations, statement) = Failures.Single(failure => failure.Name == name);
        var classes = declarations.StartsWith("class ", StringComparison.Ordinal) ? declarations : "";
        var members = classes.Length == 0 ? declarations : "";
        return $$"""
            using System;
            using System.Collections.Generic;

            {{classes}}

            class Program
            {
                {{members}}

                static void Main()
                {
                    Console.WriteLine(1);
                    {{statement}}
                }
            }
            """;
    }

    public static string Source(string name) => All.Single(program => program.Name == name).Source;

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

    /// <summary>Each failure's name, compiled in Release.</summary>
    public static TheoryData<string> FailureNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _, _) in Failures)
        {
            data.Add(name);
        }

        return data;
    }

    public static IlAssembly Compile(string source, string name, bool debug, IEnumerable<string>? references = null) =>
        IlAssembly.Load(CSharpCompiler.Compile([source], references ?? [], name, OutputKind.ConsoleApplication,
            debug ? OptimizationLevel.Debug : OptimizationLevel.Release), name + ".dll");
}
