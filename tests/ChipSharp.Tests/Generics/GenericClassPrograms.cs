using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Generics;

/// <summary>
/// C# programs with generics over classes, for the PC and the Pico tests: each prints what it computes, which must be
/// what .NET prints, compiled in Release and in Debug (whose IL for constrained calls, default(T) and new T() differs).
/// <see cref="Garbage"/> also makes the garbage collector run many times in a small heap.
/// </summary>
internal static class GenericClassPrograms
{
    /// <summary>
    /// Generic classes over classes and over int: a box, a linked list and a stack of nodes of T, a class nested in a
    /// generic class, inheritance among generic classes (Derived&lt;T&gt; : Base&lt;T&gt;, IntBox : Base&lt;int&gt;) with
    /// virtual methods, static fields of each instance, new T[n] that grows, default(T), and casts, is and as to T.
    /// </summary>
    public const string Containers = """
        using System;

        class Lane
        {
            public int Number;
            public Lane(int number) => Number = number;
        }

        class Box<T>
        {
            public T Value;
            public static int Made;
            public Box(T value) { Value = value; Made++; }
            public T Get() => Value;
            public T Swap(T other) { var old = Value; Value = other; return old; }
        }

        class Node<T>
        {
            public T Value;
            public Node<T> Next;
        }

        class Stack<T>
        {
            Node<T> top;
            public int Count;

            public void Push(T value)
            {
                top = new Node<T> { Value = value, Next = top };
                Count++;
            }

            public T Pop()
            {
                if (top == null)
                {
                    return default;
                }

                var value = top.Value;
                top = top.Next;
                Count--;
                return value;
            }
        }

        class Growing<T>
        {
            T[] items = new T[1];
            public int Count;

            public void Add(T item)
            {
                if (Count == items.Length)
                {
                    var bigger = new T[items.Length * 2];
                    for (var i = 0; i < Count; i++)
                    {
                        bigger[i] = items[i];
                    }

                    items = bigger;
                }

                items[Count++] = item;
            }

            public T this[int index] => items[index];

            public int Capacity => items.Length;
        }

        class Holder<T>
        {
            public class Cell
            {
                public T Item;
                public int Depth;
            }

            public Cell Make(T item, int depth) => new Cell { Item = item, Depth = depth };
        }

        class Base<T>
        {
            public T Item;
            public virtual int Weight() => 1;
            public static int Count;
            public Base() => Count++;
        }

        class Derived<T> : Base<T>
        {
            public override int Weight() => 2 + base.Weight();
        }

        class IntBox : Base<int>
        {
            public override int Weight() => 10 + Item;
        }

        class Program
        {
            static T Cast<T>(object value) => (T)value;

            static T As<T>(object value) where T : class => value as T;

            static bool Is<T>(object value) => value is T;

            static bool IsNull<T>(T value) => value == null;

            static T OrDefault<T>(bool give, T value) => give ? value : default;

            static bool IsLane<T>() => typeof(T) == typeof(Lane);

            static void Main()
            {
                var number = new Box<int>(5);
                var lane = new Box<Lane>(new Lane(3));
                Console.WriteLine(number.Get() + lane.Get().Number);
                Console.WriteLine(lane.Swap(new Lane(4)).Number * 10 + lane.Value.Number);
                Console.WriteLine($"{Box<int>.Made} {Box<Lane>.Made} {Box<object>.Made}");

                var lanes = new Stack<Lane>();
                for (var i = 0; i < 5; i++)
                {
                    lanes.Push(new Lane(i));
                }

                var numbers = new Stack<int>();
                numbers.Push(40);
                numbers.Push(2);
                Console.WriteLine(numbers.Pop() + numbers.Pop() + numbers.Pop());
                var total = 0;
                while (lanes.Count > 0)
                {
                    total = total * 10 + lanes.Pop().Number;
                }

                Console.WriteLine(total);
                Console.WriteLine(lanes.Pop() == null);

                var growing = new Growing<Lane>();
                var longs = new Growing<long>();
                for (var i = 0; i < 9; i++)
                {
                    growing.Add(i % 3 == 0 ? null : new Lane(i));
                    longs.Add(i * 1000000000L);
                }

                Console.WriteLine($"{growing.Count} {growing.Capacity} {growing[4].Number} {growing[3] == null} {longs[8]}");

                var cell = new Holder<Lane>().Make(new Lane(7), 2);
                Console.WriteLine(cell.Item.Number + cell.Depth);

                Base<Lane> shown = new Derived<Lane>();
                Base<int> counted = new IntBox { Item = 5 };
                Console.WriteLine($"{shown.Weight()} {new Base<Lane>().Weight()} {counted.Weight()}");
                Console.WriteLine($"{Base<Lane>.Count} {Base<int>.Count} {Base<string>.Count}");

                object thing = new Lane(8);
                Console.WriteLine(Cast<Lane>(thing).Number);
                Console.WriteLine($"{As<Lane>(thing) != null} {As<Box<Lane>>(thing) == null}");
                Console.WriteLine($"{Is<Lane>(thing)} {Is<Box<Lane>>(thing)} {Is<object>(thing)} {Is<int[]>(new int[1])}");
                Console.WriteLine($"{IsNull<Lane>(null)} {IsNull(lane)} {IsNull(3)}");
                Console.WriteLine($"{OrDefault(false, new Lane(1)) == null} {OrDefault(true, new Lane(9)).Number} {OrDefault(false, 7)}");
                Console.WriteLine($"{IsLane<Lane>()} {IsLane<Box<Lane>>()}");
            }
        }
        """;

