using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Boxing;

/// <summary>
/// Programs that box and unbox every kind of value, compare and hash boxes, use structs through interfaces, compute with
/// nullables and print objects, each printing what it does, so that ChipSharp's output can be compared with .NET's line
/// by line (see <see cref="BoxingProgramTests"/> and <see cref="BoxingSimulatorTests"/>). The hash codes printed are those
/// .NET gives the same in every run (numbers, bools, chars, enums, a struct's own); a struct's ValueType hash code and an
/// object's are compared, not printed.
/// </summary>
internal static class BoxingPrograms
{
    /// <summary>Every kind of value boxed, printed, tested with patterns, hashed and unboxed.</summary>
    public const string Values = """
        using System;

        enum Color { Red, Green, Blue }

        enum Level : byte { Low = 1, High = 200 }

        enum Big : long { Far = 1L << 40 }

        [Flags]
        enum Access { None = 0, Read = 1, Write = 2 }

        struct Point
        {
            public int X, Y;

            public Point(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        struct Pair<T>
        {
            public T First, Second;
        }

        class Program
        {
            struct Inner
            {
                public byte B;
            }

            static object[] Values() => new object[]
            {
                true, 'q', (sbyte)-5, (byte)250, (short)-300, (ushort)60000, -70000, 4000000000u, -5000000000L,
                18000000000000000000ul, (nint)(-12), (nuint)34, 1.5f, -2.25, Color.Blue, Level.High, Big.Far,
                Access.Read | Access.Write, (Access)8, new Point(1, 2), new Pair<int> { First = 3, Second = 4 },
                new Inner { B = 9 }, "text", null, float.NaN, -0.0,
            };

            static string Describe(object o) => o switch
            {
                null => "null",
                bool b => "bool " + b,
                char c => "char " + c,
                sbyte v => "sbyte " + v,
                byte v => "byte " + v,
                short v => "short " + v,
                ushort v => "ushort " + v,
                int v => "int " + v,
                uint v => "uint " + v,
                long v => "long " + v,
                ulong v => "ulong " + v,
                nint v => "nint " + (long)v,
                nuint v => "nuint " + (ulong)v,
                float v => "float " + v,
                double v => "double " + v,
                Color c => "Color " + c,
                Level l => "Level " + l,
                Big g => "Big " + g,
                Access a => "Access " + a,
                Point p => "Point " + p.X + "," + p.Y,
                Pair<int> q => "Pair " + q.First + q.Second,
                Inner i => "Inner " + i.B,
                string s => "string " + s,
                _ => "other",
            };

            // (nint's hash code is that of a 64-bit .NET on the PC and of a 32-bit one on the Pico)
            static bool HashIsDotnets(object o) => o is not (Point or Pair<int> or Inner or string or nint or null);

            static void Main()
            {
                foreach (var value in Values())
                {
                    Console.WriteLine(Describe(value));
                    Console.WriteLine(value);
                    Console.WriteLine(value?.ToString() ?? "-");
                    if (HashIsDotnets(value))
                    {
                        Console.WriteLine(value.GetHashCode());
                    }
                }

                object green = Color.Green;
                object one = 1;
                object low = Level.Low;
                Console.WriteLine((int)green);
                Console.WriteLine((Color)one);
                Console.WriteLine((byte)low);
                Console.WriteLine((Level)(object)(byte)200);
                Console.WriteLine(green is int);
                Console.WriteLine(one is Color);
                Console.WriteLine(green is Color);
                Console.WriteLine(one is long);
                object point = new Point(5, 6);
                Console.WriteLine(((Point)point).Y);
                Point copy = (Point)point;
                copy.X = 50;
                Console.WriteLine(((Point)point).X + " " + copy.X);
                object again = copy;
                Console.WriteLine(ReferenceEquals(point, again) + " " + point.Equals(again));
                object[] numbers = new object[10];
                for (int i = 0; i < numbers.Length; i++)
                {
                    numbers[i] = i % 3 == 0 ? (object)(i * 1.5) : i % 3 == 1 ? i : (long)i * 1000000000000;
                }

                double total = 0;
                foreach (var number in numbers)
                {
                    total += number switch
                    {
                        int i => i,
                        long l => l / 1000000000000.0,
                        double d => d,
                        _ => -1,
                    };
                }

                Console.WriteLine(total);
            }
        }
        """;

