using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Classes;

/// <summary>
/// C# programs with classes, for the PC and the Pico tests: each prints what it computes, which must be what .NET prints,
/// compiled in Release and in Debug. <see cref="Survivors"/> also makes the garbage collector run many times in a small
/// heap, with a reference in every place one can live.
/// </summary>
internal static class ClassPrograms
{
    /// <summary>
    /// Constructors (overloads, this(...), field initializers, object's own), instance fields and methods, auto and
    /// manual properties, a static member of a class with objects, a nested class, an object initializer, this returned
    /// and passed, references compared (==, !=, is null, ReferenceEquals), ?. and ?? and ??=, null meeting an object
    /// where ways join, objects as object.
    /// </summary>
    public const string Basics = """
        using System;

        class Counter
        {
            public static int Made;
            private int count = 10;
            public readonly string Name;

            public Counter() : this("counter")
            {
            }

            public Counter(string name)
            {
                Name = name;
                Made++;
            }

            public Counter(string name, int step) : this(name) => Step = step;

            public int Step { get; set; } = 1;

            public int Count => count;

            public int Value
            {
                get => count;
                set => count = value < 0 ? 0 : value;
            }

            public Counter Next()
            {
                count += Step;
                return this;
            }

            public bool Same(Counter other) => this == other;

            public Reading Read() => new Reading { At = count, From = this };

            public static Counter Pick(Counter a, Counter b, bool first) => first ? a : b;

            public sealed class Reading
            {
                public int At;
                public Counter From;
            }
        }

        class Program
        {
            static Counter shared;

            static void Main()
            {
                var a = new Counter();
                var b = new Counter("b", 5);
                a.Next().Next();
                b.Next();
                Console.WriteLine($"{a.Name} {a.Count} {a.Step}");
                Console.WriteLine($"{b.Name} {b.Count} {b.Step}");
                b.Value = -4;
                Console.WriteLine(b.Value);
                b.Value = 7;
                b.Step = 2;
                b.Next();
                Console.WriteLine(b.Count);
                Console.WriteLine(Counter.Made);

                shared = Counter.Pick(a, b, false);
                Console.WriteLine(shared == b);
                Console.WriteLine(shared != a);
                Console.WriteLine(a.Same(a));
                Console.WriteLine(ReferenceEquals(a, b));

                var reading = a.Read();
                Console.WriteLine(reading.At);
                Console.WriteLine(reading.From == a);

                Counter none = null;
                Console.WriteLine(none is null);
                Console.WriteLine(none is not null);
                Console.WriteLine((none ?? a).Name);
                Console.WriteLine(none?.Read() == null);
                Console.WriteLine(a?.Read().From == a);
                Counter maybe = a.Count > 100 ? a : null;
                Console.WriteLine(maybe == null);
                maybe ??= b;
                Console.WriteLine(maybe.Name);
                Console.WriteLine(Describe(null));
                Console.WriteLine(Describe(a));
                none?.Next();
                a?.Next();
                Console.WriteLine(a.Count);

                object any = a;
                object plain = new object();
                Console.WriteLine(any == a);
                Console.WriteLine(plain != any);
            }

            static string Describe(Counter counter) => counter == null ? "none" : counter.Name;
        }
        """;