    /// <summary>
    /// Constraints: where T : class (box and callvirt), where T : Base (a virtual call through the constraint), an
    /// interface constraint on classes (a constrained call on the object), where T : new() on a class, a struct with a
    /// constructor of its own, a struct without one and a number, where T : unmanaged, and an interface's generic
    /// method called through a constraint.
    /// </summary>
    public const string Constraints = """
        using System;

        interface IShape
        {
            int Area();
        }

        abstract class Animal
        {
            public abstract int Legs { get; }
            public virtual int Weight => 10;
        }

        class Dog : Animal
        {
            public override int Legs => 4;
        }

        class Bird : Animal
        {
            public override int Legs => 2;
            public override int Weight => 1;
        }

        class Square : IShape
        {
            public int Side = 2;
            public int Area() => Side * Side;
        }

        class Circle : IShape
        {
            public int Radius;
            public Circle() => Radius = 3;
            public int Area() => 3 * Radius * Radius;
        }

        struct Counter
        {
            public int Count;
            public Counter() => Count = 42;
        }

        struct Plain
        {
            public int Value;
        }

        class Program
        {
            static int LegsOf<T>(T animal) where T : Animal => animal.Legs * 100 + animal.Weight;

            static int Area<T>(T shape) where T : IShape => shape.Area();

            static int Total<T>(T[] shapes) where T : IShape
            {
                var total = 0;
                foreach (var shape in shapes)
                {
                    total += shape.Area();
                }

                return total;
            }

            static int HashOf<T>(T value) where T : class => value == null ? -1 : value is Animal ? 1 : 0;

            static T Make<T>() where T : new() => new T();

            static int Size<T>(T value) where T : unmanaged => System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

            static void Main()
            {
                Console.WriteLine(LegsOf(new Dog()) + LegsOf<Animal>(new Bird()));
                Console.WriteLine(Area(new Square()) + Area<IShape>(new Circle()));
                Console.WriteLine(Total(new IShape[] { new Square(), new Circle() }) + Total(new[] { new Square() }));
                Console.WriteLine($"{HashOf<Dog>(null)} {HashOf(new Bird())} {HashOf(new Square())}");
                Console.WriteLine($"{Make<Circle>().Radius} {Make<Square>().Side} {Make<Counter>().Count} {Make<Plain>().Value} {Make<long>()}");
                Console.WriteLine($"{Size(1)} {Size(2L)} {Size((byte)3)}");
            }
        }
        """;

