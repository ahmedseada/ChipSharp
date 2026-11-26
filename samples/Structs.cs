using System;

// Structs are values: copying one copies its fields, so changing the copy leaves the original as it was. A method of a
// struct changes the struct it is called on. Enums are their whole numbers. ref and out hand a method the variable
// itself, so the method can change it.
class Program
{
    static Counter counter;

    static void Main()
    {
        var p = new Point(3, 4);
        Console.WriteLine(p.X);
        Console.WriteLine(p.LengthSquared);

        var q = p;
        q.Move(10, 20);
        Console.WriteLine(p.X);
        Console.WriteLine(q.X);
        Console.WriteLine(Add(p, q).Y);
        Console.WriteLine(default(Point).Y);

        var box = new Box(new Point(1, 2), new Point(5, 7));
        Console.WriteLine(box.Area);
        box.Corner.X = -1;
        Console.WriteLine(box.Area);

        var color = Color.Green;
        Console.WriteLine((int)color);
        color = Next(color);
        Console.WriteLine(color == Color.Blue);
        Console.WriteLine((int)Next(color));

        var access = Access.Read | Access.Write;
        Console.WriteLine((access & Access.Write) != 0);
        Console.WriteLine((access & Access.Execute) != 0);
        access ^= Access.Read | Access.Execute;
        Console.WriteLine((int)access);

        int a = 1, b = 2;
        Swap(ref a, ref b);
        Console.WriteLine(a);
        Console.WriteLine(b);
        Swap(ref p, ref q);
        Console.WriteLine(p.X);

        if (TryParseDigit(55, out var digit))
        {
            Console.WriteLine(digit);
        }

        Console.WriteLine(TryParseDigit(65, out digit));
        Console.WriteLine(digit);
        Console.WriteLine(Dot(in p, in q));

        for (int i = 1; i <= 4; i++)
        {
            Tick(i * 100);
        }

        Console.WriteLine(counter.Ticks);
        Console.WriteLine(counter.Total);
    }

    static Point Add(Point left, Point right) => new Point(left.X + right.X, left.Y + right.Y);

    static Color Next(Color color) => color == Color.Blue ? Color.Red : color + 1;

    static void Swap(ref int left, ref int right) => (left, right) = (right, left);

    static void Swap(ref Point left, ref Point right)
    {
        var old = left;
        left = right;
        right = old;
    }

    static bool TryParseDigit(int code, out int digit)
    {
        digit = code - 48;
        if (digit is < 0 or > 9)
        {
            digit = 0;
            return false;
        }

        return true;
    }

    static int Dot(in Point left, in Point right) => left.X * right.X + left.Y * right.Y;

    static void Tick(int amount)
    {
        counter.Ticks++;
        counter.Total += amount;
    }
}

struct Point
{
    public int X;
    public int Y;

    public Point(int x, int y)
    {
        X = x;
        Y = y;
    }

    public readonly int LengthSquared => X * X + Y * Y;

    public void Move(int dx, int dy)
    {
        X += dx;
        Y += dy;
    }
}

struct Box(Point corner, Point opposite)
{
    public Point Corner = corner;
    public Point Opposite = opposite;

    public readonly int Area => (Opposite.X - Corner.X) * (Opposite.Y - Corner.Y);
}

struct Counter
{
    public int Ticks;
    public long Total;
}

enum Color : byte
{
    Red,
    Green,
    Blue,
}

[Flags]
enum Access
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4,
}
