using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Classes;

/// <summary>
/// C# programs with virtual methods, interfaces of classes and type tests, for the PC and the Pico tests: each prints
/// what it computes, which must be what .NET prints, compiled in Release and in Debug. <see cref="Zoo"/> also makes the
/// garbage collector run many times in a small heap while virtual calls go on.
/// </summary>
internal static class VirtualPrograms
{
    /// <summary>
    /// Virtual, abstract and override methods and properties over three levels, a sealed override, base calls, a new
    /// virtual method that opens a slot of its own (and a new method that hides one), a virtual call in a constructor
    /// and in a method of the base class, calls on this, on a field, on a parameter, on a call's result and where ways
    /// join, and calls that only one class can answer.
    /// </summary>
    public const string Dispatch = """
        using System;

        abstract class Animal
        {
            protected Animal(string name)
            {
                Name = name;
                Legs = CountLegs();
            }

            public string Name;
            public int Legs;

            public abstract string Sound();

            public virtual int CountLegs() => 4;

            public virtual int Weight => 10;

            public int Describe()
            {
                Console.Write(Name);
                Console.Write(" says ");
                Console.Write(Sound());
                Console.Write(" on ");
                Console.Write(Legs);
                Console.WriteLine(" legs");
                return Weight;
            }
        }

        class Dog : Animal
        {
            public Dog() : this("dog")
            {
            }

            protected Dog(string name) : base(name)
            {
            }

            public override string Sound() => "woof";

            public override int Weight => base.Weight * 3;
        }

        class Puppy : Dog
        {
            public Puppy() : base("puppy")
            {
            }

            public override string Sound() => "yip";

            public sealed override int Weight => base.Weight / 2;
        }

        class Bird : Animal
        {
            public Bird() : base("bird")
            {
            }

            public override string Sound() => "tweet";

            public override int CountLegs() => 2;

            public new virtual int Weight => 1;
        }

        sealed class Parrot : Bird
        {
            public override int Weight => 2;

            public new string Sound() => "hello";
        }

        class Counter
        {
            public virtual int Next(int value) => value + 1;
        }

        class Doubler : Counter
        {
            public override int Next(int value) => value * 2;
        }

        class Program
        {
            static Animal pet = new Puppy();

            static void Main()
            {
                var total = 0;
                Animal[] none = null;
                Console.WriteLine(none == null);
                total += new Dog().Describe();
                total += new Puppy().Describe();
                total += new Bird().Describe();
                Bird bird = new Parrot();
                total += bird.Describe();
                Console.WriteLine(total);

                Console.WriteLine(bird.Weight);
                Console.WriteLine(((Animal)bird).Weight);
                var parrot = new Parrot();
                Console.WriteLine(parrot.Sound());
                Console.WriteLine(((Animal)parrot).Sound());
                Console.WriteLine(pet.Weight + pet.Sound());

                Counter counter = new Counter();
                var value = 1;
                for (var i = 0; i < 5; i++)
                {
                    value = counter.Next(value);
                    counter = i % 2 == 0 ? new Doubler() : new Counter();
                }

                Console.WriteLine(value);
                Console.WriteLine(Make(3).Next(5));
                Console.WriteLine(Make(4).Next(5));
                Console.WriteLine(Heaviest(new Dog(), new Puppy()).Name);
            }

            static Counter Make(int kind) => kind == 3 ? new Doubler() : new Counter();

            static Animal Heaviest(Animal a, Animal b) => a.Weight >= b.Weight ? a : b;
        }
        """;