    /// <summary>
    /// Generic interfaces over classes: a repository interface with two implementations (an array that grows and a
    /// linked list), called through the interface; generic virtual methods of a class (overridden twice, calling base)
    /// and of an interface (implicitly and explicitly), a generic method of a generic class; variance (out and in, of the
    /// program's interfaces and of IComparer&lt;in T&gt;) in calls, is and casts.
    /// </summary>
    public const string Interfaces = """
        using System;
        using System.Collections.Generic;

        class Item
        {
            public int Id;
            public Item(int id) => Id = id;
        }

        interface IRepository<T> where T : class
        {
            void Add(T item);
            T Find(int index);
            int Count { get; }
        }

        class ArrayRepository<T> : IRepository<T> where T : class
        {
            T[] items = new T[2];
            int count;

            public void Add(T item)
            {
                if (count == items.Length)
                {
                    var bigger = new T[items.Length * 2];
                    for (var i = 0; i < count; i++)
                    {
                        bigger[i] = items[i];
                    }

                    items = bigger;
                }

                items[count++] = item;
            }

            public T Find(int index) => index < count ? items[index] : null;

            public int Count => count;
        }

        class ListRepository<T> : IRepository<T> where T : class
        {
            class Cell
            {
                public T Item;
                public Cell Next;
            }

            Cell first;
            int count;

            public void Add(T item)
            {
                var cell = new Cell { Item = item };
                if (first == null)
                {
                    first = cell;
                }
                else
                {
                    var last = first;
                    while (last.Next != null)
                    {
                        last = last.Next;
                    }

                    last.Next = cell;
                }

                count++;
            }

            public T Find(int index)
            {
                var cell = first;
                for (var i = 0; i < index && cell != null; i++)
                {
                    cell = cell.Next;
                }

                return cell?.Item;
            }

            public int Count => count;
        }

        interface IVisitor
        {
            int Visit<T>(T item);
        }

        abstract class Printer
        {
            public abstract int Print<T>(T item);
            public virtual int Twice<T>(T item) => Print(item) + Print(item);
        }

        class Counting : Printer
        {
            public int Count;

            public override int Print<T>(T item)
            {
                Count++;
                return item is Item found ? found.Id : 1;
            }
        }

        class Doubling : Counting
        {
            public override int Print<T>(T item) => 2 * base.Print(item);
            public override int Twice<T>(T item) => 100 + base.Twice(item);
        }

        class Sizer : IVisitor
        {
            public int Visit<T>(T item) => item is Item ? 10 : 20;
        }

        class Hidden : IVisitor
        {
            int IVisitor.Visit<T>(T item) => 30;
        }

        class Pair<T>
        {
            public T First;
            public U Pick<U>(U a, U b) => First == null ? b : a;
        }

        interface IProducer<out T>
        {
            T Produce();
        }

        interface IConsumer<in T>
        {
            int Take(T item);
        }

        class Special : Item
        {
            public Special() : base(99)
            {
            }
        }

        class SpecialMaker : IProducer<Special>
        {
            public Special Produce() => new Special();
        }

        class Taker : IConsumer<Item>
        {
            public int Take(Item item) => item.Id + 1;
        }

        class ById : IComparer<Item>
        {
            public int Compare(Item x, Item y) => x.Id - y.Id;
        }

        class Program
        {
            static int Use(IRepository<Item> repository)
            {
                for (var i = 1; i <= 5; i++)
                {
                    repository.Add(new Item(i * i));
                }

                var total = 0;
                for (var i = 0; i < repository.Count; i++)
                {
                    total += repository.Find(i).Id;
                }

                return total + (repository.Find(10) == null ? 1000 : 0);
            }

            static int Run(Printer printer) =>
                printer.Print(new Item(5)) + printer.Print(7) + printer.Twice(new Item(3)) + printer.Twice(true);

            static int Visit(IVisitor visitor) => visitor.Visit(new Item(1)) + visitor.Visit(2L);

            static int Produce(IProducer<Item> producer) => producer.Produce().Id;

            static void Main()
            {
                Console.WriteLine(Use(new ArrayRepository<Item>()));
                Console.WriteLine(Use(new ListRepository<Item>()));
                var counting = new Counting();
                Console.WriteLine($"{Run(counting)} {counting.Count}");
                var doubling = new Doubling();
                Console.WriteLine($"{Run(doubling)} {doubling.Count}");
                Console.WriteLine($"{Visit(new Sizer())} {Visit(new Hidden())}");
                var pair = new Pair<Item> { First = new Item(1) };
                Console.WriteLine($"{pair.Pick(3, 4)} {new Pair<Item>().Pick(3, 4)} {pair.Pick(new Item(5), null).Id}");

                Console.WriteLine(Produce(new SpecialMaker()));
                IConsumer<Special> consumer = new Taker();
                Console.WriteLine(consumer.Take(new Special()));
                object maker = new SpecialMaker();
                Console.WriteLine($"{maker is IProducer<Item>} {maker is IProducer<object>} {maker is IProducer<Taker>}");
                IComparer<Special> comparer = new ById();
                Console.WriteLine(comparer.Compare(new Special(), new Special()));
                Console.WriteLine(((IProducer<object>)maker).Produce() is Special);
            }
        }
        """;

