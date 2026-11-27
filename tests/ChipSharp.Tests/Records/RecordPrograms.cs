using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Records;

/// <summary>
/// Programs of records (classes and structs, positional, derived, with members of their own), of StringBuilder and of
/// type objects (typeof and GetType), each printing what it does, so that ChipSharp's output can be compared with .NET's
/// line by line (see <see cref="RecordProgramTests"/> and <see cref="RecordSimulatorTests"/>). A record's hash code is
/// compared, not printed: .NET's mixes in its type's, which is another number in every run. A StringBuilder's capacity
/// is printed where ChipSharp's single buffer has .NET's (see src/ChipSharp.Library/StringBuilder.cs).
/// </summary>
internal static class RecordPrograms
{
    /// <summary>
    /// Positional records: ToString (members of every kind, nested records, null, an array, collections), ==, Equals,
    /// GetHashCode, with, deconstruction, nominal records with init members, generic records.
    /// </summary>
    public const string Positional = """
        using System;
        using System.Collections.Generic;

        record Point(int X, int Y)
        {
            public int Sum => X + Y;
            public Point Moved(int dx) => this with { X = X + dx };
            public static Point Origin { get; } = new(0, 0);
        }

        record Reading(string Sensor, double Value, int? Code, Point At, int[] Raw);

        record Person
        {
            public string Name { get; init; }
            public int Age { get; init; }
            public string Nick;
        }

        record Empty;

        record Pair<T>(T First, T Second);

        record Log(string Name, List<int> Values, Dictionary<string, int> Counts);

        class Program
        {
            static void Main()
            {
                var p = new Point(1, 2);
                var q = new Point(1, 2);
                var r = p with { Y = 5 };
                Console.WriteLine(p);
                Console.WriteLine(r);
                Console.WriteLine(p == q);
                Console.WriteLine(p != r);
                Console.WriteLine(p.Equals(q));
                Console.WriteLine(p.Equals((object)q));
                Console.WriteLine(p.Equals(null));
                Console.WriteLine(ReferenceEquals(p, q));
                Console.WriteLine(p.GetHashCode() == q.GetHashCode());
                Console.WriteLine(p.Sum + " " + r.Moved(10));
                var (x, y) = r;
                Console.WriteLine(x * 100 + y);
                Console.WriteLine(Point.Origin);
                Console.WriteLine(p.GetType().Name + " " + p.GetType().FullName);

                var reading = new Reading("temp", 21.5, null, p, new[] { 1, 2 });
                Console.WriteLine(reading);
                var other = reading with { Code = 7, Sensor = null };
                Console.WriteLine(other);
                Console.WriteLine(reading == other);
                Console.WriteLine(reading == (reading with { }));

                var person = new Person { Name = "Ada", Age = 36, Nick = "ada" };
                Console.WriteLine(person);
                var older = person with { Age = 37 };
                Console.WriteLine(older.Age + " " + older.Name + " " + (older == person));
                Console.WriteLine(person == new Person { Name = "Ada", Age = 36, Nick = "ada" });
                Console.WriteLine(new Empty());
                Console.WriteLine(new Empty() == new Empty());

                var pair = new Pair<int>(3, 4);
                var names = new Pair<string>("a", "b");
                Console.WriteLine(pair);
                Console.WriteLine(names);
                Console.WriteLine(names with { Second = "c" });
                Console.WriteLine(pair.GetType());
                Console.WriteLine(new Pair<Point>(p, r));
                var log = new Log("a", new List<int> { 1, 2 }, new Dictionary<string, int>());
                Console.WriteLine(log);
                Console.WriteLine(log == log with { } ? "same" : "other");
                Console.WriteLine(log.Values.GetType() + " " + log.Values.GetType().Name);

                Point none = null;
                Console.WriteLine(none == null);
                Console.WriteLine(p == none);
                Console.WriteLine(none != p);
                object boxed = p;
                Console.WriteLine(boxed.ToString());
                Console.WriteLine(boxed.Equals(q));
                Console.WriteLine($"{p} and {r}");
                string kept = "kept " + p;
                Console.WriteLine(kept.Length);
            }
        }
        """;