    /// <summary>
    /// A class derived from a class derived from a class: base constructors, inherited fields and methods, a private
    /// field with the same name in a base and a derived class, a method hidden with new that calls the base's, objects
    /// used through their base class.
    /// </summary>
    public const string Inheritance = """
        using System;

        class Animal
        {
            public static int Count;
            public string Name;
            protected int legs;
            private int id;

            public Animal(string name, int legs)
            {
                Name = name;
                this.legs = legs;
                id = ++Count;
            }

            public int Legs => legs;

            public int Id => id;

            public string Describe() => Name;

            public int Speed() => legs * 2;
        }

        class Dog : Animal
        {
            private int id = 100;
            public int Tricks;

            public Dog(string name) : base(name, 4)
            {
            }

            public Dog(string name, int tricks) : this(name) => Tricks = tricks;

            public int DogId => id;

            public new int Speed() => base.Speed() + Tricks;

            public void Learn()
            {
                Tricks++;
                id += 10;
            }
        }

        sealed class Puppy : Dog
        {
            public int Age;

            public Puppy(string name, int age) : base(name, 1) => Age = age;

            public int Total() => Legs + Tricks + Age + Speed();
        }

        class Program
        {
            static void Main()
            {
                var cat = new Animal("cat", 4);
                var dog = new Dog("dog", 2);
                var puppy = new Puppy("puppy", 1);
                dog.Learn();
                puppy.Learn();
                puppy.Learn();
                Animal animal = puppy;
                Dog asDog = puppy;
                Console.WriteLine($"{cat.Describe()} {cat.Legs} {cat.Id} {cat.Speed()}");
                Console.WriteLine($"{dog.Describe()} {dog.Legs} {dog.Id} {dog.DogId} {dog.Tricks} {dog.Speed()} {((Animal)dog).Speed()}");
                Console.WriteLine($"{puppy.Describe()} {puppy.Age} {puppy.DogId} {puppy.Total()} {animal.Speed()} {animal.Id}");
                Console.WriteLine(asDog.Speed());
                Console.WriteLine(Animal.Count);
                Console.WriteLine(animal == puppy);
                Console.WriteLine(Heaviest(cat, dog).Name);
            }

            static Animal Heaviest(Animal a, Animal b) => a.Legs * 10 + a.Id >= b.Legs * 10 + b.Id ? a : b;
        }
        """;

    /// <summary>
    /// What objects are built into: a linked list kept in a struct (in a static field, in an object, in an object of
    /// that object), a ring of nodes that point at each other, a binary tree built by recursion, and an array on the
    /// stack of structs holding objects.
    /// </summary>
    public const string Structures = """
        using System;

        struct Chain
        {
            public Item First;
            public Item Last;
            public int Count;

            public void Add(Item item)
            {
                if (First == null)
                {
                    First = item;
                }
                else
                {
                    Last.Next = item;
                }

                Last = item;
                Count++;
            }

            public int Sum()
            {
                int sum = 0;
                for (var item = First; item != null; item = item.Next)
                {
                    sum += item.Value;
                }

                return sum;
            }
        }

        class Item
        {
            public int Value;
            public Item Next;

            public Item(int value) => Value = value;
        }

        class Bag
        {
            public Chain Items;
            public Bag Inner;
        }

        class Ring
        {
            public int Key;
            public Ring Prev;
            public Ring Next;

            public Ring(int key)
            {
                Key = key;
                Prev = this;
                Next = this;
            }

            public void InsertAfter(Ring node)
            {
                node.Prev = this;
                node.Next = Next;
                Next.Prev = node;
                Next = node;
            }
        }

        class Tree
        {
            public int Key;
            public Tree Left;
            public Tree Right;

            public Tree(int key) => Key = key;

            public static Tree Insert(Tree tree, int key)
            {
                if (tree == null)
                {
                    return new Tree(key);
                }

                if (key < tree.Key)
                {
                    tree.Left = Insert(tree.Left, key);
                }
                else
                {
                    tree.Right = Insert(tree.Right, key);
                }

                return tree;
            }

            public static int Height(Tree tree)
            {
                if (tree == null)
                {
                    return 0;
                }

                int left = Height(tree.Left);
                int right = Height(tree.Right);
                return 1 + (left > right ? left : right);
            }

            public static long Sum(Tree tree) => tree == null ? 0 : tree.Key + Sum(tree.Left) + Sum(tree.Right);
        }

        class Program
        {
            static Chain chain;
            static Bag bag;

            static void Main()
            {
                for (int i = 1; i <= 5; i++)
                {
                    chain.Add(new Item(i * 10));
                }

                bag = new Bag { Inner = new Bag() };
                for (int i = 0; i < 3; i++)
                {
                    bag.Items.Add(new Item(i));
                }

                bag.Inner.Items.Add(new Item(42));
                Console.WriteLine($"{chain.Count} {chain.Sum()} {chain.Last.Value}");
                Console.WriteLine($"{bag.Items.Count} {bag.Items.Sum()} {bag.Inner.Items.Sum()} {bag.Inner.Inner == null}");

                var ring = new Ring(0);
                for (int i = 1; i < 6; i++)
                {
                    ring.Prev.InsertAfter(new Ring(i));
                }

                Console.Write("ring:");
                var node = ring;
                do
                {
                    Console.Write(" " + node.Key);
                    node = node.Next;
                }
                while (node != ring);
                Console.WriteLine($", back: {ring.Prev.Key} {ring.Prev.Prev.Key}");

                Tree tree = null;
                int seed = 7;
                for (int i = 0; i < 60; i++)
                {
                    seed = (seed * 1103515245 + 12345) & 0x7fffffff;
                    tree = Tree.Insert(tree, seed % 1000);
                }

                Console.WriteLine($"tree: {Tree.Height(tree)} high, {Tree.Sum(tree)} in all, {tree.Key} at the root");

                var chains = new Chain[3];
                for (int i = 0; i < 9; i++)
                {
                    chains[i % 3].Add(new Item(i));
                }

                Console.WriteLine($"{chains[0].Sum()} {chains[1].Sum()} {chains[2].Sum()} {chains[2].Last.Value}");
            }
        }
        """;