    /// <summary>
    /// Interfaces implemented by classes: implicitly and explicitly, through a base class, re-implemented by a derived
    /// class, by a virtual method an override replaces, an interface inheriting another, a default method and a
    /// property, a generic interface over int and the library's IComparable&lt;int&gt; and IEquatable&lt;int&gt;, in
    /// locals, parameters, return values, fields and static fields.
    /// </summary>
    public const string Interfaces = """
        using System;

        interface IShape
        {
            int Area();

            int Corners { get; }

            int Score() => Area() * 10 + Corners;
        }

        interface IMovable
        {
            void Move(int by);

            int Position { get; }
        }

        interface IBody : IShape, IMovable
        {
            string Kind();
        }

        interface IStore<T>
        {
            void Put(T value);

            T Take();
        }

        class Square : IBody
        {
            private int side;
            private int at;

            public Square(int side) => this.side = side;

            public virtual int Area() => side * side;

            public int Corners => 4;

            public int Position => at;

            public void Move(int by) => at += by;

            string IBody.Kind() => "square";
        }

        class BigSquare : Square
        {
            public BigSquare() : base(10)
            {
            }

            public override int Area() => base.Area() + 1;
        }

        class Circle : IShape, IMovable
        {
            public int Position { get; private set; }

            public int Area() => 3;

            int IShape.Corners => 0;

            void IMovable.Move(int by) => Position += by * 2;

            public int Score() => 99;
        }

        class Triangle : Square, IShape
        {
            public Triangle() : base(2)
            {
            }

            int IShape.Corners => 3;
        }

        class Box : IStore<int>, IComparable<int>, IEquatable<int>
        {
            private int held;

            public void Put(int value) => held += value;

            public int Take() => held;

            public int CompareTo(int other) => held.CompareTo(other);

            public bool Equals(int other) => held == other;
        }

        class Stack : IStore<int>
        {
            private int top;
            private int count;

            public void Put(int value)
            {
                top = value;
                count++;
            }

            public int Take() => top * 100 + count;
        }

        class Program
        {
            static IShape favourite;
            static IMovable mover;

            static void Main()
            {
                IShape[] noShapes = null;
                Console.WriteLine(noShapes == null);
                favourite = new BigSquare();
                Show(new Square(3));
                Show(favourite);
                Show(new Circle());
                Show(new Triangle());

                IBody body = new Square(5);
                body.Move(3);
                body.Move(4);
                Console.WriteLine($"{body.Kind()} at {body.Position} area {body.Area()} score {body.Score()}");
                mover = new Circle();
                mover.Move(5);
                Console.WriteLine(mover.Position);
                mover = body;
                mover.Move(1);
                Console.WriteLine(mover.Position);

                Fill(new Box());
                Fill(new Stack());
                IComparable<int> comparable = new Box();
                Console.WriteLine($"{comparable.CompareTo(-1)} {comparable.CompareTo(0)} {comparable.CompareTo(1)}");
                IEquatable<int> equatable = new Box();
                Console.WriteLine(equatable.Equals(0));
                Console.WriteLine(Pick(true).Area() + Pick(false).Area());
            }

            static void Show(IShape shape) => Console.WriteLine($"{shape.Area()} {shape.Corners} {shape.Score()}");

            static void Fill(IStore<int> store)
            {
                for (var i = 1; i <= 4; i++)
                {
                    store.Put(i);
                }

                Console.WriteLine(store.Take());
            }

            static IShape Pick(bool square) => square ? new Square(4) : new Circle();
        }
        """;

    /// <summary>
    /// is, as and casts to classes, interfaces and arrays, patterns with a declaration and with properties, a switch on
    /// types, is null and is not null, null cast, an object holding each kind of thing, and a cast that turns an object
    /// back into an array.
    /// </summary>
    public const string TypeTests = """
        using System;

        interface IName
        {
            int Code();
        }

        class Fruit
        {
            public int Weight;
        }

        class Apple : Fruit, IName
        {
            public int Code() => 1;
        }

        class Pear : Fruit
        {
        }

        sealed class Lemon : Fruit, IName
        {
            public bool Sour = true;

            public int Code() => 3;
        }

        class Stone : IName
        {
            public int Code() => 9;
        }

        class Program
        {
            static object Thing(int i) => i switch
            {
                0 => new Apple { Weight = 150 },
                1 => new Pear { Weight = 200 },
                2 => new Lemon { Weight = 90 },
                3 => new Stone(),
                4 => new object(),
                5 => new int[] { 4, 5, 6 },
                6 => new Fruit[] { new Apple() },
                7 => new Lemon[2],
                _ => null,
            };

            static void Main()
            {
                for (var i = 0; i < 9; i++)
                {
                    var thing = Thing(i);
                    Console.Write($"{i}: {thing is Fruit} {thing is Apple} {thing is IName} {thing is null} ");
                    Console.Write($"{thing is not null} {thing is int[]} {thing is Fruit[]} {thing is Lemon[]} ");
                    Console.WriteLine($"{thing is object[]} {Describe(thing)}");
                    if (thing is Fruit { Weight: > 100 } heavy)
                    {
                        Console.WriteLine(heavy.Weight);
                    }

                    var named = thing as IName;
                    Console.WriteLine(named?.Code() ?? -1);
                    var fruit = thing as Fruit;
                    Console.WriteLine(fruit == null ? 0 : fruit.Weight);
                }

                var numbers = (int[])Thing(5);
                Console.WriteLine(numbers[0] + numbers[2]);
                var fruits = (Fruit[])Thing(7);
                Console.WriteLine(fruits.Length);
                var lemon = (Lemon)Thing(2);
                Console.WriteLine(lemon.Sour);
                Console.WriteLine(((IName)Thing(3)).Code());
                Console.WriteLine((Apple)Thing(8) == null);
                Console.WriteLine((IName)Thing(8) == null);
                object empty = Array.Empty<int>();
                Console.WriteLine($"{empty is int[]} {empty is Fruit} {empty is IName}");
            }

            static string Describe(object thing) => thing switch
            {
                Lemon { Sour: true } => "a sour lemon",
                Apple => "an apple",
                Fruit fruit when fruit.Weight > 100 => "a big fruit",
                Fruit => "a fruit",
                IName => "a name",
                int[] { Length: 3 } => "three numbers",
                Fruit[] => "fruits",
                null => "nothing",
                _ => "something",
            };
        }
        """;