    /// <summary>
    /// Record classes derived from each other, abstract and sealed ones, PrintMembers and ToString of their own, Equals
    /// and GetHashCode of their own, required members, record structs, primary constructors of classes and structs, and
    /// typeof(T) in generic code.
    /// </summary>
    public const string Inheritance = """
        using System;
        using System.Text;

        abstract record Shape(string Name)
        {
            public abstract double Area { get; }
        }

        record Circle(string Name, double Radius) : Shape(Name)
        {
            public override double Area => 3 * Radius * Radius;
        }

        record Square(string Name, double Side) : Shape(Name)
        {
            public override double Area => Side * Side;
        }

        sealed record Tagged(string Name, double Side, string Tag) : Square(Name, Side)
        {
            public override string ToString() => "Tagged " + Tag;
        }

        record Base(int A)
        {
            protected virtual bool PrintMembers(StringBuilder builder)
            {
                builder.Append("A is ").Append(A);
                return true;
            }
        }

        record Derived(int A, int B) : Base(A);

        record Custom(int Id, string Label)
        {
            public virtual bool Equals(Custom other) => other is not null && Id == other.Id;
            public override int GetHashCode() => Id;
        }

        record Device(string Name)
        {
            public required int Pin { get; init; }
            public int Count { get; set; }
        }

        interface IHasId { int Id { get; } }

        record Item(int Id) : IHasId;

        record struct Vector(int X, int Y)
        {
            public int Length2 => X * X + Y * Y;
        }

        readonly record struct Money(long Cents, string Currency);

        record struct Mutable
        {
            public int Value { get; set; }
            public string Note { get; set; }
        }

        class Counter(int start)
        {
            private int _value = start;
            public int Next() => ++_value;
            public int Start => start;
        }

        struct Span2(int from, int to)
        {
            public int Width => to - from;
            public override string ToString() => $"[{from}..{to})";
        }

        class Program
        {
            static string Kind<T>(T value) => typeof(T).Name + ":" + value;

            static bool SameType<T, U>() => typeof(T) == typeof(U);

            static void Main()
            {
                Shape[] shapes = { new Circle("c", 2), new Square("s", 3), new Tagged("t", 1, "x") };
                foreach (var shape in shapes)
                {
                    Console.WriteLine(shape + " " + shape.Area + " " + shape.GetType().Name);
                }

                Shape a = new Square("same", 2);
                Shape b = new Tagged("same", 2, "tag");
                Console.WriteLine(a == b);
                Console.WriteLine(a.Equals(new Square("same", 2)));
                Shape c = a with { Name = "renamed" };
                Console.WriteLine(c + " " + c.GetType().Name);
                Shape d = b with { Name = "copied" };
                Console.WriteLine(d.GetType().Name + " " + ((Tagged)d).Tag + " " + d.Name);

                Console.WriteLine(new Base(1));
                Console.WriteLine(new Derived(1, 2));
                Console.WriteLine(new Derived(1, 2) == new Derived(1, 2));
                Console.WriteLine(new Base(1) == new Derived(1, 2));

                Console.WriteLine(new Custom(1, "a") == new Custom(1, "b"));
                Console.WriteLine(new Custom(1, "a").GetHashCode());
                Console.WriteLine(new Custom(1, "a"));

                var device = new Device("led") { Pin = 25 };
                device.Count = 3;
                Console.WriteLine(device);
                Console.WriteLine(device with { Pin = 26 });

                IHasId item = new Item(9);
                Console.WriteLine(item.Id + " " + item);

                var v = new Vector(3, 4);
                var w = v with { Y = 0 };
                Console.WriteLine(v + " " + v.Length2 + " " + w);
                Console.WriteLine(v == new Vector(3, 4));
                Console.WriteLine(v != w);
                Console.WriteLine(v.Equals(w));
                Console.WriteLine(v.GetHashCode() == new Vector(3, 4).GetHashCode());
                var (vx, vy) = v;
                Console.WriteLine(vx + vy);
                object boxed = v;
                Console.WriteLine(boxed.Equals(new Vector(3, 4)) + " " + boxed);

                var price = new Money(1250, "EUR");
                Console.WriteLine(price);
                Console.WriteLine(price == new Money(1250, "EUR"));
                Console.WriteLine(price with { Cents = 99 });

                var m = new Mutable { Value = 1, Note = "n" };
                m.Value = 2;
                Console.WriteLine(m);
                Console.WriteLine(m == new Mutable { Value = 2, Note = "n" });
                Console.WriteLine(default(Mutable));

                var counter = new Counter(10);
                counter.Next();
                Console.WriteLine(counter.Next() + " " + counter.Start);
                var span = new Span2(2, 7);
                Console.WriteLine(span + " " + span.Width);

                Console.WriteLine(Kind(5) + " " + Kind("x") + " " + Kind(v) + " " + Kind(price));
                Console.WriteLine(SameType<int, int>() + " " + SameType<int, long>() + " " + SameType<Circle, Shape>());
                Console.WriteLine(typeof(Vector).IsValueType + " " + typeof(Circle).IsValueType + " " + typeof(Shape).IsClass);
            }
        }
        """;