    /// <summary>Equals and GetHashCode of boxes: numbers, floats, ValueType's field by field, a struct's own, object.Equals.</summary>
    public const string Equality = """
        using System;

        struct Floats
        {
            public float A;
            public double B;
        }

        struct Named
        {
            public string Name;
            public int Count;
        }

        struct Tens
        {
            public int Value;

            public override bool Equals(object other) => other is Tens tens && tens.Value % 10 == Value % 10;

            public override int GetHashCode() => Value % 10;
        }

        class Thing
        {
            public int Id;

            public override bool Equals(object other) => other is Thing thing && thing.Id == Id;

            public override int GetHashCode() => Id;
        }

        struct Outer
        {
            public Tens Tens;
            public int? Maybe;
            public Named Inner;
            public Thing Thing;
            public object Anything;
        }

        struct Empty
        {
        }

        enum Day { Sunday, Monday, Friday = 5 }

        class Program
        {
            static void Show(string what, object a, object b) =>
                Console.WriteLine(what + ": " + a.Equals(b) + " " + Equals(a, b) + " " + (a.Equals(b) ? (a.GetHashCode() == b.GetHashCode()).ToString() : "-"));

            static void Main()
            {
                Show("int", 5, 5);
                Show("int long", 5, 5L);
                Show("byte", (byte)3, (byte)3);
                Show("bool", true, true);
                Show("char", 'a', 'b');
                Show("NaN", double.NaN, double.NaN);
                Show("zeros", 0.0, -0.0);
                Show("float zeros", 0f, -0f);
                Show("float NaN", float.NaN, -float.NaN);
                Show("enum", Day.Monday, Day.Monday);
                Show("enum int", Day.Monday, 1);
                Show("floats", new Floats { A = float.NaN, B = 1 }, new Floats { A = float.NaN, B = 1 });
                Show("floats zeros", new Floats { A = -0f, B = 0 }, new Floats { A = 0f, B = -0.0 });
                Show("floats differ", new Floats { A = 1, B = 2 }, new Floats { A = 1, B = 3 });
                Show("named", new Named { Name = "a" + 1, Count = 2 }, new Named { Name = "a1", Count = 2 });
                Show("named null", new Named { Count = 2 }, new Named { Count = 2 });
                Show("named other", new Named { Name = "x" }, new Named { Name = "y" });
                Show("tens", new Tens { Value = 3 }, new Tens { Value = 13 });
                Show("empty", new Empty(), new Empty());
                var thing = new Thing { Id = 7 };
                var outer = new Outer { Tens = new Tens { Value = 4 }, Maybe = 5, Inner = new Named { Name = "n" }, Thing = thing, Anything = 3 };
                var same = new Outer { Tens = new Tens { Value = 14 }, Maybe = 5, Inner = new Named { Name = "n" }, Thing = new Thing { Id = 7 }, Anything = 3 };
                Show("outer", outer, same);
                same.Maybe = null;
                Show("outer maybe", outer, same);
                same.Maybe = 5;
                same.Anything = 3L;
                Show("outer anything", outer, same);
                same.Anything = 3;
                same.Thing = null;
                Show("outer thing", outer, same);
                Console.WriteLine(((object)new Tens { Value = 25 }).GetHashCode());
                Console.WriteLine(Equals(null, null) + " " + Equals(1, null) + " " + Equals(null, "x") + " " + Equals("x", "x"));
                object a = 1, b = 1;
                Console.WriteLine(ReferenceEquals(a, b) + " " + a.Equals(b) + " " + (a == b));
                Console.WriteLine(5.GetHashCode() + " " + (-5L).GetHashCode() + " " + true.GetHashCode() + " " + 'z'.GetHashCode());
                Console.WriteLine(((object)1.5).GetHashCode() + " " + ((object)2.5f).GetHashCode() + " " + ((object)Day.Friday).GetHashCode());
            }
        }
        """;