    /// <summary>
    /// Object's Equals and GetHashCode: overridden, overridden in a base class, object's own (the same object, and the
    /// same number for it), base.Equals and base.GetHashCode, through object, through an interface and on a plain
    /// object, and on arrays, which have object's own.
    /// </summary>
    public const string ObjectMethods = """
        using System;

        interface IPoint
        {
            int X { get; }
        }

        class Point : IPoint
        {
            public Point(int x, int y)
            {
                X = x;
                Y = y;
            }

            public int X { get; }

            public int Y { get; }

            public override bool Equals(object other) => other is Point point && point.X == X && point.Y == Y;

            public override int GetHashCode() => X * 1000 + Y;
        }

        class Point3 : Point
        {
            public Point3(int x, int y, int z) : base(x, y) => Z = z;

            public int Z { get; }

            public override bool Equals(object other) => base.Equals(other) && other is Point3 point && point.Z == Z;
        }

        class Token
        {
            public int Id;

            public bool SameAs(object other) => base.Equals(other);

            public int Hash() => base.GetHashCode();
        }

        class Program
        {
            static void Main()
            {
                var a = new Point(1, 2);
                var b = new Point(1, 2);
                var c = new Point3(1, 2, 3);
                var d = new Point3(1, 2, 3);
                var t = new Token();
                var u = new Token();
                object plain = new object();
                var numbers = new int[] { 1 };
                Console.WriteLine($"{a.Equals(b)} {b.Equals(a)} {a.Equals(c)} {c.Equals(a)} {c.Equals(d)} {a.Equals(null)}");
                Console.WriteLine($"{a.GetHashCode()} {c.GetHashCode()} {a.GetHashCode() == b.GetHashCode()}");
                Console.WriteLine($"{t.Equals(t)} {t.Equals(u)} {t.SameAs(t)} {t.SameAs(u)}");
                Console.WriteLine($"{t.GetHashCode() == t.GetHashCode()} {t.Hash() == t.GetHashCode()}");
                Console.WriteLine($"{Same(a, b)} {Same(t, t)} {Same(t, u)} {Same(plain, plain)} {Same(plain, t)}");
                Console.WriteLine($"{Same(numbers, numbers)} {Same(numbers, new int[] { 1 })} {Same(c, d)}");
                Console.WriteLine($"{Hash(a)} {Hash(c)} {Hash(t) == Hash(t)} {Hash(numbers) == Hash(numbers)}");
                Console.WriteLine($"{numbers.Equals(numbers)} {numbers.Equals(new int[] { 1 })} {numbers.GetHashCode() == Hash(numbers)}");
                IPoint p = new Point(5, 6);
                Console.WriteLine($"{p.Equals(new Point(5, 6))} {p.GetHashCode()} {p.X}");
            }

            static bool Same(object x, object y) => x.Equals(y);

            static int Hash(object x) => x.GetHashCode();
        }
        """;

    /// <summary>
    /// Thousands of animals of four classes, short-lived and kept, in a list and in an interface's static field, with
    /// a virtual call, an interface call and a type test on each: in a small heap the collector runs again and again in
    /// between, and must keep every animal still in the list.
    /// </summary>
    public const string Zoo = """
        using System;

        interface IFeed
        {
            int Eat(int food);
        }

        abstract class Animal : IFeed
        {
            public Animal Next;
            public int Fed;

            public abstract int Size { get; }

            public virtual int Eat(int food)
            {
                Fed += food;
                return Size;
            }
        }

        class Cat : Animal
        {
            public override int Size => 2;
        }

        class Lion : Cat
        {
            public long Mane;

            public override int Size => 9;

            public override int Eat(int food) => base.Eat(food * 2) + 1;
        }

        class Fish : Animal
        {
            public override int Size => 1;
        }

        sealed class Shark : Fish, IFeed
        {
            int IFeed.Eat(int food) => 50;
        }

        class Program
        {
            static IFeed last;

            static void Main()
            {
                Animal kept = null;
                long total = 0;
                var lions = 0;
                for (var i = 0; i < 20000; i++)
                {
                    Animal animal = (i % 4) switch
                    {
                        0 => new Cat(),
                        1 => new Lion { Mane = i },
                        2 => new Fish(),
                        _ => new Shark(),
                    };
                    total += animal.Size;
                    IFeed feed = animal;
                    total += feed.Eat(i % 7);
                    if (animal is Lion lion)
                    {
                        lions++;
                        total += lion.Mane % 3;
                    }

                    if (i % 500 == 0)
                    {
                        animal.Next = kept;
                        kept = animal;
                    }

                    last = feed;
                }

                var count = 0;
                long fed = 0;
                for (var animal = kept; animal != null; animal = animal.Next)
                {
                    count++;
                    fed += animal.Fed + animal.Size;
                }

                Console.WriteLine(total);
                Console.WriteLine(lions);
                Console.WriteLine(count);
                Console.WriteLine(fed);
                Console.WriteLine(last.Eat(1));
            }
        }
        """;

    /// <summary>Every program, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Dispatch), Dispatch),
        (nameof(Interfaces), Interfaces),
        (nameof(TypeTests), TypeTests),
        (nameof(ObjectMethods), ObjectMethods),
        (nameof(Zoo), Zoo),
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
