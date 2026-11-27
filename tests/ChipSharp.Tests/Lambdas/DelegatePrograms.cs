using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Lambdas;

/// <summary>
/// C# programs with delegates, lambdas, closures and events, for the PC and the Pico tests: each prints what it computes,
/// which must be what .NET prints, compiled in Release and in Debug (where C# caches its lambdas differently).
/// <see cref="Churn"/> also makes the garbage collector run many times in a small heap while closures and multicast
/// delegates are in use.
/// </summary>
internal static class DelegatePrograms
{
    /// <summary>
    /// Every way C# makes a delegate: static and instance method groups, a virtual method (two overrides and one), an
    /// interface's method, an extension method closed over its object, a library method, a generic method; lambdas that
    /// capture nothing (static ones too), a variable, this, a variable of an outer scope, loop variables; local functions
    /// called directly (a struct closure) and made into delegates; the program's own delegate types, a generic one; and
    /// delegates of every kind of value in and out.
    /// </summary>
    public const string Creation = """
        using System;

        delegate int Op(int a, int b);

        delegate T Transform<T>(T value);

        delegate void Report(string what, long value);

        struct Point
        {
            public int X;
            public int Y;

            public Point(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        interface IShape
        {
            int Sides();
        }

        abstract class Shape : IShape
        {
            public abstract int Sides();

            public virtual int Corners() => Sides();
        }

        class Square : Shape
        {
            public override int Sides() => 4;
        }

        class Triangle : Shape
        {
            public override int Sides() => 3;

            public override int Corners() => 30;
        }

        class Counter
        {
            private int _count;

            public Counter(int start) => _count = start;

            public int Next() => ++_count;

            public int Add(int amount) => _count += amount;

            public Func<int> Doubler() => () => _count * 2;

            public Func<int, int> Scaler(int factor) => x => x * factor + _count;
        }

        static class Extensions
        {
            public static int Twice(this Counter counter) => counter.Next() * 2;

            public static int Size(this string text) => text.Length;
        }

        static class Program
        {
            static int Add(int a, int b) => a + b;

            static int Sub(int a, int b) => a - b;

            static T Same<T>(T value) => value;

            static long Big(long value) => value * 1000000007L;

            static int Apply(Op op, int a, int b) => op(a, b);

            static int Run(Func<int, int> f, int x) => f.Invoke(x);

            static Func<int> MakeCounter()
            {
                var count = 0;
                return () => ++count;
            }

            static Func<int, Func<int, int>> Adder() => a => b => a + b;

            static Func<T> Keep<T>(T value) => () => value;

            static Func<T, bool> Is<T>(T wanted) where T : IEquatable<T> => x => x.Equals(wanted);

            static void Main()
            {
                // method groups
                Op add = Add;
                Op sub = new Op(Sub);
                Console.WriteLine(add(2, 3));
                Console.WriteLine(Apply(sub, 10, 4));
                Console.WriteLine(Apply(Add, 7, 8));

                var counter = new Counter(10);
                Func<int> next = counter.Next;
                Func<int, int> more = counter.Add;
                Console.WriteLine(next());
                Console.WriteLine(next());
                Console.WriteLine(more(100));
                Func<int> twice = counter.Twice;
                Console.WriteLine(twice());
                Func<int> length = "hello".Size;
                Console.WriteLine(length());

                // virtual and interface methods: the override of the object it was made of
                Shape[] shapes = { new Square(), new Triangle() };
                foreach (var shape in shapes)
                {
                    Func<int> sides = shape.Sides;
                    Func<int> corners = shape.Corners;
                    IShape described = shape;
                    Func<int> through = described.Sides;
                    Console.WriteLine(sides() + corners() * 100 + through() * 10000);
                }

                // a library method, a generic one
                Action<int> print = Console.WriteLine;
                print(42);
                Action<string> say = Console.WriteLine;
                say("said");
                Func<int, int> same = Same;
                Func<long, long> big = Big;
                Transform<int> twiceOf = x => x * 2;
                Transform<string> shout = text => text + "!";
                Console.WriteLine(same(5) + twiceOf(21));
                Console.WriteLine(big(3));
                Console.WriteLine(shout("hey"));

                // lambdas that capture nothing, a static one, one that captures
                Func<int, int> square = x => x * x;
                Func<int, bool> positive = static x => x > 0;
                var offset = 7;
                Func<int, int> shifted = x => x + offset;
                offset = 8;
                Console.WriteLine(Run(square, 9));
                Console.WriteLine(positive(-1));
                Console.WriteLine(positive(1));
                Console.WriteLine(shifted(1));

                // a closure that keeps its variable after its method returned
                var first = MakeCounter();
                var second = MakeCounter();
                Console.WriteLine(first() + first() + first());
                Console.WriteLine(second());
                Console.WriteLine(Adder()(3)(4));

                // captured this
                var doubler = counter.Doubler();
                var scaler = counter.Scaler(3);
                Console.WriteLine(doubler());
                counter.Next();
                Console.WriteLine(doubler());
                Console.WriteLine(scaler(5));

                // nested scopes: a variable of the outer one and one of the inner one
                var total = 0;
                Action[] adds = new Action[3];
                for (var i = 0; i < 3; i++)
                {
                    var step = (i + 1) * 10;
                    adds[i] = () => total += step + i;
                }

                foreach (var a in adds)
                {
                    a();
                }

                Console.WriteLine(total);

                // loop variables: foreach gives each its own, a for loop one for all
                var values = new[] { 1, 2, 3 };
                var each = new Func<int>[3];
                var index = 0;
                foreach (var value in values)
                {
                    each[index++] = () => value * 10;
                }

                var shared = new Func<int>[3];
                for (var i = 0; i < 3; i++)
                {
                    shared[i] = () => i;
                }

                Console.WriteLine(each[0]() + each[1]() + each[2]());
                Console.WriteLine(shared[0]() + shared[1]() + shared[2]());

                // local functions: called directly (their variables in a struct) and made into a delegate
                var bias = 100;
                int Biased(int x) => x + bias;
                Console.WriteLine(Biased(1));
                bias = 200;
                Console.WriteLine(Biased(2));
                var scale = 3;
                int Scaled(int x) => x * scale;
                Func<int, int> scaled = Scaled;
                scale = 4;
                Console.WriteLine(scaled(5) + Scaled(1));

                // every kind of value in and out
                Func<byte, sbyte, short> narrow = (a, b) => (short)(a * b);
                Func<double, float, double> floats = (a, b) => a / b;
                Func<char, char> upper = c => (char)(c - 32);
                Func<Point, Point, Point> sum = (p, q) => new Point(p.X + q.X, p.Y + q.Y);
                Func<int, int, int, int, int, int, int, int, int> eight = (a, b, c, d, e, f, g, h) =>
                    a + b * 2 + c * 3 + d * 4 + e * 5 + f * 6 + g * 7 + h * 8;
                Report report = (what, value) => Console.WriteLine(what + " = " + value);
                Func<Func<int>> maker = () => () => 99;
                Console.WriteLine(narrow(200, -3));
                Console.WriteLine(floats(1.0, 3f));
                Console.WriteLine(upper('q'));
                var p = sum(new Point(1, 2), new Point(30, 40));
                Console.WriteLine(p.X * 100 + p.Y);
                Console.WriteLine(eight(1, 1, 1, 1, 1, 1, 1, 1));
                report("answer", 4200000000L);
                Console.WriteLine(maker()());

                // lambdas in generic methods, and generic delegates over classes and text
                Console.WriteLine(Keep(5)() + Keep("five")().Length);
                Console.WriteLine(Is(3)(3));
                Console.WriteLine(Is("x")("y"));
                Func<Counter, int> nextOf = c => c.Next();
                Func<string, string> sameText = Same<string>;
                Func<int, Counter> make = start => new Counter(start);
                Console.WriteLine(nextOf(make(41)));
                Console.WriteLine(sameText("same"));
            }
        }
        """;