    /// <summary>Structs used through interfaces (boxed: a call changes the box), and the interfaces of boxed numbers and enums.</summary>
    public const string Interfaces = """
        using System;

        interface IDevice
        {
            int Tick();

            string Name { get; }
        }

        struct Counter : IDevice
        {
            public int Count;

            public int Tick() => ++Count;

            public string Name => "counter";
        }

        struct Blinker : IDevice
        {
            public bool On;

            int IDevice.Tick()
            {
                On = !On;
                return On ? 1 : 0;
            }

            public string Name => "blinker";
        }

        struct Temperature : IComparable<Temperature>, IComparable
        {
            public int Degrees;

            public int CompareTo(Temperature other) => Degrees.CompareTo(other.Degrees);

            public int CompareTo(object other) => other is Temperature t ? CompareTo(t) : 1;
        }

        enum Size { Small, Large }

        class Program
        {
            static void Run(IDevice device, int times)
            {
                for (int i = 0; i < times; i++)
                {
                    device.Tick();
                }

                Console.WriteLine(device.Name + " " + device.Tick());
            }

            static void Main()
            {
                var counter = new Counter();
                IDevice boxed = counter;
                Run(boxed, 3);
                Run(boxed, 1);
                Console.WriteLine(counter.Count + " " + ((Counter)boxed).Count);
                IDevice[] devices = { new Counter { Count = 10 }, new Blinker(), new Counter() };
                foreach (var device in devices)
                {
                    Run(device, 2);
                }

                object[] comparables = { 5, 2.5, true, 'c', Size.Large, (byte)7, new Temperature { Degrees = 20 } };
                object[] others = { 7, 2.5, false, 'a', Size.Small, (byte)9, new Temperature { Degrees = 30 } };
                for (int i = 0; i < comparables.Length; i++)
                {
                    var comparable = (IComparable)comparables[i];
                    Console.WriteLine(comparable.CompareTo(others[i]) + " " + comparable.CompareTo(comparables[i]) + " " +
                        comparable.CompareTo(null) + " " + (comparables[i] is IComparable));
                }

                IComparable<int> five = 5;
                IEquatable<int> six = 6;
                Console.WriteLine(five.CompareTo(9) + " " + six.Equals(6) + " " + six.Equals(7));
                IComparable<Temperature> warm = new Temperature { Degrees = 25 };
                Console.WriteLine(warm.CompareTo(new Temperature { Degrees = 25 }) + " " + warm.CompareTo(new Temperature()));
                object maybe = new Counter();
                Console.WriteLine((maybe is IDevice) + " " + (maybe is IComparable) + " " + (5 is IComparable) + " " + (maybe as IDevice)?.Name);
            }
        }
        """;

    /// <summary>Nullables: arithmetic, comparisons, ??, members, of enums and structs, in fields and arrays, boxed and unboxed, printed.</summary>
    public const string Nullables = """
        using System;

        enum Light { Red, Amber, Green }

        struct Reading
        {
            public int Value;

            public override string ToString() => "reading " + Value;
        }

        class Sensor
        {
            public int? Last;
            public Light? Light;
        }

        class Program
        {
            static int? Half(int value) => value % 2 == 0 ? value / 2 : null;

            static int? Add(int? a, int? b) => a + b;

            static double? Scale(double? value, double factor) => value * factor;

            static string Show(int? value) => value.HasValue ? "has " + value.Value : "none";

            static void Main()
            {
                int? none = null;
                int? four = 4;
                Console.WriteLine(Show(none) + " " + Show(four) + " " + Show(Half(10)) + " " + Show(Half(7)));
                Console.WriteLine(Show(Add(four, 3)) + " " + Show(Add(four, none)) + " " + Show(-four) + " " + Show(four * four));
                Console.WriteLine((four > 3) + " " + (none > 3) + " " + (none < 3) + " " + (four == 4) + " " + (none == null) + " " + (none != four));
                Console.WriteLine((none ?? 9) + " " + (four ?? 9) + " " + none.GetValueOrDefault() + " " + none.GetValueOrDefault(-1) + " " + four.GetValueOrDefault(-1));
                Console.WriteLine(Scale(1.5, 2) + " " + Scale(null, 2).HasValue);
                Light? light = Light.Amber;
                Light? unknown = null;
                Console.WriteLine(light + " " + unknown + "|" + (light == Light.Amber) + " " + (unknown == Light.Amber));
                Reading? reading = new Reading { Value = 3 };
                Reading? noReading = null;
                Console.WriteLine(reading + "|" + noReading + "|" + reading.Value.Value);
                var sensor = new Sensor();
                Console.WriteLine(sensor.Last.HasValue + " " + sensor.Light.HasValue);
                sensor.Last = 12;
                sensor.Light = Light.Green;
                sensor.Last += 1;
                Console.WriteLine(sensor.Last + " " + sensor.Light);
                int?[] values = { 1, null, 3, null, 5 };
                int sum = 0;
                int missing = 0;
                foreach (var value in values)
                {
                    if (value is int n)
                    {
                        sum += n;
                    }
                    else
                    {
                        missing++;
                    }
                }

                Console.WriteLine(sum + " " + missing);
                object boxedNone = none;
                object boxedFour = four;
                Console.WriteLine((boxedNone == null) + " " + boxedFour + " " + (boxedFour is int) + " " + (boxedFour is long));
                int? back = (int?)boxedFour;
                int? backNone = (int?)boxedNone;
                Console.WriteLine(back + " " + backNone.HasValue + " " + (boxedFour as int?) + " " + ("text" as object as int?).HasValue);
                Console.WriteLine(four.Equals(4) + " " + four.Equals(4L) + " " + none.Equals(null) + " " + four.Equals(null));
                Console.WriteLine(four.GetHashCode() + " " + none.GetHashCode() + " " + four.ToString() + "|" + none.ToString() + "|");
                Console.WriteLine(none);
                Console.WriteLine(four);
                Console.WriteLine(light);
                Console.WriteLine(reading);
                Console.WriteLine($"[{none}] [{four}] [{none,3}] [{four,-3}] [{four:X2}] [{light}] [{reading}]");
                string kept = $"{four}{none}|{light,6}|";
                Console.WriteLine(kept + kept.Length);
                Console.WriteLine(string.Format("{0}-{1}-{2}", none, four, light));
                bool? yes = true;
                bool? maybe = null;
                Console.WriteLine((yes & maybe) + "|" + (yes | maybe) + "|" + (maybe == true) + " " + (yes == true));
                long? big = 5000000000L;
                Console.WriteLine(big / 2 + " " + (big > int.MaxValue));
                Console.WriteLine(default(int?).HasValue);
            }
        }
        """;

