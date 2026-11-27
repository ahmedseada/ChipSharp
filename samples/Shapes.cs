using System;

// Shapes: virtual methods, interfaces and type tests. An abstract Shape whose abstract and virtual members each shape
// overrides (an override that calls the base's, a sealed override), interfaces that several classes implement (one
// inheriting another, one explicitly, one through a base class, one with a default method, a generic one over int and
// the library's IComparable<int>), called through a list of objects and a static field; is, as, casts and a switch on
// types; object's Equals and GetHashCode, overridden and not. Ring.Next is virtual, but only one kind of object runs
// it: ChipSharp makes that a plain call, and leaves out every override no object of the program can run.

interface IDescribed
{
    int Code { get; }

    void Describe();

    // a default method: classes that implement IDescribed run it as it is
    void Announce()
    {
        Console.Write("announcing ");
        Describe();
    }
}

interface IScalable : IDescribed
{
    void Scale(int factor);
}

interface IMeasured<T>
{
    T Measure();
}

abstract class Shape : IDescribed
{
    private static int made;

    protected Shape(int x, int y)
    {
        X = x;
        Y = y;
        Id = ++made;
    }

    public int X { get; private set; }

    public int Y { get; private set; }

    public int Id { get; }

    public virtual int Sides => 0;

    public virtual int Code => Id * 100 + Sides;

    public abstract int Area();

    public void MoveBy(int dx, int dy)
    {
        X += dx;
        Y += dy;
    }

    public virtual void Describe() => Console.WriteLine($"shape {Id} at {X},{Y} with {Sides} sides, area {Area()}");
}

class Circle : Shape, IScalable
{
    public Circle(int x, int y, int radius) : base(x, y) => Radius = radius;

    public int Radius { get; private set; }

    public override int Area() => 3 * Radius * Radius;

    public void Scale(int factor) => Radius *= factor;

    public override void Describe()
    {
        Console.Write("circle: ");
        base.Describe();
    }
}

class Rectangle : Shape, IScalable, IMeasured<int>
{
    public Rectangle(int x, int y, int width, int height) : base(x, y)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public override int Sides => 4;

    public override int Area() => Width * Height;

    public int Measure() => 2 * (Width + Height);

    // an explicit implementation: only a call through IScalable runs it
    void IScalable.Scale(int factor)
    {
        Width *= factor;
        Height *= factor;
    }

    public override bool Equals(object other) => other is Rectangle rectangle && rectangle.Width == Width
        && rectangle.Height == Height;

    public override int GetHashCode() => Width * 31 + Height;
}

sealed class Square : Rectangle
{
    public Square(int x, int y, int side) : base(x, y, side, side)
    {
    }

    public sealed override int Code => 9000 + Id;

    public override void Describe()
    {
        Console.Write("square: ");
        base.Describe();
    }
}

class Triangle : Shape, IMeasured<int>
{
    public readonly int Base;
    public readonly int Height;

    public Triangle(int x, int y, int width, int height) : base(x, y)
    {
        Base = width;
        Height = height;
    }

    public override int Sides => 3;

    public override int Area() => Base * Height / 2;

    public int Measure() => Base + Height + Height;
}

class Thermometer : IComparable<int>
{
    public int Degrees;

    public int CompareTo(int other) => Degrees.CompareTo(other);
}

class Ring
{
    public virtual int Next(int at) => (at + 1) % 3;
}

class Faster : Ring
{
    // no Faster is ever made: this override is left out
    public override int Next(int at) => (at + 2) % 3;
}

class Entry
{
    public Shape Shape;
    public Entry Next;

    public Entry(Shape shape, Entry next)
    {
        Shape = shape;
        Next = next;
    }
}

class Program
{
    static IDescribed favourite;

    static void Main()
    {
        Entry list = null;
        list = new Entry(new Triangle(0, 0, 4, 3), list);
        list = new Entry(new Square(1, 1, 3), list);
        list = new Entry(new Rectangle(2, 0, 2, 5), list);
        list = new Entry(new Circle(5, 5, 2), list);

        var total = 0;
        for (var entry = list; entry != null; entry = entry.Next)
        {
            entry.Shape.Describe();
            total += entry.Shape.Area();
        }

        Console.WriteLine($"total area {total}");

        for (var entry = list; entry != null; entry = entry.Next)
        {
            IDescribed described = entry.Shape;
            Console.Write(described.Code);
            if (described is IScalable scalable)
            {
                scalable.Scale(2);
                Console.Write($" scaled to {entry.Shape.Area()}");
            }

            if (entry.Shape is IMeasured<int> measured)
            {
                Console.Write($" measuring {measured.Measure()}");
            }

            Console.WriteLine($" is {Kind(entry.Shape)}");
        }

        object any = list.Next.Shape;
        Console.WriteLine(any is Shape);
        Console.WriteLine(any is Square);
        Console.WriteLine(any is IScalable);
        Console.WriteLine(any as Rectangle != null);
        Console.WriteLine(any as Square == null);
        Console.WriteLine(any as IMeasured<int> != null);
        var shape = (Shape)any;
        shape.MoveBy(1, 1);
        shape.Describe();
        object nothing = null;
        Console.WriteLine(nothing is Shape);
        Console.WriteLine(nothing is null);
        Console.WriteLine((Shape)nothing == null);
        Console.WriteLine(Kind(null));

        var a = new Rectangle(0, 0, 2, 3);
        var b = new Rectangle(9, 9, 2, 3);
        var c = new Circle(0, 0, 1);
        object plain = new object();
        Console.WriteLine(a.Equals(b));
        Console.WriteLine(a.Equals(c));
        Console.WriteLine(c.Equals(c));
        Console.WriteLine(c.Equals(new Circle(0, 0, 1)));
        Console.WriteLine(a.GetHashCode() == b.GetHashCode());
        Console.WriteLine(c.GetHashCode() == c.GetHashCode());
        Console.WriteLine(plain.Equals(plain));
        Console.WriteLine(plain.Equals(a));
        Console.WriteLine($"{Same(a, b)} {Same(b, a)} {Same(plain, plain)} {Same(c, plain)}");

        IComparable<int> thermometer = new Thermometer { Degrees = 21 };
        Console.WriteLine($"{thermometer.CompareTo(18)} {thermometer.CompareTo(21)} {thermometer.CompareTo(30)}");

        var ring = new Ring();
        var at = 0;
        for (var i = 0; i < 10; i++)
        {
            at = ring.Next(at);
        }

        Console.WriteLine(at);

        favourite = list.Next.Next.Shape;
        favourite.Announce();
        favourite.Describe();
    }

    static string Kind(Shape shape) => shape switch
    {
        Square => "a square",
        Rectangle { Width: > 2 } => "a wide rectangle",
        Rectangle => "a rectangle",
        Circle { Radius: > 3 } => "a big circle",
        Circle => "a circle",
        null => "nothing",
        _ => "another shape",
    };

    static bool Same(object x, object y) => x.Equals(y);
}