    /// <summary>
    /// Multicast delegates: += and -= with .NET's order, the last occurrence removed, a run of a list removed, what is
    /// left (null, the one delegate, a list), the result of the last one called, GetInvocationList, == and Equals.
    /// </summary>
    public const string Multicast = """
        using System;

        class Box
        {
            public int Value;

            public Box(int value) => Value = value;

            public void Show() => Console.Write(Value);
        }

        static class Program
        {
            static int calls;

            static void A() => Console.Write("a");

            static void B() => Console.Write("b");

            static void C() => Console.Write("c");

            static int One() { calls++; return 1; }

            static int Two() { calls++; return 2; }

            static void Run(string name, Action action)
            {
                Console.Write(name);
                Console.Write(": ");
                if (action == null)
                {
                    Console.WriteLine("null");
                    return;
                }

                action();
                Console.Write(" (");
                Console.Write(action.GetInvocationList().Length);
                Console.WriteLine(")");
            }

            static void Main()
            {
                Action a = A, b = B, c = C;
                Run("a+b+c", a + b + c);
                Run("a+null", a + null);
                Run("null+b", null + b);
                Run("a-a", a - a);
                Run("a-b", a - b);
                Run("a+b+a-a", a + b + a - a);
                Run("a+b+c-(b+c)", a + b + c - (b + c));
                Run("a+b+c-(a+c)", a + b + c - (a + c));
                Run("a+b-(a+b)", a + b - (a + b));
                Run("a+b+c+a+b-(a+b)", a + b + c + a + b - (a + b));
                Run("(a+b)+(c+a)", (a + b) + (c + a));
                Run("a+b+c-a-b", a + b + c - a - b);
                Run("a+b-(a+b+c)", a + b - (a + b + c));
                Run("a+b-null", a + b - null);

                Action chain = null;
                chain += a;
                chain += b;
                chain += a;
                chain -= a;
                Run("chain", chain);
                chain -= b;
                chain -= a;
                Run("emptied", chain);

                // the last one's result, every one called
                Func<int> numbers = One;
                numbers += Two;
                numbers += One;
                Console.WriteLine(numbers());
                Console.WriteLine(calls);
                foreach (var single in numbers.GetInvocationList())
                {
                    Console.Write(((Func<int>)single)());
                }

                Console.WriteLine();

                // equality: the same method on the same target, lists one by one
                var box = new Box(5);
                var other = new Box(5);
                Action show = box.Show;
                Action again = box.Show;
                Action elsewhere = other.Show;
                Console.WriteLine(show == again);
                Console.WriteLine(show.Equals(again));
                Console.WriteLine(show == elsewhere);
                Console.WriteLine(show != elsewhere);
                Console.WriteLine((a + b) == (a + b));
                Console.WriteLine((a + b) == (b + a));
                Console.WriteLine((a + b).Equals(a));
                Console.WriteLine(a == null);
                Action none = null;
                Console.WriteLine(none == null);
                Console.WriteLine(null == a);
                Console.WriteLine(a.Equals(null));
                Console.WriteLine(a.Equals("a"));
                Console.WriteLine(a.GetHashCode() == ((Action)A).GetHashCode());
                Console.WriteLine((a + b).GetHashCode() == (a + b).GetHashCode());

                // a delegate as an object: its type's name, Equals and GetHashCode through object, type tests
                Console.WriteLine(a);
                Console.WriteLine(numbers.ToString());
                object boxed = show;
                Console.WriteLine(boxed.Equals(again));
                Console.WriteLine(boxed.GetHashCode() == again.GetHashCode());
                Console.WriteLine(boxed is Action);
                Console.WriteLine(boxed is Func<int>);
                Console.WriteLine(boxed as Delegate != null);
                Console.WriteLine(boxed);
                Delegate both = a + b;
                Console.WriteLine(both.GetInvocationList().Length);
                Console.WriteLine((Action)both == a + b);

                Action shows = show + elsewhere + new Action(box.Show);
                Run("shows", shows);
                shows -= again;
                Run("shows-again", shows);
            }
        }
        """;

