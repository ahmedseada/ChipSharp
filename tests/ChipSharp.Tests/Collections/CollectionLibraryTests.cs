using Mine = ChipSharp.Library.Collections;

namespace ChipSharp.Tests.Collections;

/// <summary>
/// ChipSharp.Library's collections run on .NET next to .NET's own: thousands of random operations (valid and not), the
/// same on both, after each of which both must hold the same items in the same order, have the same capacity, and have
/// returned or thrown the same (the exception's type and message). So the port keeps .NET's growth, its order after
/// removals, its checks and its messages; the programs that ChipSharp compiles (CollectionProgramTests) then show that it
/// compiles them as .NET runs them.
/// </summary>
public sealed class CollectionLibraryTests
{
    private const int Operations = 4000;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_list_does_what_dotnets_does(int seed)
    {
        var random = new Random(seed);
        var mine = new Mine.List<int>();
        var theirs = new List<int>();
        for (var step = 0; step < Operations; step++)
        {
            var a = random.Next(-2, 30);
            var b = random.Next(-2, 12);
            var value = random.Next(0, 20);
            var (left, right) = (random.Next(10)) switch
            {
                0 or 1 or 2 => (Outcome(() => mine.Add(value)), Outcome(() => theirs.Add(value))),
                3 => (Outcome(() => mine.Insert(a, value)), Outcome(() => theirs.Insert(a, value))),
                4 => (Outcome(() => mine.RemoveAt(a)), Outcome(() => theirs.RemoveAt(a))),
                5 => (Outcome(() => mine.Remove(value)), Outcome(() => theirs.Remove(value))),
                6 => random.Next(6) switch
                {
                    0 => (Outcome(() => mine.RemoveRange(a, b)), Outcome(() => theirs.RemoveRange(a, b))),
                    1 => (Outcome(() => mine.Reverse(a, b)), Outcome(() => theirs.Reverse(a, b))),
                    2 => (Outcome(() => mine.Sort()), Outcome(() => theirs.Sort())),
                    3 => (Outcome(() => mine.InsertRange(a, [value, b])), Outcome(() => theirs.InsertRange(a, [value, b]))),
                    4 => (Outcome(() => mine.AddRange(mine)), Outcome(() => theirs.AddRange(theirs))),
                    _ => (Outcome(() => mine.InsertRange(a, mine)), Outcome(() => theirs.InsertRange(a, theirs))),
                },
                7 => random.Next(5) switch
                {
                    0 => (Outcome(() => mine.IndexOf(value, a, b)), Outcome(() => theirs.IndexOf(value, a, b))),
                    1 => (Outcome(() => mine.LastIndexOf(value, a, b)), Outcome(() => theirs.LastIndexOf(value, a, b))),
                    2 => (Outcome(() => string.Join(",", mine.GetRange(a, b).ToArray())),
                        Outcome(() => string.Join(",", theirs.GetRange(a, b)))),
                    3 => (Outcome(() => mine[a]), Outcome(() => theirs[a])),
                    _ => (Outcome(() => mine.IndexOf(value, a)), Outcome(() => theirs.IndexOf(value, a))),
                },
                8 => random.Next(4) switch
                {
                    0 => (Outcome(() => mine.Capacity = a * 3), Outcome(() => theirs.Capacity = a * 3)),
                    1 => (Outcome(() => mine.TrimExcess()), Outcome(() => theirs.TrimExcess())),
                    2 => (Outcome(() => mine.EnsureCapacity(a * 4)), Outcome(() => theirs.EnsureCapacity(a * 4))),
                    _ => (Outcome(() => mine.RemoveAll(x => x % 3 == value % 3)), Outcome(() => theirs.RemoveAll(x => x % 3 == value % 3))),
                },
                _ => random.Next(8) == 0
                    ? (Outcome(() => mine.Clear()), Outcome(() => theirs.Clear()))
                    : (Outcome(() => mine[a] = value), Outcome(() => theirs[a] = value)),
            };

            Assert.Equal(right, left);
            Assert.Equal(string.Join(",", theirs), string.Join(",", Items(mine)));
            Assert.Equal(theirs.Capacity, mine.Capacity);
        }
    }