    /// <summary>
    /// The default comparers: EqualityComparer&lt;T&gt;.Default over numbers (NaN equals NaN), an enum, a struct and a
    /// class that implement IEquatable&lt;T&gt;, a class with its own Equals(object) and one without; their hash codes;
    /// Comparer&lt;T&gt;.Default over numbers and IComparable&lt;T&gt; types, null first; a comparer kept in a field and
    /// called through IEqualityComparer&lt;T&gt;; a collection of the program's that finds its elements with it, and
    /// is looped over with foreach through a struct enumerator (no try/finally).
    /// </summary>
    public const string Comparers = """
        using System;
        using System.Collections.Generic;

        enum Color : byte { Red, Green, Blue }

        struct Point : IEquatable<Point>, IComparable<Point>
        {
            public int X, Y;
            public Point(int x, int y) { X = x; Y = y; }
            public bool Equals(Point other) => X == other.X && Y == other.Y;
            public override int GetHashCode() => X * 31 + Y;
            public int CompareTo(Point other) => (X + Y).CompareTo(other.X + other.Y);
        }

        class Name
        {
            public int Id;
            public Name(int id) => Id = id;
            public override bool Equals(object other) => other is Name name && name.Id == Id;
            public override int GetHashCode() => Id;
        }

        class Key : IEquatable<Key>, IComparable<Key>
        {
            public int Id;
            public Key(int id) => Id = id;
            public bool Equals(Key other) => other != null && other.Id % 10 == Id % 10;
            public override bool Equals(object other) => false;
            public override int GetHashCode() => Id % 10;
            public int CompareTo(Key other) => Id - other.Id;
        }

        class Plain
        {
        }

        class Bag<T>
        {
            readonly IEqualityComparer<T> comparer;
            T[] items = new T[2];
            int count;

            public Bag(IEqualityComparer<T> comparer) => this.comparer = comparer;

            public Bag() : this(EqualityComparer<T>.Default)
            {
            }

            public void Add(T item)
            {
                if (count == items.Length)
                {
                    var bigger = new T[count * 2];
                    for (var i = 0; i < count; i++)
                    {
                        bigger[i] = items[i];
                    }

                    items = bigger;
                }

                items[count++] = item;
            }

            public int IndexOf(T item)
            {
                for (var i = 0; i < count; i++)
                {
                    if (comparer.Equals(items[i], item))
                    {
                        return i;
                    }
                }

                return -1;
            }

            public Enumerator GetEnumerator() => new Enumerator(this);

            public struct Enumerator
            {
                readonly Bag<T> bag;
                int index;

                public Enumerator(Bag<T> bag)
                {
                    this.bag = bag;
                    index = -1;
                }

                public bool MoveNext() => ++index < bag.count;

                public T Current => bag.items[index];
            }
        }

        class Tens : IEqualityComparer<int>
        {
            public bool Equals(int x, int y) => x / 10 == y / 10;
            public int GetHashCode(int value) => value / 10;
        }

        class Program
        {
            static bool Same<T>(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);

            static int Hash<T>(T a) => EqualityComparer<T>.Default.GetHashCode(a);

            static int Compare<T>(T a, T b) => Comparer<T>.Default.Compare(a, b);

            static void Main()
            {
                Console.WriteLine($"{Same(1, 1)} {Same(1, 2)} {Same(1.5, 1.5)} {Same(double.NaN, double.NaN)} {Same(0.0f, -0.0f)}");
                Console.WriteLine($"{Same(Color.Red, Color.Red)} {Same(Color.Red, Color.Blue)} {Same(true, false)}");
                Console.WriteLine($"{Same(new Point(1, 2), new Point(1, 2))} {Same(new Point(1, 2), new Point(2, 1))}");
                Console.WriteLine($"{Same(new Name(1), new Name(1))} {Same(new Name(1), new Name(2))} {Same(new Name(1), null)} {Same<Name>(null, null)} {Same<Name>(null, new Name(1))}");
                Console.WriteLine($"{Same(new Key(1), new Key(11))} {Same(new Key(1), new Key(2))}");
                var plain = new Plain();
                Console.WriteLine($"{Same(plain, plain)} {Same(plain, new Plain())}");
                Console.WriteLine($"{Hash(5)} {Hash(new Point(1, 2))} {Hash(new Name(7))} {Hash<Name>(null)} {Hash(-3L)} {Hash(new Key(23))} {Hash(Color.Blue)}");
                Console.WriteLine($"{Compare(1, 2)} {Compare(5, 5)} {Compare((byte)200, (byte)3)} {Compare(2.5, 1.0)} {Compare(new Point(1, 1), new Point(0, 5))}");
                Console.WriteLine($"{Compare(new Key(5), new Key(2))} {Compare(null, new Key(1))} {Compare(new Key(1), null)} {Compare<Key>(null, null)}");
                Console.WriteLine($"{EqualityComparer<int>.Default.Equals(3, 3)} {Comparer<long>.Default.Compare(4, 3)}");
                IEqualityComparer<Name> names = EqualityComparer<Name>.Default;
                IComparer<int> ints = Comparer<int>.Default;
                Console.WriteLine($"{names.Equals(new Name(4), new Name(4))} {names.GetHashCode(new Name(6))} {ints.Compare(4, 9)}");

                var bag = new Bag<Name>();
                for (var i = 0; i < 6; i++)
                {
                    bag.Add(new Name(i * 10));
                }

                Console.WriteLine($"{bag.IndexOf(new Name(30))} {bag.IndexOf(new Name(31))}");
                var total = 0;
                foreach (var name in bag)
                {
                    total += name.Id;
                }

                Console.WriteLine(total);
                var tens = new Bag<int>(new Tens());
                tens.Add(12);
                tens.Add(25);
                Console.WriteLine($"{tens.IndexOf(29)} {tens.IndexOf(31)} {new Bag<Point>().IndexOf(new Point())}");
            }
        }
        """;