    /// <summary>
    /// Events: a field-like event with two subscribers and an unsubscribe, a static one, an event with custom add and
    /// remove, EventHandler&lt;T&gt; with its sender, and raising with ?.Invoke when there is no subscriber.
    /// </summary>
    public const string Events = """
        using System;

        class Button
        {
            public event Action Clicked;

            public event EventHandler<int> Moved;

            public static event Action<string> Logged;

            private Action _pressed;

            public int Presses;

            public event Action Pressed
            {
                add
                {
                    Presses++;
                    _pressed += value;
                }

                remove
                {
                    Presses--;
                    _pressed -= value;
                }
            }

            public string Name;

            public Button(string name) => Name = name;

            public void Click()
            {
                Clicked?.Invoke();
                Logged?.Invoke(Name);
            }

            public void Move(int distance) => Moved?.Invoke(this, distance);

            public void Press() => _pressed?.Invoke();
        }

        class Listener
        {
            public int Heard;

            public void OnClick() => Heard++;
        }

        static class Program
        {
            static int moved;

            static void Main()
            {
                var button = new Button("ok");
                button.Click();

                var first = new Listener();
                var second = new Listener();
                button.Clicked += first.OnClick;
                button.Clicked += second.OnClick;
                button.Click();
                button.Click();
                button.Clicked -= first.OnClick;
                button.Click();
                Console.WriteLine(first.Heard);
                Console.WriteLine(second.Heard);

                Button.Logged += name => Console.WriteLine("logged " + name);
                button.Click();
                Action<string> twice = name => Console.WriteLine("again " + name);
                Button.Logged += twice;
                new Button("cancel").Click();
                Button.Logged -= twice;
                new Button("close").Click();

                button.Moved += (sender, distance) => moved += distance;
                button.Moved += (sender, distance) => Console.WriteLine(((Button)sender).Name + " moved " + distance);
                button.Move(3);
                button.Move(4);
                Console.WriteLine(moved);

                Action press = () => Console.WriteLine("pressed");
                button.Pressed += press;
                button.Pressed += press;
                button.Press();
                button.Pressed -= press;
                button.Press();
                Console.WriteLine(button.Presses);
            }
        }
        """;