    [Fact]
    public void A_list_sorts_searches_and_compares_as_dotnets_does()
    {
        var random = new Random(7);
        double[] doubles = [.. Enumerable.Range(0, 300).Select(i => i % 17 == 0 ? double.NaN : i % 13 == 0 ? -0.0 : random.Next(-20, 20) * 0.5)];
        var mine = new Mine.List<double>(doubles);
        var theirs = new List<double>(doubles);
        mine.Sort();
        theirs.Sort();
        Assert.Equal(theirs.Select(BitConverter.DoubleToInt64Bits), Items(mine).Select(BitConverter.DoubleToInt64Bits));

        string[] words = [.. Enumerable.Range(0, 200).Select(i => i % 11 == 0 ? null! : ((char)('a' + random.Next(26))).ToString() + i % 7)];
        var myWords = new Mine.List<string>(words);
        var theirWords = new List<string>(words);
        myWords.Sort();
        theirWords.Sort(StringComparer.Ordinal);
        Assert.Equal(theirWords, Items(myWords));
        Assert.Equal(theirWords.BinarySearch("m3", StringComparer.Ordinal), myWords.BinarySearch("m3"));

        var numbers = new Mine.List<int>(Enumerable.Range(0, 500).Select(i => (i * 7919) % 211));
        var theirNumbers = new List<int>(Enumerable.Range(0, 500).Select(i => (i * 7919) % 211));
        numbers.Sort((x, y) => (x % 10).CompareTo(y % 10));
        theirNumbers.Sort((x, y) => (x % 10).CompareTo(y % 10));
        Assert.Equal(theirNumbers, Items(numbers));
        Assert.Equal(theirNumbers.FindIndex(3, 40, x => x > 100), numbers.FindIndex(3, 40, x => x > 100));
        Assert.Equal(theirNumbers.FindLastIndex(400, 50, x => x < 5), numbers.FindLastIndex(400, 50, x => x < 5));
        Assert.Equal(theirNumbers.ConvertAll(x => x * 2), Items(numbers.ConvertAll(x => x * 2)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_dictionary_does_what_dotnets_does(int seed)
    {
        var random = new Random(seed);
        var mine = new Mine.Dictionary<int, int>();
        var theirs = new Dictionary<int, int>();
        var myWords = new Mine.Dictionary<string, int>();
        var theirWords = new Dictionary<string, int>();
        for (var step = 0; step < Operations; step++)
        {
            var key = random.Next(0, 60);
            var word = "w" + key;
            var value = random.Next(100);
            var (left, right) = random.Next(10) switch
            {
                0 or 1 => (Outcome(() => mine.Add(key, value)), Outcome(() => theirs.Add(key, value))),
                2 => (Outcome(() => mine[key] = value), Outcome(() => theirs[key] = value)),
                3 => (Outcome(() => mine.TryAdd(key, value)), Outcome(() => theirs.TryAdd(key, value))),
                4 or 5 => (Outcome(() => mine.Remove(key)), Outcome(() => theirs.Remove(key))),
                6 => (Outcome(() => mine[key]), Outcome(() => theirs[key])),
                7 => (Outcome(() => mine.TryGetValue(key, out var v) + ":" + v), Outcome(() => theirs.TryGetValue(key, out var v) + ":" + v)),
                8 => random.Next(4) switch
                {
                    0 => (Outcome(() => mine.EnsureCapacity(key * 2)), Outcome(() => theirs.EnsureCapacity(key * 2))),
                    1 => (Outcome(() => mine.TrimExcess()), Outcome(() => theirs.TrimExcess())),
                    2 => (Outcome(() => mine.ContainsValue(value)), Outcome(() => theirs.ContainsValue(value))),
                    _ => random.Next(10) == 0 ? (Outcome(() => mine.Clear()), Outcome(() => theirs.Clear())) : ("", ""),
                },
                _ => random.Next(3) switch
                {
                    0 => (Outcome(() => myWords.Add(word, value)), Outcome(() => theirWords.Add(word, value))),
                    1 => (Outcome(() => myWords.Remove(word)), Outcome(() => theirWords.Remove(word))),
                    _ => (Outcome(() => myWords[word]), Outcome(() => theirWords[word])),
                },
            };

            Assert.Equal(right, left);
            Assert.Equal(string.Join(",", theirs), string.Join(",", Pairs(mine)));
            Assert.Equal(string.Join(",", theirWords), string.Join(",", Pairs(myWords)));
            Assert.Equal(string.Join(",", theirs.Keys), string.Join(",", Keys(mine)));
            Assert.Equal(theirs.EnsureCapacity(0), mine.EnsureCapacity(0));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_set_does_what_dotnets_does(int seed)
    {
        var random = new Random(seed);
        var mine = new Mine.HashSet<int>();
        var theirs = new HashSet<int>();
        for (var step = 0; step < Operations; step++)
        {
            int[] other = [.. Enumerable.Range(0, random.Next(6)).Select(_ => random.Next(40))];
            var value = random.Next(40);
            var (left, right) = random.Next(10) switch
            {
                0 or 1 or 2 => (Outcome(() => mine.Add(value)), Outcome(() => theirs.Add(value))),
                3 or 4 => (Outcome(() => mine.Remove(value)), Outcome(() => theirs.Remove(value))),
                5 => (Outcome(() => mine.UnionWith(other)), Outcome(() => theirs.UnionWith(other))),
                6 => (Outcome(() => mine.IntersectWith(other.Concat(Items(mine).Take(8)).ToList())),
                    Outcome(() => theirs.IntersectWith(other.Concat(theirs.Take(8)).ToList()))),
                7 => (Outcome(() => mine.ExceptWith(other)), Outcome(() => theirs.ExceptWith(other))),
                8 => (Outcome(() => mine.SymmetricExceptWith(other)), Outcome(() => theirs.SymmetricExceptWith(other))),
                _ => random.Next(5) switch
                {
                    0 => (Outcome(() => mine.IsSubsetOf(other)), Outcome(() => theirs.IsSubsetOf(other))),
                    1 => (Outcome(() => mine.Overlaps(other)), Outcome(() => theirs.Overlaps(other))),
                    2 => (Outcome(() => mine.SetEquals(other)), Outcome(() => theirs.SetEquals(other))),
                    3 => (Outcome(() => mine.RemoveWhere(x => x % 5 == value % 5)), Outcome(() => theirs.RemoveWhere(x => x % 5 == value % 5))),
                    _ => (Outcome(() => mine.EnsureCapacity(value)), Outcome(() => theirs.EnsureCapacity(value))),
                },
            };

            Assert.Equal(right, left);
            Assert.Equal(string.Join(",", theirs), string.Join(",", Items(mine)));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_queue_and_a_stack_do_what_dotnets_do(int seed)
    {
        var random = new Random(seed);
        var myQueue = new Mine.Queue<int>();
        var theirQueue = new Queue<int>();
        var myStack = new Mine.Stack<int>();
        var theirStack = new Stack<int>();
        for (var step = 0; step < Operations; step++)
        {
            var value = random.Next(100);
            var (left, right) = random.Next(12) switch
            {
                0 or 1 or 2 => (Outcome(() => myQueue.Enqueue(value)), Outcome(() => theirQueue.Enqueue(value))),
                3 or 4 => (Outcome(() => myQueue.Dequeue()), Outcome(() => theirQueue.Dequeue())),
                5 => (Outcome(() => myQueue.Peek() + " " + myQueue.Contains(value)), Outcome(() => theirQueue.Peek() + " " + theirQueue.Contains(value))),
                6 => random.Next(3) == 0 ? (Outcome(() => myQueue.TrimExcess()), Outcome(() => theirQueue.TrimExcess()))
                    : (Outcome(() => myQueue.EnsureCapacity(value / 4)), Outcome(() => theirQueue.EnsureCapacity(value / 4))),
                7 or 8 => (Outcome(() => myStack.Push(value)), Outcome(() => theirStack.Push(value))),
                9 or 10 => (Outcome(() => myStack.Pop()), Outcome(() => theirStack.Pop())),
                _ => random.Next(3) == 0 ? (Outcome(() => myStack.TrimExcess()), Outcome(() => theirStack.TrimExcess()))
                    : (Outcome(() => myStack.Peek() + " " + myStack.Contains(value)), Outcome(() => theirStack.Peek() + " " + theirStack.Contains(value))),
            };

            Assert.Equal(right, left);
            Assert.Equal(string.Join(",", theirQueue), string.Join(",", myQueue.ToArray()));
            Assert.Equal(string.Join(",", theirQueue), string.Join(",", Items(myQueue)));
            Assert.Equal(string.Join(",", theirStack), string.Join(",", myStack.ToArray()));
            Assert.Equal(string.Join(",", theirStack), string.Join(",", Items(myStack)));
            Assert.Equal(theirQueue.EnsureCapacity(0), myQueue.EnsureCapacity(0));
            Assert.Equal(theirStack.EnsureCapacity(0), myStack.EnsureCapacity(0));
        }
    }

    /// <summary>
    /// Which change stops an enumerator that is out, and which does not, as in .NET: each change made after the first
    /// MoveNext, then the next MoveNext's outcome.
    /// </summary>
    [Fact]
    public void A_change_stops_the_enumerators_that_dotnets_does()
    {
        (string, string) List(Action<Mine.List<int>> mine, Action<List<int>> theirs)
        {
            var my = new Mine.List<int> { 1, 2, 3 };
            var their = new List<int> { 1, 2, 3 };
            var myEnumerator = my.GetEnumerator();
            var theirEnumerator = their.GetEnumerator();
            myEnumerator.MoveNext();
            theirEnumerator.MoveNext();
            mine(my);
            theirs(their);
            return (Outcome(() => myEnumerator.MoveNext()), Outcome(() => theirEnumerator.MoveNext()));
        }

        (string, string) Map(Action<Mine.Dictionary<int, int>> mine, Action<Dictionary<int, int>> theirs)
        {
            var my = new Mine.Dictionary<int, int> { [1] = 1, [2] = 2 };
            var their = new Dictionary<int, int> { [1] = 1, [2] = 2 };
            var myEnumerator = my.GetEnumerator();
            var theirEnumerator = their.GetEnumerator();
            myEnumerator.MoveNext();
            theirEnumerator.MoveNext();
            mine(my);
            theirs(their);
            return (Outcome(() => myEnumerator.MoveNext()), Outcome(() => theirEnumerator.MoveNext()));
        }

        (string, string) Set(Action<Mine.HashSet<int>> mine, Action<HashSet<int>> theirs)
        {
            var my = new Mine.HashSet<int> { 1, 2 };
            var their = new HashSet<int> { 1, 2 };
            var myEnumerator = my.GetEnumerator();
            var theirEnumerator = their.GetEnumerator();
            myEnumerator.MoveNext();
            theirEnumerator.MoveNext();
            mine(my);
            theirs(their);
            return (Outcome(() => myEnumerator.MoveNext()), Outcome(() => theirEnumerator.MoveNext()));
        }

        (string, string) Stack(Action<Mine.Stack<int>> mine, Action<Stack<int>> theirs)
        {
            var my = new Mine.Stack<int>([1, 2]);
            var their = new Stack<int>([1, 2]);
            var myEnumerator = my.GetEnumerator();
            var theirEnumerator = their.GetEnumerator();
            myEnumerator.MoveNext();
            theirEnumerator.MoveNext();
            mine(my);
            theirs(their);
            return (Outcome(() => myEnumerator.MoveNext()), Outcome(() => theirEnumerator.MoveNext()));
        }

        (string, string) Queue(Action<Mine.Queue<int>> mine, Action<Queue<int>> theirs)
        {
            var my = new Mine.Queue<int>([1, 2]);
            var their = new Queue<int>([1, 2]);
            var myEnumerator = my.GetEnumerator();
            var theirEnumerator = their.GetEnumerator();
            myEnumerator.MoveNext();
            theirEnumerator.MoveNext();
            mine(my);
            theirs(their);
            return (Outcome(() => myEnumerator.MoveNext()), Outcome(() => theirEnumerator.MoveNext()));
        }

        (string, string)[] outcomes =
        [
            List(l => l[0] = 5, l => l[0] = 5),
            List(l => l.Capacity = 10, l => l.Capacity = 10),
            List(l => l.Sort(), l => l.Sort()),
            List(l => l.AddRange(Array.Empty<int>()), l => l.AddRange(Array.Empty<int>())),
            List(l => l.RemoveRange(0, 0), l => l.RemoveRange(0, 0)),
            List(l => l.Clear(), l => l.Clear()),
            Map(m => m[1] = 5, m => m[1] = 5),
            Map(m => m.Remove(2), m => m.Remove(2)),
            Map(m => m.Clear(), m => m.Clear()),
            Map(m => m[3] = 3, m => m[3] = 3),
            Map(m => m.EnsureCapacity(50), m => m.EnsureCapacity(50)),
            Map(m => m.TrimExcess(), m => m.TrimExcess()),
            Set(s => s.Remove(2), s => s.Remove(2)),
            Set(s => s.Add(1), s => s.Add(1)),
            Set(s => s.Add(3), s => s.Add(3)),
            Set(s => s.EnsureCapacity(50), s => s.EnsureCapacity(50)),
            Set(s => s.Clear(), s => s.Clear()),
            Stack(s => s.Push(3), s => s.Push(3)),
            Stack(s => s.TrimExcess(), s => s.TrimExcess()),
            Queue(q => q.Enqueue(3), q => q.Enqueue(3)),
            Queue(q => q.TrimExcess(), q => q.TrimExcess()),
        ];

        Assert.All(outcomes, outcome => Assert.Equal(outcome.Item2, outcome.Item1));
    }

    [Fact]
    public void The_checks_throw_dotnets_exceptions_with_its_messages()
    {
        (string, string)[] outcomes =
        [
            (Outcome(() => new Mine.List<int>(-1)), Outcome(() => new List<int>(-1))),
            (Outcome(() => new Mine.List<int>().CopyTo(0, new int[2], 0, 3)), Outcome(() => new List<int>().CopyTo(0, new int[2], 0, 3))),
            (Outcome(() => new Mine.List<int>().FindIndex(2, x => true)), Outcome(() => new List<int>().FindIndex(2, x => true))),
            (Outcome(() => new Mine.List<int>().FindLastIndex(0, x => true)), Outcome(() => new List<int>().FindLastIndex(0, x => true))),
            (Outcome(() => new Mine.List<int> { 1 }.Capacity = 0), Outcome(() => new List<int> { 1 }.Capacity = 0)),
            (Outcome(() => new Mine.List<int>(null!)), Outcome(() => new List<int>(null!))),
            (Outcome(() => new Mine.Dictionary<int, int>(-1)), Outcome(() => new Dictionary<int, int>(-1))),
            (Outcome(() => new Mine.Dictionary<string, int>()[null!] = 1), Outcome(() => new Dictionary<string, int>()[null!] = 1)),
            (Outcome(() => new Mine.Dictionary<string, int>()["x"]), Outcome(() => new Dictionary<string, int>()["x"])),
            (Outcome(() => new Mine.Dictionary<string, int> { { "a", 1 }, { "a", 2 } }), Outcome(() => new Dictionary<string, int> { { "a", 1 }, { "a", 2 } })),
            (Outcome(() => new Mine.Dictionary<int, int> { [1] = 1 }.TrimExcess(0)), Outcome(() => new Dictionary<int, int> { [1] = 1 }.TrimExcess(0))),
            (Outcome(() => new Mine.HashSet<int>(-1)), Outcome(() => new HashSet<int>(-1))),
            (Outcome(() => new Mine.HashSet<int>().UnionWith(null!)), Outcome(() => new HashSet<int>().UnionWith(null!))),
            (Outcome(() => new Mine.HashSet<int> { 1, 2 }.CopyTo(new int[1], 0, 2)), Outcome(() => new HashSet<int> { 1, 2 }.CopyTo(new int[1], 0, 2))),
            (Outcome(() => new Mine.Queue<int>(-1)), Outcome(() => new Queue<int>(-1))),
            (Outcome(() => new Mine.Queue<int>().Dequeue()), Outcome(() => new Queue<int>().Dequeue())),
            (Outcome(() => new Mine.Queue<int>([1, 2]).CopyTo(new int[1], 0)), Outcome(() => new Queue<int>([1, 2]).CopyTo(new int[1], 0))),
            (Outcome(() => new Mine.Queue<int>([1]).CopyTo(new int[1], 3)), Outcome(() => new Queue<int>([1]).CopyTo(new int[1], 3))),
            (Outcome(() => new Mine.Queue<int>([1, 2]).TrimExcess(1)), Outcome(() => new Queue<int>([1, 2]).TrimExcess(1))),
            (Outcome(() => new Mine.Stack<int>(-1)), Outcome(() => new Stack<int>(-1))),
            (Outcome(() => new Mine.Stack<int>().Pop()), Outcome(() => new Stack<int>().Pop())),
            (Outcome(() => new Mine.Stack<int>([1]).CopyTo(new int[1], 2)), Outcome(() => new Stack<int>([1]).CopyTo(new int[1], 2))),
        ];

        Assert.All(outcomes, outcome => Assert.Equal(outcome.Item2, outcome.Item1));
    }

    /// <summary>What an operation gave, or the exception it threw: its type and message.</summary>
    private static string Outcome<T>(Func<T> operation)
    {
        try
        {
            return "= " + operation();
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    private static string Outcome(Action operation) => Outcome(() =>
    {
        operation();
        return 0;
    });

    private static List<T> Items<T>(Mine.List<T> list)
    {
        var items = new List<T>();
        foreach (var item in list)
        {
            items.Add(item);
        }

        return items;
    }

    private static List<T> Items<T>(Mine.HashSet<T> set)
    {
        var items = new List<T>();
        foreach (var item in set)
        {
            items.Add(item);
        }

        return items;
    }

    private static List<T> Items<T>(Mine.Queue<T> queue)
    {
        var items = new List<T>();
        foreach (var item in queue)
        {
            items.Add(item);
        }

        return items;
    }

    private static List<T> Items<T>(Mine.Stack<T> stack)
    {
        var items = new List<T>();
        foreach (var item in stack)
        {
            items.Add(item);
        }

        return items;
    }

    private static List<string> Pairs<TKey, TValue>(Mine.Dictionary<TKey, TValue> map)
        where TKey : notnull
    {
        var pairs = new List<string>();
        foreach (var pair in map)
        {
            pairs.Add($"[{pair.Key}, {pair.Value}]");
        }

        return pairs;
    }

    private static List<TKey> Keys<TKey, TValue>(Mine.Dictionary<TKey, TValue> map)
        where TKey : notnull
    {
        var keys = new List<TKey>();
        foreach (var key in map.Keys)
        {
            keys.Add(key);
        }

        return keys;
    }
}