    /// <summary>
    /// The library's generic code over the program's types: MemoryExtensions' IndexOf, Contains and SequenceEqual over
    /// spans of a struct of the program's that implements IEquatable&lt;T&gt;, and Array.Sort and BinarySearch of an array
    /// of one that implements IComparable&lt;T&gt; (.NET's introsort, so equal elements end up where .NET puts them).
    /// </summary>
    public const string Library = """
        using System;

        struct Point : IEquatable<Point>
        {
            public int X, Y;
            public Point(int x, int y) { X = x; Y = y; }
            public bool Equals(Point other) => X == other.X && Y == other.Y;
        }

        struct Rank : IComparable<Rank>
        {
            public int Value, Order;
            public Rank(int value, int order) { Value = value; Order = order; }
            public int CompareTo(Rank other) => Value.CompareTo(other.Value);
        }

        class Program
        {
            static void Main()
            {
                var ranks = new Rank[20];
                for (var i = 0; i < ranks.Length; i++)
                {
                    ranks[i] = new Rank(i * 7 % 5, i);
                }

                Array.Sort(ranks);
                foreach (var rank in ranks)
                {
                    Console.Write($"{rank.Value}:{rank.Order} ");
                }

                Console.WriteLine();
                Console.WriteLine($"{Array.BinarySearch(ranks, new Rank(4, 0)) >= 16} {Array.BinarySearch(ranks, new Rank(9, 0))}");
                Span<Point> points = stackalloc Point[3];
                points[0] = new Point(1, 2);
                points[1] = new Point(3, 4);
                points[2] = new Point(5, 6);
                Console.WriteLine($"{points.IndexOf(new Point(3, 4))} {points.IndexOf(new Point(3, 5))}");
                Console.WriteLine($"{points.Contains(new Point(5, 6))} {points.LastIndexOf(new Point(1, 2))}");
                ReadOnlySpan<Point> same = new[] { new Point(1, 2), new Point(3, 4), new Point(5, 6) };
                Console.WriteLine(((ReadOnlySpan<Point>)points).SequenceEqual(same));
            }
        }
        """;