    /// <summary>
    /// The methods of System.Array that take a delegate, on arrays of numbers, bools, structs and strings: what each finds,
    /// in which order it asks, and Sort with a comparison of arrays small and large, with elements it calls equal.
    /// </summary>
    public const string ArrayDelegates = """
        using System;

        class Node
        {
            public int Value;

            public Node(int value) => Value = value;
        }

        struct Item
        {
            public int Key;
            public int Id;

            public Item(int key, int id)
            {
                Key = key;
                Id = id;
            }
        }

        static class Program
        {
            static int asked;

            static bool Even(int x)
            {
                asked++;
                return x % 2 == 0;
            }

            static void Print(int[] values)
            {
                Console.Write(values.Length);
                Console.Write(":");
                foreach (var value in values)
                {
                    Console.Write(" ");
                    Console.Write(value);
                }

                Console.WriteLine();
            }

            static void Main()
            {
                int[] numbers = { 5, 8, 3, 6, 7, 2, 9, 4 };
                Console.WriteLine(Array.Exists(numbers, x => x > 8));
                Console.WriteLine(Array.Exists(numbers, x => x > 9));
                Console.WriteLine(Array.Find(numbers, Even));
                Console.WriteLine(asked);
                Console.WriteLine(Array.Find(numbers, x => x > 100));
                Console.WriteLine(Array.FindLast(numbers, Even));
                Print(Array.FindAll(numbers, Even));
                Print(Array.FindAll(numbers, x => x > 100));
                Print(Array.FindAll(numbers, x => true));
                Print(Array.FindAll(new int[0], x => true));
                Console.WriteLine(Array.FindIndex(numbers, Even));
                Console.WriteLine(Array.FindIndex(numbers, 2, Even));
                Console.WriteLine(Array.FindIndex(numbers, 2, 1, Even));
                Console.WriteLine(Array.FindIndex(numbers, 8, Even));
                Console.WriteLine(Array.FindLastIndex(numbers, Even));
                Console.WriteLine(Array.FindLastIndex(numbers, 4, Even));
                Console.WriteLine(Array.FindLastIndex(numbers, 4, 2, Even));
                Console.WriteLine(Array.FindLastIndex(new int[0], -1, Even));
                Console.WriteLine(Array.TrueForAll(numbers, x => x > 1));
                Console.WriteLine(Array.TrueForAll(numbers, Even));
                var sum = 0;
                Array.ForEach(numbers, x => sum += x);
                Console.WriteLine(sum);
                Print(Array.ConvertAll(numbers, x => x * x));
                var halves = Array.ConvertAll(numbers, x => x / 2.0);
                Console.WriteLine(halves[0] + halves[7]);
                var flags = Array.ConvertAll(numbers, x => x > 5);
                Console.WriteLine(Array.FindAll(flags, x => x).Length);
                Console.WriteLine(asked);

                // sort with a comparison: backwards, then by key with the ids of equal keys where .NET leaves them
                Array.Sort(numbers, (a, b) => b - a);
                Print(numbers);
                foreach (var size in new[] { 0, 1, 2, 3, 10, 16, 17, 40, 300 })
                {
                    var items = new Item[size];
                    var seed = 12345u;
                    for (var i = 0; i < size; i++)
                    {
                        seed = seed * 1103515245u + 12345u;
                        items[i] = new Item((int)(seed >> 16) % 7, i);
                    }

                    Array.Sort(items, (x, y) => x.Key.CompareTo(y.Key));
                    long hash = 0;
                    foreach (var item in items)
                    {
                        hash = hash * 31 + item.Key * 1000 + item.Id;
                    }

                    Console.WriteLine(hash);
                }

                // text
                string[] words = { "pear", "fig", "apple", "kiwi", "banana" };
                Array.Sort(words, (x, y) => string.CompareOrdinal(x, y));
                Console.WriteLine(string.Join(",", words));
                Array.Sort(words, (x, y) => x.Length - y.Length);
                Console.WriteLine(string.Join(",", words));
                Console.WriteLine(Array.Find(words, w => w.StartsWith("b")));
                Console.WriteLine(Array.FindAll(words, w => w.Length == 4).Length);
                Console.WriteLine(string.Join("|", Array.ConvertAll(words, w => w.ToUpperInvariant())));
                Console.WriteLine(Array.FindIndex(words, w => w == "kiwi"));

                // objects
                Node[] nodes = { new Node(3), new Node(1), new Node(2), new Node(1) };
                Array.Sort(nodes, (x, y) => x.Value - y.Value);
                Console.WriteLine(nodes[0].Value * 1000 + nodes[1].Value * 100 + nodes[2].Value * 10 + nodes[3].Value);
                Console.WriteLine(Array.Find(nodes, n => n.Value > 1).Value);
                Console.WriteLine(Array.FindLast(nodes, n => n.Value == 1) == nodes[1]);
                Console.WriteLine(Array.FindAll(nodes, n => n.Value > 1).Length);
                Console.WriteLine(Array.ConvertAll(nodes, n => n.Value)[3]);
                Console.WriteLine(Array.Find(nodes, n => n.Value > 5) == null);
            }
        }
        """;