    /// <summary>
    /// Survivors and garbage: objects kept in every place a reference can live (a static field, a struct in a static
    /// field, a struct inside an object, a struct local and an array of structs on the stack across calls, a plain local,
    /// the frames of a recursion) while many short-lived objects are made. In a small heap the collector runs again and
    /// again; what the survivors hold must be intact at the end.
    /// </summary>
    public const string Survivors = """
        using System;

        struct Holder
        {
            public Node Node;
            public int Tag;
        }

        class Node
        {
            public int Value;
            public Node Next;

            public Node(int value, Node next)
            {
                Value = value;
                Next = next;
            }
        }

        class Box
        {
            public Holder Inside;
            public Box Other;
            public long Sum;
        }

        class Program
        {
            const int Rounds = 4000;

            static Node kept;
            static Holder holder;
            static Box box;

            static void Main()
            {
                kept = Chain(100, 0);
                holder.Node = Chain(50, 1000);
                holder.Tag = 3;
                box = new Box { Other = new Box { Sum = 7 } };
                box.Inside.Node = Chain(30, 2000);
                Holder local;
                local.Node = Chain(20, 3000);
                local.Tag = 1;
                var holders = new Holder[4];
                for (int i = 0; i < holders.Length; i++)
                {
                    holders[i].Node = Chain(10, 4000 + i * 100);
                }

                Node onStack = Chain(40, 5000);

                long garbage = 0;
                for (int round = 0; round < Rounds; round++)
                {
                    garbage += Sum(Chain(25, round));
                    if (round % 200 == 0)
                    {
                        garbage += Deep(30, round);
                    }
                }

                Console.WriteLine(garbage);
                Console.WriteLine(Sum(kept));
                Console.WriteLine(Sum(holder.Node) + holder.Tag);
                Console.WriteLine(Sum(box.Inside.Node) + box.Other.Sum);
                Console.WriteLine(Sum(local.Node) + local.Tag);
                for (int i = 0; i < holders.Length; i++)
                {
                    Console.WriteLine(Sum(holders[i].Node));
                }

                Console.WriteLine(Sum(onStack));
            }

            static Node Chain(int length, int start)
            {
                Node node = null;
                for (int i = 0; i < length; i++)
                {
                    node = new Node(start + i, node);
                }

                return node;
            }

            static long Sum(Node node)
            {
                long sum = 0;
                for (; node != null; node = node.Next)
                {
                    sum += node.Value;
                }

                return sum;
            }

            static long Deep(int depth, int value)
            {
                var mine = new Node(value, null);
                if (depth == 0)
                {
                    return Sum(Chain(10, value));
                }

                long below = Deep(depth - 1, value + 1);
                return below + mine.Value;
            }
        }
        """;

    /// <summary>Every program, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Basics), Basics),
        (nameof(Inheritance), Inheritance),
        (nameof(Structures), Structures),
        (nameof(Survivors), Survivors),
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