    /// <summary>Objects, structs and boxed values in printed and kept text: $"...", string.Format, Console.WriteLine of a format, +.</summary>
    public const string Text = """
        using System;

        struct Point
        {
            public int X, Y;
        }

        struct Named
        {
            public int Value;

            public override string ToString() => "named " + Value;
        }

        enum Mode { Off, On }

        class Shape
        {
            public virtual string Name => "shape";

            public override string ToString() => Name;
        }

        class Circle : Shape
        {
            public override string Name => "circle";
        }

        class Plain
        {
        }

        class Program
        {
            static string Keep(object a, object b) => $"<{a}|{b,5}|{a,-4}>";

            static void Main()
            {
                object number = 42;
                object text = "text";
                object nothing = null;
                Shape shape = new Circle();
                object plain = new Plain();
                var point = new Point { X = 1, Y = 2 };
                var named = new Named { Value = 7 };
                Console.WriteLine($"{number} {text} [{nothing}] {shape} {plain} {point} {named} {Mode.On} {(object)Mode.Off}");
                Console.WriteLine(Keep(number, shape) + Keep(nothing, point) + Keep(named, 2.5));
                Console.WriteLine(string.Format("{0}", number));
                Console.WriteLine(string.Format("{0} {1}", text, point));
                Console.WriteLine(string.Format("{0} {1} {2}", shape, nothing, named));
                Console.WriteLine(string.Format("{0} {1} {2} {3} {4,6} {5:X4}", 1, 'c', true, Mode.On, shape, 255));
                Console.WriteLine("{0} and {1}", number, shape);
                Console.WriteLine("{0} {1} {2} {3}", 1, 2.5, "three", point);
                Console.WriteLine("{0}{1}{2}{3}{4}{5}", 'a', named, nothing, number, plain, Mode.Off);
                Console.WriteLine("x" + number + shape + point + named + nothing);
                Console.WriteLine(number);
                Console.WriteLine(point);
                Console.WriteLine(named);
                Console.WriteLine(shape);
                Console.Write(point);
                Console.Write(' ');
                Console.Write(number);
                Console.WriteLine();
                string kept = $"{point}{named}";
                Console.WriteLine(kept.Length);
                object[] all = { number, text, shape, point, named, Mode.On, 1.25f, null };
                foreach (var item in all)
                {
                    Console.WriteLine($"[{item,8}]");
                }
            }
        }
        """;