    /// <summary>
    /// Some 60000 short-lived objects of generic classes over classes and over values (boxes, nodes of a
    /// stack, arrays of T that grow), with a few kept in a stack and a static field of an instance: in a small heap the
    /// collector runs again and again in between, and must keep every one still in use.
    /// </summary>
    public const string Garbage = """
        using System;

        class Reading
        {
            public int Value;
            public Reading(int value) => Value = value;
        }

        class Node<T>
        {
            public T Value;
            public Node<T> Next;
        }

        class Stack<T>
        {
            Node<T> top;
            public int Count;
            public void Push(T value) { top = new Node<T> { Value = value, Next = top }; Count++; }
            public T Peek() => top.Value;
            public T Pop() { var value = top.Value; top = top.Next; Count--; return value; }
        }

        class Box<T>
        {
            public T Value;
            public static Box<T> Last;
            public Box(T value) { Value = value; Last = this; }
        }

        class Window<T>
        {
            T[] items = new T[2];
            int count;

            public void Add(T item)
            {
                if (count == items.Length)
                {
                    var bigger = new T[count * 2];
                    for (var i = 0; i < count; i++)
                    {
                        bigger[i] = items[i];
                    }

                    items = bigger;
                }

                items[count++] = item;
            }

            public T this[int index] => items[index];
        }

        class Program
        {
            static void Main()
            {
                var kept = new Stack<Reading>();
                var sums = new Stack<long>();
                long total = 0;
                for (var i = 0; i < 5000; i++)
                {
                    var box = new Box<Reading>(new Reading(i));
                    var number = new Box<int>(i % 7);
                    var window = new Window<Reading>();
                    for (var j = 0; j < 5; j++)
                    {
                        window.Add(new Reading(i + j));
                    }

                    total += box.Value.Value % 13 + number.Value + window[4].Value % 3;
                    if (i % 250 == 0)
                    {
                        kept.Push(new Reading(i));
                        sums.Push(total);
                    }
                }

                var count = 0;
                long values = 0;
                while (kept.Count > 0)
                {
                    count++;
                    values += kept.Pop().Value + sums.Pop() % 1000;
                }

                Console.WriteLine(total);
                Console.WriteLine(count);
                Console.WriteLine(values);
                Console.WriteLine(Box<Reading>.Last.Value.Value + Box<int>.Last.Value);
            }
        }
        """;

    /// <summary>Every program, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Containers), Containers),
        (nameof(Constraints), Constraints),
        (nameof(Interfaces), Interfaces),
        (nameof(Comparers), Comparers),
        (nameof(Library), Library),
        (nameof(Garbage), Garbage),
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
