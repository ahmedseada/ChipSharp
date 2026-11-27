// Generic classes over classes and over values: a linked stack of sensor readings and of numbers, a repository
// interface with two implementations (an array that grows and a linked list), new T() through a constraint,
// EqualityComparer<T>.Default over a class with its own Equals and over int, and enough short-lived objects of generic
// classes to make the Pico's garbage collector run again and again.
using System;
using System.Collections.Generic;

class Reading
{
    public int Sensor;
    public int Value;

    public Reading()
    {
        Sensor = -1;
    }

    public Reading(int sensor, int value)
    {
        Sensor = sensor;
        Value = value;
    }

    // two readings are equal when they come from the same sensor with the same value
    public override bool Equals(object other) => other is Reading reading && reading.Sensor == Sensor && reading.Value == Value;

    public override int GetHashCode() => Sensor * 1000 + Value;
}

class Node<T>
{
    public T Value;
    public Node<T> Next;
}

// a stack of T as a linked list of nodes: one class for each T it is used with
class Stack<T>
{
    private Node<T> _top;

    public int Count { get; private set; }

    public void Push(T value)
    {
        _top = new Node<T> { Value = value, Next = _top };
        Count++;
    }

    public T Pop()
    {
        if (_top == null)
        {
            return default;
        }

        var value = _top.Value;
        _top = _top.Next;
        Count--;
        return value;
    }

    public bool Contains(T value)
    {
        var comparer = EqualityComparer<T>.Default;
        for (var node = _top; node != null; node = node.Next)
        {
            if (comparer.Equals(node.Value, value))
            {
                return true;
            }
        }

        return false;
    }
}

interface IRepository<T> where T : class
{
    void Add(T item);

    T Find(int index);

    int Count { get; }
}

class ArrayRepository<T> : IRepository<T> where T : class
{
    private T[] _items = new T[2];
    private int _count;

    public void Add(T item)
    {
        if (_count == _items.Length)
        {
            var bigger = new T[_items.Length * 2];
            for (var i = 0; i < _count; i++)
            {
                bigger[i] = _items[i];
            }

            _items = bigger;
        }

        _items[_count++] = item;
    }

    public T Find(int index) => index < _count ? _items[index] : null;

    public int Count => _count;
}

class ListRepository<T> : IRepository<T> where T : class
{
    private Node<T> _first;
    private Node<T> _last;
    private int _count;

    public void Add(T item)
    {
        var node = new Node<T> { Value = item };
        if (_last == null)
        {
            _first = node;
        }
        else
        {
            _last.Next = node;
        }

        _last = node;
        _count++;
    }

    public T Find(int index)
    {
        var node = _first;
        for (var i = 0; i < index && node != null; i++)
        {
            node = node.Next;
        }

        return node?.Value;
    }

    public int Count => _count;
}

class Program
{
    static T Make<T>() where T : new() => new T();

    static int Total(IRepository<Reading> readings)
    {
        var total = 0;
        for (var i = 0; i < readings.Count; i++)
        {
            total += readings.Find(i).Value;
        }

        return total;
    }

    static void Fill(IRepository<Reading> readings, int count)
    {
        for (var i = 0; i < count; i++)
        {
            readings.Add(new Reading(i % 3, i * i));
        }
    }

    static void Main()
    {
        var readings = new Stack<Reading>();
        var numbers = new Stack<int>();
        for (var i = 1; i <= 5; i++)
        {
            readings.Push(new Reading(i, i * 10));
            numbers.Push(i * i);
        }

        Console.WriteLine($"readings: {readings.Count}, numbers: {numbers.Count}");
        Console.WriteLine($"contains (3, 30): {readings.Contains(new Reading(3, 30))}, (3, 31): {readings.Contains(new Reading(3, 31))}");
        Console.WriteLine($"contains 16: {numbers.Contains(16)}, 17: {numbers.Contains(17)}");
        Console.WriteLine($"popped: {readings.Pop().Value} {numbers.Pop()}");

        IRepository<Reading> array = new ArrayRepository<Reading>();
        IRepository<Reading> list = new ListRepository<Reading>();
        Fill(array, 9);
        Fill(list, 9);
        Console.WriteLine($"array: {array.Count} readings, total {Total(array)}, past the end: {array.Find(20) == null}");
        Console.WriteLine($"list: {list.Count} readings, total {Total(list)}, past the end: {list.Find(20) == null}");

        Console.WriteLine($"new T(): sensor {Make<Reading>().Sensor}, {Make<int>()}");
        Console.WriteLine($"equal: {EqualityComparer<Reading>.Default.Equals(new Reading(1, 2), new Reading(1, 2))} {EqualityComparer<int>.Default.Equals(4, 5)}");

        // tens of thousands of short-lived stacks and nodes: only a garbage collector makes room for them on a chip
        long checksum = 0;
        for (var round = 0; round < 3000; round++)
        {
            var window = new Stack<Reading>();
            for (var i = 0; i < 8; i++)
            {
                window.Push(new Reading(round % 7, round + i));
            }

            while (window.Count > 0)
            {
                checksum += window.Pop().Value % 11;
            }
        }

        Console.WriteLine($"checksum: {checksum}");
        while (readings.Count > 0)
        {
            Console.Write(readings.Pop().Sensor);
        }

        Console.WriteLine();
    }
}