    /// <summary>Boxing in generic code over values: patterns, interfaces, the default comparer of a struct without IEquatable.</summary>
    public const string Generic = """
        using System;
        using System.Collections.Generic;

        interface IDevice
        {
            int Tick();
        }

        struct Counter : IDevice
        {
            public int Count;

            public int Tick() => ++Count;
        }

        struct Point
        {
            public int X, Y;
        }

        struct Tens
        {
            public int Value;

            public override bool Equals(object other) => other is Tens tens && tens.Value % 10 == Value % 10;

            public override int GetHashCode() => Value % 10;
        }

        enum Mode { Off, On }

        class Program
        {
            static string Kind<T>(T value)
            {
                if (value is int i)
                {
                    return "int " + i;
                }

                if (value is IComparable)
                {
                    return "comparable";
                }

                return value is IDevice ? "device" : "other";
            }

            static int Total<T>(T value)
            {
                object boxed = value;
                return boxed is int n ? n : boxed is long l ? (int)l : -1;
            }

            static int TickIfDevice<T>(T value) => value is IDevice device ? device.Tick() + device.Tick() : 0;

            static bool Same<T>(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);

            static int Hash<T>(T a) => EqualityComparer<T>.Default.GetHashCode(a);

            static string Text<T>(T value) => value.ToString() + "/" + value.GetHashCode().Equals(value.GetHashCode()) + "/" + value.Equals(value);

            static void Main()
            {
                Console.WriteLine(Kind(5) + ", " + Kind(5L) + ", " + Kind(new Counter()) + ", " + Kind(new Point()) + ", " + Kind(Mode.On));
                Console.WriteLine(Total(3) + " " + Total(4L) + " " + Total(2.5));
                Console.WriteLine(TickIfDevice(new Counter { Count = 5 }) + " " + TickIfDevice(1));
                Console.WriteLine(Same(new Point { X = 1 }, new Point { X = 1 }) + " " + Same(new Point { X = 1 }, new Point { Y = 1 }));
                Console.WriteLine(Same(new Tens { Value = 2 }, new Tens { Value = 32 }) + " " + Same(Mode.On, Mode.Off) + " " + Same(3, 3));
                Console.WriteLine((Hash(new Point { X = 1 }) == Hash(new Point { X = 1 })) + " " + Hash(new Tens { Value = 47 }) + " " + Hash(Mode.On));
                Console.WriteLine(Text(new Point()) + " " + Text(Mode.On) + " " + Text(new Tens { Value = 3 }) + " " + Text(7));
            }
        }
        """;

    /// <summary>
    /// Boxes made by the thousand and kept in an array of objects, a static field and a field of an object, while most die:
    /// in a small heap only collections make room, and a box one of them lost would change what it prints.
    /// </summary>
    public const string Garbage = """
        using System;

        struct Reading
        {
            public int Value;
            public string Unit;
        }

        class Log
        {
            public object Last;
        }

        class Program
        {
            static object kept;

            static void Main()
            {
                var window = new object[16];
                var log = new Log();
                long total = 0;
                for (int i = 0; i < 20000; i++)
                {
                    object box = (i % 4) switch
                    {
                        0 => i,
                        1 => (long)i * 3,
                        2 => new Reading { Value = i, Unit = i % 8 == 2 ? "mV" : "mA" },
                        _ => (double)i / 2,
                    };
                    window[i % window.Length] = box;
                    if (i % 1000 == 0)
                    {
                        kept = box;
                    }

                    if (i % 400 == 2)
                    {
                        log.Last = box;
                    }

                    total += window[(i * 7) % window.Length] switch
                    {
                        int n => n,
                        long l => l,
                        Reading r => r.Value + r.Unit.Length,
                        double d => (long)d,
                        _ => 0,
                    };
                }

                Console.WriteLine(total);
                Console.WriteLine(kept);
                Console.WriteLine(((Reading)log.Last).Value + " " + ((Reading)log.Last).Unit);
                foreach (var item in window)
                {
                    Console.WriteLine(item is Reading r ? r.Unit + r.Value : item);
                }
            }
        }
        """;

    /// <summary>Every program but <see cref="Garbage"/>, which runs in a small heap, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Values), Values),
        (nameof(Equality), Equality),
        (nameof(Interfaces), Interfaces),
        (nameof(Nullables), Nullables),
        (nameof(Text), Text),
        (nameof(Generic), Generic),
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