    /// <summary>
    /// Every member of StringBuilder: appends of every kind, growth and capacity, Length and Capacity set, Insert,
    /// Remove, Replace, the indexer, CopyTo, Equals, AppendJoin, and a report table built with Append($"...") and
    /// AppendFormat.
    /// </summary>
    public const string Builders = """
        using System;
        using System.Text;

        enum Mode { Off, On }

        class Program
        {
            static void Show(StringBuilder sb) => Console.WriteLine($"[{sb}] {sb.Length} of {sb.Capacity}");

            static void Main()
            {
                var sb = new StringBuilder();
                Show(sb);
                sb.Append("hello").Append(' ').Append('*', 3).Append(42).Append(-7L).Append(2.5).Append(1.5f).Append(true)
                    .Append((byte)200).Append((sbyte)-3).Append((short)-300).Append((ushort)65000).Append(4000000000u)
                    .Append(18000000000000000000ul).Append((object)Mode.On).Append((object)null).Append((string)null);
                Show(sb);
                sb.Append("0123456789", 2, 3).Append("xyz".ToCharArray()).Append("abcdef".ToCharArray(), 1, 2)
                    .Append("span".AsSpan()).Append(new StringBuilder("inner")).Append(new StringBuilder("0123"), 1, 2);
                Show(sb);
                sb.AppendLine().AppendLine("line").AppendJoin(", ", new[] { "a", "b", "c" }).AppendJoin('|', new object[] { 1, "x", null, 2.5 });
                Console.Write(sb.ToString());
                Console.WriteLine();
                Console.WriteLine(sb.ToString(0, 5) + "/" + sb[1] + sb[sb.Length - 1]);

                var grow = new StringBuilder(4);
                for (int i = 0; i < 40; i++)
                {
                    grow.Append((char)('a' + i % 26));
                    if (i % 8 == 7)
                    {
                        Show(grow);
                    }
                }

                grow.Length = 10;
                Console.WriteLine($"[{grow}] {grow.Length}");
                grow.Length = 13;
                Console.WriteLine(grow.ToString().Replace('\0', '_'));
                grow.Capacity = 64;
                Show(grow);
                Console.WriteLine(grow.EnsureCapacity(100) + " " + grow.MaxCapacity);
                grow.Clear();
                Console.WriteLine($"[{grow}] {grow.Length}");

                var edit = new StringBuilder("The quick brown fox", 64);
                edit.Insert(4, "very ").Insert(0, 3).Insert(edit.Length, '!').Insert(1, true);
                Console.WriteLine(edit);
                edit.Remove(0, 5).Replace("quick", "slow").Replace('o', '0').Replace("very", "so", 0, 10);
                Console.WriteLine(edit);
                edit[0] = 'T';
                edit.Replace("0", "OO");
                Console.WriteLine(edit + " " + edit.Length);
                Console.WriteLine(edit.Equals(new StringBuilder(edit.ToString())) + " " + edit.Equals(new StringBuilder("x")));
                var chars = new char[6];
                edit.CopyTo(1, chars, 0, 6);
                Console.WriteLine(new string(chars));

                var report = new StringBuilder();
                string[] names = { "pump", "valve", "fan" };
                int[] values = { 12, 7, 1500 };
                report.AppendLine("name     value  hex");
                for (int i = 0; i < names.Length; i++)
                {
                    report.Append($"{names[i],-8}{values[i],6} {values[i]:X4}").AppendLine();
                }

                report.AppendFormat("{0} rows, total {1,5}", names.Length, values[0] + values[1] + values[2]).AppendLine();
                report.AppendLine($"mode {Mode.On}, ok {true}, {names[0]}!");
                Console.Write(report);
                Console.WriteLine(report.Length);
            }
        }
        """;

