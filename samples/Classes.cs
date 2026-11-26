using System;

// Classes: objects on the heap, made with new and kept by the garbage collector while the program can reach them. A
// linked list and a binary tree of objects, constructors that call each other and their base class's, a derived class,
// a class holding a struct, objects in static fields, null, ?. and ??, and many short-lived objects: on the Pico they
// make the collector run again and again, while the list and the tree stay.

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

class Shape
{
    public string Name;
    public Point Origin;
    private readonly int sides;

    public Shape(string name, int sides) : this(name, sides, new Point(0, 0))
    {
    }

    public Shape(string name, int sides, Point origin)
    {
        Name = name;
        this.sides = sides;
        Origin = origin;
    }

    public int Sides => sides;

    public void MoveBy(int dx, int dy)
    {
        Origin.X += dx;
        Origin.Y += dy;
    }
}

sealed class Rectangle : Shape
{
    public Rectangle(int width, int height) : base("rectangle", 4)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; private set; }

    public int Area() => Width * Height;

    public void Grow(int by) => Height += by;
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

class Tree
{
    private readonly int key;
    private Tree left;
    private Tree right;

    public Tree(int key) => this.key = key;

    public void Add(int value)
    {
        if (value < key)
        {
            if (left == null)
            {
                left = new Tree(value);
            }
            else
            {
                left.Add(value);
            }
        }
        else if (right == null)
        {
            right = new Tree(value);
        }
        else
        {
            right.Add(value);
        }
    }

    public int Count() => 1 + (left == null ? 0 : left.Count()) + (right == null ? 0 : right.Count());

    public int Depth()
    {
        int toLeft = left == null ? 0 : left.Depth();
        int toRight = right == null ? 0 : right.Depth();
        return 1 + (toLeft > toRight ? toLeft : toRight);
    }

    public void Print()
    {
        left?.Print();
        Console.Write(" " + key);
        right?.Print();
    }
}

class Program
{
    static Node squares;
    static Shape current;

    static void Main()
    {
        for (int i = 1; i <= 6; i++)
        {
            squares = new Node(i * i, squares);
        }

        Console.Write("squares:");
        for (Node node = squares; node != null; node = node.Next)
        {
            Console.Write(" " + node.Value);
        }

        Console.WriteLine();

        var box = new Rectangle(3, 4);
        box.Grow(2);
        box.MoveBy(5, -1);
        current = box;
        Console.WriteLine($"{current.Name}: {current.Sides} sides, {box.Width} x {box.Height} = {box.Area()}, " +
            $"at ({current.Origin.X}, {current.Origin.Y})");

        var triangle = new Shape("triangle", 3, new Point(1, 2));
        Console.WriteLine($"{triangle.Name}: {triangle.Sides} sides, at ({triangle.Origin.X}, {triangle.Origin.Y})");
        Console.WriteLine(current == box);
        Console.WriteLine(current == triangle);

        int[] keys = { 50, 30, 70, 20, 40, 60, 80, 35, 65, 10 };
        var tree = new Tree(keys[0]);
        for (int i = 1; i < keys.Length; i++)
        {
            tree.Add(keys[i]);
        }

        Console.Write("tree:");
        tree.Print();
        Console.WriteLine($", {tree.Count()} keys, {tree.Depth()} deep");

        // 100000 nodes of 16 bytes on the Pico: its 224 KB heap fills up seven times
        long total = 0;
        for (int round = 0; round < 5000; round++)
        {
            Node chain = null;
            for (int i = 0; i < 20; i++)
            {
                chain = new Node(round + i, chain);
            }

            total += Sum(chain);
        }

        Console.WriteLine($"100000 nodes made, their sum {total}");
        Console.WriteLine($"the squares still sum to {Sum(squares)}, the tree still has {tree.Count()} keys");

        Node none = null;
        Node second = squares?.Next;
        Console.WriteLine(none == null);
        Console.WriteLine((none ?? squares).Value);
        Console.WriteLine(second.Value);
        Console.WriteLine(Last(squares).Value);
    }

    static long Sum(Node list)
    {
        long sum = 0;
        for (; list != null; list = list.Next)
        {
            sum += list.Value;
        }

        return sum;
    }

    static Node Last(Node list) => list.Next == null ? list : Last(list.Next);
}