    /// <summary>
    /// Many short-lived closures and delegates, multicast lists grown and cut, and a few kept in static fields, so that the
    /// collector runs many times in a small heap while they are in use: one that lost a closure, a target or a list would
    /// change what this prints.
    /// </summary>
    public const string Churn = """
        using System;

        class Node
        {
            public int Value;
            public Func<int> Read;
            public Node Next;

            public Node(int value, Node next)
            {
                Value = value;
                Next = next;
                Read = () => Value * 2;
            }
        }

        static class Program
        {
            static Func<int> kept;
            static Action<int> listeners;
            static long heard;

            static Func<int> Make(int seed)
            {
                var a = seed;
                var b = new int[4];
                b[seed % 4] = seed;
                return () => a + b[seed % 4];
            }

            static void Main()
            {
                long total = 0;
                Node list = null;
                for (var i = 0; i < 20000; i++)
                {
                    var made = Make(i);
                    total += made();
                    if (i % 1000 == 0)
                    {
                        kept += made;
                        list = new Node(i, list);
                        var copy = i;
                        listeners += x => heard += x + copy;
                    }

                    if (i % 3000 == 0 && listeners != null)
                    {
                        listeners(1);
                    }

                    if (i % 7000 == 0 && listeners != null)
                    {
                        listeners -= listeners.GetInvocationList().Length > 2
                            ? (Action<int>)listeners.GetInvocationList()[1]
                            : null;
                    }
                }

                Console.WriteLine(total);
                Console.WriteLine(kept());
                Console.WriteLine(kept.GetInvocationList().Length);
                Console.WriteLine(listeners.GetInvocationList().Length);
                Console.WriteLine(heard);
                long read = 0;
                for (var node = list; node != null; node = node.Next)
                {
                    read += node.Read();
                }

                Console.WriteLine(read);
            }
        }
        """;

    /// <summary>Every program, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Creation), Creation),
        (nameof(Multicast), Multicast),
        (nameof(Events), Events),
        (nameof(ArrayDelegates), ArrayDelegates),
        (nameof(Churn), Churn),
    ];

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

    /// <summary>Compiles a program in Release, or in Debug, and reads it back.</summary>
    public static IlAssembly Compile(string name, bool debug, IEnumerable<string>? references = null) =>
        Compile(Source(name), name, debug, references);

    public static IlAssembly Compile(string source, string name, bool debug, IEnumerable<string>? references = null) =>
        IlAssembly.Load(CSharpCompiler.Compile([source], references ?? [], name, OutputKind.ConsoleApplication,
            debug ? OptimizationLevel.Debug : OptimizationLevel.Release), name + ".dll");
}