    /// <summary>
    /// typeof and GetType of every kind of type, with each of the names .NET gives it, the kind it is, and ==.
    /// </summary>
    public const string Types = """
        using System;
        using System.Text;
        using System.Collections.Generic;

        namespace N { class C { public class Inner { } } struct S { } }

        class TopLevel { }
        class Box<T> { public class Cell { } }
        class Pair<A, B> { }
        struct Point { public int X; }
        enum Color { Red }
        interface IShape { }
        interface IStore<T> { }
        record Rec(int X);
        record struct RecS(int X);
        delegate int MyDelegate(int x);

        class Program
        {
            class Node { }
            enum Mode { On }
            static void Show(Type t) => Console.WriteLine($"{t} | {t.Name} | {t.FullName ?? "null"} | {t.Namespace ?? "null"} | v{(t.IsValueType ? 1 : 0)} a{(t.IsArray ? 1 : 0)} e{(t.IsEnum ? 1 : 0)} c{(t.IsClass ? 1 : 0)} i{(t.IsInterface ? 1 : 0)} p{(t.IsPrimitive ? 1 : 0)} g{(t.IsGenericType ? 1 : 0)}");
            static void Main()
            {
                Show(typeof(int)); Show(typeof(string)); Show(typeof(object)); Show(typeof(int[])); Show(typeof(int[][])); Show(typeof(string[]));
                Show(typeof(Node)); Show(typeof(TopLevel)); Show(typeof(N.C)); Show(typeof(N.C.Inner)); Show(typeof(N.S));
                Show(typeof(Box<int>)); Show(typeof(Box<string>)); Show(typeof(Box<Node>)); Show(typeof(Box<Box<int>>)); Show(typeof(Box<int>[]));
                Show(typeof(Pair<int, string>)); Show(typeof(Box<int>.Cell)); Show(typeof(Color)); Show(typeof(Mode)); Show(typeof(Point));
                Show(typeof(Rec)); Show(typeof(RecS)); Show(typeof(IShape)); Show(typeof(IStore<int>)); Show(typeof(Action)); Show(typeof(Action<int>));
                Show(typeof(Func<int, string>)); Show(typeof(int?)); Show(typeof(Point?)); Show(typeof(StringBuilder)); Show(typeof(TimeSpan));
                Show(typeof(List<int>)); Show(typeof(Type)); Show(typeof(int).GetType()); Show(typeof(MyDelegate)); Show(typeof(bool)); Show(typeof(char));
                Show(typeof(double)); Show(typeof(nint)); Show(typeof(void)); Show(typeof(Node[])); Show(typeof(Color[]));
                Show(typeof(EqualityComparer<int>)); Show(typeof(Box<N.C>)); Show(typeof(Box<int?>));
                Show(((object)5).GetType()); Show("x".GetType()); Show(new int[0].GetType()); Show(((Action)Main).GetType()); Show(((object)Color.Red).GetType());
                Show(new StringBuilder().GetType()); Show(typeof(ValueType)); Show(typeof(Enum)); Show(typeof(Array)); Show(typeof(Delegate)); Show(typeof(IComparable<int>));
                Console.WriteLine(typeof(int).GetHashCode() == typeof(int).GetHashCode());
                Console.WriteLine(typeof(int) == typeof(int)); Console.WriteLine(typeof(int).Equals(typeof(long))); Console.WriteLine(typeof(int).Equals((object)typeof(int)));
            }
        }
        """;

    /// <summary>
    /// Thousands of short-lived records, copies, builders and strings, a few kept in an array, a static field and a
    /// builder: in a small heap the collector runs again and again, and what it frees must not be what is kept.
    /// </summary>
    public const string Garbage = """
        using System;
        using System.Text;

        record Sample(int Index, string Unit, double Value);

        record struct Window(int Low, int High);

        class Program
        {
            static Sample kept;
            static StringBuilder log = new StringBuilder();

            static void Main()
            {
                var recent = new Sample[8];
                var window = new Window(0, 0);
                long checksum = 0;
                for (int i = 0; i < 6000; i++)
                {
                    var sample = new Sample(i, i % 3 == 0 ? "mV" : "mA", i / 4.0);
                    var copy = sample with { Value = sample.Value + 1 };
                    recent[i % recent.Length] = copy;
                    window = window with { High = window.High + 1 };
                    var text = new StringBuilder().Append(copy.Unit).Append(':').Append(copy.Index).ToString();
                    checksum += text.Length + copy.ToString().Length;
                    if (copy == recent[(i + 4) % recent.Length])
                    {
                        checksum += 1000;
                    }

                    if (i % 500 == 0)
                    {
                        kept = copy;
                        log.Append(i).Append(' ');
                    }
                }

                Console.WriteLine(checksum);
                Console.WriteLine(kept);
                Console.WriteLine(window);
                Console.WriteLine(log);
                foreach (var sample in recent)
                {
                    Console.WriteLine(sample);
                }
            }
        }
        """;

    /// <summary>Every program but <see cref="Garbage"/>, which runs in a small heap, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Positional), Positional),
        (nameof(Inheritance), Inheritance),
        (nameof(Builders), Builders),
        (nameof(Types), Types),
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
    public static IlAssembly Compile(string source, string name, bool debug, IEnumerable<string>? references = null) =>
        IlAssembly.Load(CSharpCompiler.Compile([source], references ?? [], name, OutputKind.ConsoleApplication,
            debug ? OptimizationLevel.Debug : OptimizationLevel.Release), name + ".dll");
}
