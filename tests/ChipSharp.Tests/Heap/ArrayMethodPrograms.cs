namespace ChipSharp.Tests.Heap;

/// <summary>
/// C# programs that use the methods of System.Array (see ChipSharp.Frontend's ArrayMethods), for the PC and the Pico:
/// each prints what .NET prints, every overload with its edge cases (empty arrays, the whole array, nothing at the end,
/// overlapping copies), of every element type arrays hold.
/// </summary>
internal static class ArrayMethodPrograms
{
    /// <summary>The types and printing the programs share.</summary>
    private const string Shared = """
        enum Color : byte { Red, Green, Blue }

        enum Big : long { Low = -5, High = 1L << 40 }

        struct Point
        {
            public int X;
            public short Y;
            public bool On;

            public Point(int x, short y, bool on) { X = x; Y = y; On = on; }
        }

        class Node
        {
            public int Value;
            public Node Next;

            public Node(int value) { Value = value; }
        }

        static class Show
        {
            public static void Line(int[] a) { foreach (var x in a) { Console.Write(x); Console.Write(" "); } Console.WriteLine(a.Length); }
            public static void Line(byte[] a) { foreach (var x in a) { Console.Write(x); Console.Write(" "); } Console.WriteLine(a.Length); }
            public static void Line(long[] a) { foreach (var x in a) { Console.Write(x); Console.Write(" "); } Console.WriteLine(a.Length); }
            public static void Line(float[] a) { foreach (var x in a) { Console.Write(x); Console.Write(" "); } Console.WriteLine(a.Length); }
            public static void Line(double[] a) { foreach (var x in a) { Console.Write(x); Console.Write(" "); } Console.WriteLine(a.Length); }
            public static void Line(bool[] a) { foreach (var x in a) { Console.Write(x); Console.Write(" "); } Console.WriteLine(a.Length); }
            public static void Line(Color[] a) { foreach (var x in a) { Console.Write((int)x); Console.Write(" "); } Console.WriteLine(a.Length); }
            public static void Line(Point[] a)
            {
                foreach (var p in a) { Console.Write($"{p.X},{p.Y},{p.On} "); }
                Console.WriteLine(a.Length);
            }

            public static void Line(Node[] a)
            {
                foreach (var n in a) { Console.Write(n == null ? -1 : n.Value); Console.Write(" "); }
                Console.WriteLine(a.Length);
            }
        }
        """;

    /// <summary>Copy (every overload), ConstrainedCopy, CopyTo and Clear, of numbers, bools, enums, structs and references.</summary>
    public const string Copies = "using System;\n\n" + Shared + """

        class Program
        {
            static void Main()
            {
                int[] source = { 1, 2, 3, 4, 5, 6, 7, 8 };
                int[] target = new int[8];
                Array.Copy(source, target, 8);
                Show.Line(target);
                Array.Copy(source, target, 0);
                Array.Copy(source, 8, target, 8, 0);
                Array.Copy(source, 2, target, 5, 3);
                Show.Line(target);

                // overlapping, both ways, as memmove does
                Array.Copy(target, 0, target, 2, 6);
                Show.Line(target);
                Array.Copy(target, 3, target, 0, 5);
                Show.Line(target);
                Array.ConstrainedCopy(source, 1, target, 1, 2);
                Show.Line(target);

                // the long overloads
                long[] longs = { 10, 20, 30, 40 };
                long[] wide = new long[6];
                Array.Copy(longs, wide, 4L);
                Array.Copy(longs, 1L, wide, 4L, 2L);
                Show.Line(wide);
                byte[] bytes = { 1, 2, 3, 4, 5, 6, 7 };
                Array.Copy(bytes, 0L, bytes, 1L, 6L);
                Show.Line(bytes);

                // CopyTo
                int[] small = { 9, 8 };
                small.CopyTo(target, 6);
                Show.Line(target);
                small.CopyTo(target, 0L);
                new int[0].CopyTo(target, 8);
                Show.Line(target);

                // Clear: all, a range, nothing
                Array.Clear(target, 2, 3);
                Show.Line(target);
                Array.Clear(target, 8, 0);
                Array.Clear(target);
                Show.Line(target);
                Array.Clear(new int[0]);

                // other elements
                bool[] flags = { true, false, true, true };
                bool[] moreFlags = new bool[5];
                Array.Copy(flags, 0, moreFlags, 1, 4);
                Show.Line(moreFlags);
                Array.Clear(moreFlags, 3, 2);
                Show.Line(moreFlags);
                Color[] colors = { Color.Blue, Color.Green, Color.Red };
                Color[] colorCopy = new Color[3];
                colors.CopyTo(colorCopy, 0);
                Array.Copy(colorCopy, 0, colorCopy, 1, 2);
                Show.Line(colorCopy);
                Big[] bigs = { Big.High, Big.Low };
                Big[] bigCopy = new Big[2];
                Array.Copy(bigs, bigCopy, 2);
                Console.WriteLine((long)bigCopy[0] + (long)bigCopy[1]);
                Point[] points = { new Point(1, -2, true), new Point(3, 4, false), new Point(-5, 6, true) };
                Point[] pointCopy = new Point[4];
                Array.Copy(points, 0, pointCopy, 1, 3);
                Show.Line(pointCopy);
                Array.Clear(pointCopy, 1, 1);
                Show.Line(pointCopy);
                float[] floats = { 1.5f, -0f, float.NaN };
                float[] floatCopy = new float[3];
                Array.Copy(floats, floatCopy, 3);
                Show.Line(floatCopy);
                double[] doubles = { double.Epsilon, -1e300 };
                double[] doubleCopy = new double[2];
                doubles.CopyTo(doubleCopy, 0);
                Show.Line(doubleCopy);
                nint[] natives = { -1, 2 };
                nint[] nativeCopy = new nint[2];
                Array.Copy(natives, nativeCopy, 2);
                Console.WriteLine(nativeCopy[0] + nativeCopy[1]);

                // references: objects, arrays of arrays, null
                Node[] nodes = { new Node(1), null, new Node(3) };
                Node[] nodeCopy = new Node[4];
                Array.Copy(nodes, 0, nodeCopy, 1, 3);
                Show.Line(nodeCopy);
                Array.Copy(nodeCopy, 1, nodeCopy, 0, 3);
                Show.Line(nodeCopy);
                Array.Clear(nodeCopy, 0, 2);
                Show.Line(nodeCopy);
                int[][] rows = { new[] { 1, 2 }, new[] { 3 }, null };
                int[][] rowCopy = new int[3][];
                rows.CopyTo(rowCopy, 0);
                Console.WriteLine(rowCopy[0] == rows[0]);
                Console.WriteLine(rowCopy[1][0]);
                Console.WriteLine(rowCopy[2] == null);
            }
        }
        """;

    /// <summary>Fill, IndexOf, LastIndexOf and Reverse, every overload, of every element type they take.</summary>
    public const string Searches = "using System;\n\n" + Shared + """

        class Program
        {
            static void Main()
            {
                int[] values = { 4, 7, 4, 1, 7, 9, 4 };
                Console.WriteLine(Array.IndexOf(values, 4));
                Console.WriteLine(Array.IndexOf(values, 4, 1));
                Console.WriteLine(Array.IndexOf(values, 4, 3, 3));
                Console.WriteLine(Array.IndexOf(values, 4, 7));
                Console.WriteLine(Array.IndexOf(values, 4, 7, 0));
                Console.WriteLine(Array.IndexOf(values, 5));
                Console.WriteLine(Array.LastIndexOf(values, 7));
                Console.WriteLine(Array.LastIndexOf(values, 7, 3));
                Console.WriteLine(Array.LastIndexOf(values, 4, 5, 3));
                Console.WriteLine(Array.LastIndexOf(values, 4, 5, 0));
                Console.WriteLine(Array.LastIndexOf(values, 4, 0));
                Console.WriteLine(Array.LastIndexOf(values, 8));
                int[] empty = new int[0];
                Console.WriteLine(Array.IndexOf(empty, 1));
                Console.WriteLine(Array.IndexOf(empty, 1, 0, 0));
                Console.WriteLine(Array.LastIndexOf(empty, 1));
                Console.WriteLine(Array.LastIndexOf(empty, 1, -1));
                Console.WriteLine(Array.LastIndexOf(empty, 1, 0, 0));
                Console.WriteLine(Array.LastIndexOf(empty, 1, -1, 0));

                Array.Reverse(values);
                Show.Line(values);
                Array.Reverse(values, 1, 4);
                Show.Line(values);
                Array.Reverse(values, 7, 0);
                Array.Reverse(values, 2, 1);
                Array.Reverse(empty);
                Show.Line(values);
                Array.Fill(values, 3);
                Show.Line(values);
                Array.Fill(values, -1, 2, 3);
                Array.Fill(values, 5, 7, 0);
                Show.Line(values);

                // NaN equals NaN and 0 equals -0, as their Equals say
                float[] floats = { 1f, -0f, float.NaN, 0f, float.NaN };
                Console.WriteLine(Array.IndexOf(floats, float.NaN));
                Console.WriteLine(Array.LastIndexOf(floats, float.NaN));
                Console.WriteLine(Array.IndexOf(floats, 0f));
                Console.WriteLine(Array.LastIndexOf(floats, -0f));
                double[] doubles = { double.NaN, 2.5, -0.0 };
                Console.WriteLine(Array.IndexOf(doubles, 0.0));
                Console.WriteLine(Array.IndexOf(doubles, double.NaN, 1));
                Array.Reverse(doubles);
                Show.Line(doubles);
                Array.Fill(doubles, double.NegativeInfinity, 0, 1);
                Show.Line(doubles);

                // the narrow numbers, bools, enums
                byte[] bytes = { 200, 1, 255, 1 };
                Console.WriteLine(Array.IndexOf(bytes, (byte)255));
                Console.WriteLine(Array.LastIndexOf(bytes, (byte)1));
                sbyte[] small = { -1, 1, -1 };
                Console.WriteLine(Array.LastIndexOf(small, (sbyte)-1, 1));
                short[] shorts = { -300, 300 };
                Console.WriteLine(Array.IndexOf(shorts, (short)300));
                ulong[] huge = { ulong.MaxValue, 1 };
                Console.WriteLine(Array.IndexOf(huge, ulong.MaxValue));
                bool[] flags = new bool[6];
                Array.Fill(flags, true, 2, 2);
                Show.Line(flags);
                Console.WriteLine(Array.IndexOf(flags, true));
                Console.WriteLine(Array.LastIndexOf(flags, false, 3));
                Array.Reverse(flags, 0, 4);
                Show.Line(flags);
                Color[] colors = { Color.Red, Color.Blue, Color.Green, Color.Blue };
                Console.WriteLine(Array.IndexOf(colors, Color.Blue));
                Console.WriteLine(Array.LastIndexOf(colors, Color.Red));
                Array.Fill(colors, Color.Green, 0, 2);
                Array.Reverse(colors);
                Show.Line(colors);
                Big[] bigs = { Big.Low, Big.High };
                Console.WriteLine(Array.IndexOf(bigs, Big.High));

                // structs: Fill and Reverse
                Point[] points = new Point[4];
                Array.Fill(points, new Point(1, 2, true));
                Array.Fill(points, new Point(-3, 4, false), 1, 2);
                Array.Reverse(points, 1, 3);
                Show.Line(points);

                // references: by reference, null too
                Node first = new Node(1);
                Node second = new Node(2);
                Node[] nodes = { first, second, null, first };
                Console.WriteLine(Array.IndexOf(nodes, first));
                Console.WriteLine(Array.LastIndexOf(nodes, first));
                Console.WriteLine(Array.IndexOf(nodes, null));
                Console.WriteLine(Array.IndexOf(nodes, new Node(2)));
                Array.Reverse(nodes);
                Show.Line(nodes);
                Array.Fill(nodes, second, 1, 2);
                Show.Line(nodes);
                Array.Fill(nodes, null);
                Show.Line(nodes);
                int[][] rows = { new[] { 1 }, new[] { 2 } };
                int[] row = rows[1];
                Console.WriteLine(Array.IndexOf(rows, row));
                Array.Reverse(rows);
                Array.Fill(rows, row, 1, 1);
                Console.WriteLine(rows[0][0] + rows[1][0]);
            }
        }
        """;

    /// <summary>
    /// Sort and BinarySearch of every number type and of bools, long enough for each part of .NET's introsort (insertion
    /// sort, median-of-three partitions, the heapsort past the depth limit), with NaN, -0.0 and 0.0 where .NET puts them.
    /// </summary>
    public const string Sorting = """
        using System;

        class Program
        {
            static uint state = 12345;

            static int Next() { state = state * 1103515245 + 12345; return (int)(state >> 8); }

            static void Line(double[] a) { foreach (var x in a) { Console.Write(x); Console.Write(" "); } Console.WriteLine(); }
            static void Line(float[] a) { foreach (var x in a) { Console.Write(x); Console.Write(" "); } Console.WriteLine(); }
            static void Line(int[] a) { foreach (var x in a) { Console.Write(x); Console.Write(" "); } Console.WriteLine(); }

            // the sign of each zero after sorting, and the order: .NET's introsort decides where -0.0 and 0.0 go
            static void Zeros(double[] a)
            {
                foreach (var x in a) { if (x == 0) Console.Write(1 / x < 0 ? "-" : "+"); }
                Console.WriteLine();
                for (int i = 1; i < a.Length; i++) { if (a[i - 1] > a[i]) Console.WriteLine("out of order"); }
            }

            static void Main()
            {
                int[] ints = new int[300];
                for (int i = 0; i < ints.Length; i++) ints[i] = Next() % 1000 - 500;
                Array.Sort(ints);
                Console.WriteLine(ints[0] + " " + ints[150] + " " + ints[299]);
                Console.WriteLine(Array.BinarySearch(ints, ints[77]) >= 0);
                Console.WriteLine(Array.BinarySearch(ints, 10_000));
                Console.WriteLine(Array.BinarySearch(ints, -10_000));
                Console.WriteLine(Array.BinarySearch(ints, 50, 100, ints[120]));

                // a few at a time: two, three, sixteen, seventeen
                int[] two = { 2, 1 };
                Array.Sort(two);
                Line(two);
                int[] three = { 3, 1, 2 };
                Array.Sort(three);
                Line(three);
                int[] sixteen = { 5, 3, 9, 1, 7, 2, 8, 6, 4, 0, 15, 13, 11, 12, 14, 10 };
                Array.Sort(sixteen);
                Line(sixteen);
                int[] seventeen = { 5, 3, 9, 1, 7, 2, 8, 6, 4, 0, 16, 15, 13, 11, 12, 14, 10 };
                Array.Sort(seventeen, 2, 13);
                Line(seventeen);
                Array.Sort(seventeen, 3, 0);
                Array.Sort(new int[0]);

                // already sorted, reversed, all equal: the heapsort past the depth limit for some
                int[] organ = new int[200];
                for (int i = 0; i < organ.Length; i++) organ[i] = i < 100 ? i : 199 - i;
                Array.Sort(organ);
                Console.WriteLine(organ[0] + " " + organ[1] + " " + organ[198] + " " + organ[199]);
                int[] reversed = new int[100];
                for (int i = 0; i < reversed.Length; i++) reversed[i] = 100 - i;
                Array.Sort(reversed);
                Console.WriteLine(reversed[0] + " " + reversed[99]);

                // every number type
                byte[] bytes = { 200, 3, 255, 0, 17 };
                Array.Sort(bytes);
                foreach (var b in bytes) Console.Write(b + " ");
                Console.WriteLine(Array.BinarySearch(bytes, (byte)17));
                sbyte[] sbytes = { 100, -128, 0, 127, -1 };
                Array.Sort(sbytes);
                foreach (var b in sbytes) Console.Write(b + " ");
                Console.WriteLine(Array.BinarySearch(sbytes, (sbyte)-2));
                short[] shorts = { 30000, -30000, 0, 5 };
                Array.Sort(shorts);
                foreach (var s in shorts) Console.Write(s + " ");
                Console.WriteLine();
                ushort[] ushorts = { 65535, 0, 1 };
                Array.Sort(ushorts);
                foreach (var s in ushorts) Console.Write(s + " ");
                Console.WriteLine();
                uint[] uints = { 4000000000, 1, 2147483648 };
                Array.Sort(uints);
                foreach (var u in uints) Console.Write(u + " ");
                Console.WriteLine(Array.BinarySearch(uints, 3u));
                long[] longs = { long.MaxValue, long.MinValue, 0, -1 };
                Array.Sort(longs);
                foreach (var l in longs) Console.Write(l + " ");
                Console.WriteLine(Array.BinarySearch(longs, long.MinValue));
                ulong[] ulongs = { ulong.MaxValue, 0, 1UL << 63 };
                Array.Sort(ulongs);
                foreach (var u in ulongs) Console.Write(u + " ");
                Console.WriteLine();
                nint[] natives = { 5, -5, 0 };
                Array.Sort(natives);
                foreach (var n in natives) Console.Write((long)n + " ");
                Console.WriteLine();
                nuint[] unatives = { 5, 0, 3 };
                Array.Sort(unatives);
                foreach (var n in unatives) Console.Write((ulong)n + " ");
                Console.WriteLine();
                bool[] flags = { true, false, true, false, false };
                Array.Sort(flags);
                foreach (var f in flags) Console.Write(f + " ");
                Console.WriteLine(Array.BinarySearch(flags, true));

                // floats and doubles: NaNs first, then -0.0 and 0.0 where .NET's partitions leave them
                float[] floats = { 3f, float.NaN, -0f, 1f, 0f, -2f, float.NaN, 0f, -0f, float.PositiveInfinity, float.NegativeInfinity };
                Array.Sort(floats);
                Line(floats);
                Console.WriteLine(Array.BinarySearch(floats, 1f));
                Console.WriteLine(Array.BinarySearch(floats, float.NaN));
                double[] zeros = new double[120];
                for (int i = 0; i < zeros.Length; i++)
                {
                    var n = Next();
                    zeros[i] = n % 5 == 0 ? double.NaN : n % 3 == 0 ? -0.0 : n % 3 == 1 ? 0.0 : (n % 7) - 3.5;
                }

                Array.Sort(zeros);
                Zeros(zeros);
                Line(zeros);
                double[] part = new double[40];
                for (int i = 0; i < part.Length; i++) part[i] = i % 2 == 0 ? -0.0 : 0.0;
                Array.Sort(part, 5, 30);
                Zeros(part);
                Console.WriteLine(Array.BinarySearch(part, 0.0));
                Console.WriteLine(Array.BinarySearch(part, 1, 3, -1.0));
            }
        }
        """;

    /// <summary>
    /// Resize (a local, a parameter, a static field, a field of an object, null, larger, smaller, the same) and Clone, of
    /// numbers, structs and references, among enough short-lived arrays that the collector runs again and again: the
    /// copies keep every element, and an array of references keeps what its elements reference.
    /// </summary>
    public static string NewArrays(int rounds) => "using System;\n\n" + Shared + $$"""

        class Holder
        {
            public Node[] Nodes = new Node[1];
        }

        class Program
        {
            static int[] counts;
            static Node[] kept = new Node[0];

            static int Grow(int[] values, int size)
            {
                Array.Resize(ref values, size);
                return values.Length;
            }

            static void Main()
            {
                int[] local = { 1, 2, 3 };
                Array.Resize(ref local, 5);
                Show.Line(local);
                Array.Resize(ref local, 2);
                Show.Line(local);
                int[] same = local;
                Array.Resize(ref local, 2);
                Console.WriteLine(same == local);
                Array.Resize(ref local, 0);
                Show.Line(local);
                Console.WriteLine(Grow(local, 7));
                Array.Resize(ref counts, 3);
                Show.Line(counts);
                counts[2] = 9;
                Array.Resize(ref counts, 4);
                Show.Line(counts);

                int[] clone = (int[])counts.Clone();
                clone[0] = 5;
                Show.Line(clone);
                Show.Line(counts);
                Point[] points = { new Point(1, 2, true), new Point(3, 4, false) };
                Point[] pointClone = (Point[])points.Clone();
                Array.Resize(ref points, 3);
                Show.Line(points);
                Show.Line(pointClone);
                int[] empty = (int[])new int[0].Clone();
                Show.Line(empty);

                // references through many collections
                var holder = new Holder();
                long total = 0;
                for (int round = 0; round < {{rounds}}; round++)
                {
                    var node = new Node(round);
                    node.Next = new Node(-round);
                    Array.Resize(ref holder.Nodes, holder.Nodes.Length % 8 + 1);
                    holder.Nodes[holder.Nodes.Length - 1] = node;
                    Array.Resize(ref kept, kept.Length < 16 ? kept.Length + 1 : 1);
                    kept[kept.Length - 1] = node;
                    Node[] copy = (Node[])kept.Clone();
                    Array.Reverse(copy);
                    Node[] moved = new Node[copy.Length + 1];
                    Array.Copy(copy, 0, moved, 1, copy.Length);
                    int[] garbage = new int[round % 50 + 1];
                    garbage[0] = round;
                    total += moved[1].Value + moved[1].Next.Value + garbage[0];
                    if (Array.IndexOf(moved, node) != 1) Console.WriteLine("lost " + round);
                }

                Console.WriteLine(total);
                Console.WriteLine(kept.Length + " " + kept[0].Value + " " + kept[kept.Length - 1].Next.Value);
                Show.Line(holder.Nodes);
            }
        }
        """;

    /// <summary>
    /// The empty arrays: one object of each array type (as .NET has one for each T), which a type test reads like any
    /// array's, also <c>[]</c> and an empty static array; and clones, whose type is their array's.
    /// </summary>
    public const string EmptyArrays = """
        using System;

        class Node { }

        class Program
        {
            static readonly int[] None = [];
            static readonly long[] NoLongs = { };

            static void Main()
            {
                object ints = Array.Empty<int>();
                Console.WriteLine(ints is int[]);
                Console.WriteLine(ints is long[]);
                Console.WriteLine(ints is object);
                Console.WriteLine(ints is Node[]);
                object again = Array.Empty<int>();
                object unsigned = Array.Empty<uint>();
                Console.WriteLine(ReferenceEquals(ints, again));
                Console.WriteLine(ReferenceEquals(ints, unsigned));
                object nodes = Array.Empty<Node>();
                Console.WriteLine(nodes is Node[]);
                Console.WriteLine(nodes is int[]);
                Console.WriteLine(nodes is object[]);
                int[] none = [];
                object empty = none;
                Console.WriteLine(empty is int[]);
                Console.WriteLine(ReferenceEquals(empty, ints));
                object longs = Array.Empty<long>();
                Console.WriteLine(longs is long[]);
                Console.WriteLine(longs is int[]);
                Console.WriteLine(((int[])ints).Length + ((long[])longs).Length + None.Length + NoLongs.Length);
                Console.WriteLine(Array.IndexOf(None, 1) + Array.LastIndexOf(NoLongs, 2L));
                Console.WriteLine(Array.IndexOf(Array.Empty<Node>(), null));

                // a clone is an array of its own type
                int[] values = { 3, 4 };
                object clone = values.Clone();
                Console.WriteLine(clone is int[]);
                Console.WriteLine(clone is long[]);
                object original = values;
                Console.WriteLine(ReferenceEquals(clone, original));
                Console.WriteLine(((int[])clone)[1]);
                Node[][] grid = { new Node[2], Array.Empty<Node>() };
                var copy = (Node[][])grid.Clone();
                Console.WriteLine(copy[1].Length + copy[0].Length);
                Console.WriteLine(copy[1] == grid[1]);
            }
        }
        """;

    /// <summary>
    /// The methods that make no array, on arrays that need no heap: on the stack and in static fields (a fixed array in
    /// RAM that they write, a constant table that they only read, which stays in flash).
    /// </summary>
    public const string WithoutHeap = "using System;\n\n" + """
        enum Mode : byte { Off, Slow, Fast }

        class Program
        {
            static readonly byte[] Table = { 9, 3, 7, 1, 5 };
            static readonly int[] Readings = new int[6];

            static void Line(int[] a) { foreach (var x in a) { Console.Write(x); Console.Write(" "); } Console.WriteLine(); }

            static void Main()
            {
                Console.WriteLine(Array.IndexOf(Table, (byte)7));
                Console.WriteLine(Array.LastIndexOf(Table, (byte)1, 4, 2));
                Array.Fill(Readings, 2);
                Array.Fill(Readings, 8, 4, 2);
                int[] buffer = new int[6];
                Array.Copy(Readings, 1, buffer, 0, 5);
                Line(buffer);
                Array.Reverse(buffer);
                Array.Sort(buffer, 1, 4);
                Line(buffer);
                Console.WriteLine(Array.BinarySearch(buffer, 1, 4, 8));
                Array.Clear(Readings, 0, 3);
                Readings.CopyTo(buffer, 0);
                Line(buffer);
                Array.Sort(Readings);
                Line(Readings);
                Mode[] modes = new Mode[4];
                Array.Fill(modes, Mode.Fast, 1, 2);
                Console.WriteLine(Array.IndexOf(modes, Mode.Fast) + Array.LastIndexOf(modes, Mode.Off));
                Span<int> view = buffer;
                Array.Clear(buffer);
                Console.WriteLine(view[0] + view[5]);
            }
        }
        """;

    /// <summary>
    /// A statement that .NET throws an exception for, by name: Array.Copy and the rest given null, an index or a length
    /// outside the array, a destination too short, a long that is no int. Each runs after the program prints "1".
    /// </summary>
    public static IReadOnlyList<(string Name, string Statement)> Failures { get; } =
    [
        ("Copy null source", "Array.Copy(none, b, 1);"),
        ("Copy null destination", "Array.Copy(a, none, 1);"),
        ("Copy negative length", "Array.Copy(a, b, -1);"),
        ("Copy source too short", "Array.Copy(b, a, 4);"),
        ("Copy destination too short", "Array.Copy(a, b, 4);"),
        ("Copy range negative length", "Array.Copy(a, -2, b, -3, -1);"),
        ("Copy range negative source index", "Array.Copy(a, -2147483648, b, 0, 1);"),
        ("Copy range negative destination index", "Array.Copy(a, 0, b, -7, 1);"),
        ("Copy range source too short", "Array.Copy(a, 3, b, 0, 3);"),
        ("Copy range destination too short", "Array.Copy(a, 0, b, 1, 3);"),
        ("Copy range source index past the end", "Array.Copy(a, 6, b, 0, 0);"),
        ("Copy range both too short", "Array.Copy(a, 4, b, 2, 3);"),
        ("Copy long huge length", "Array.Copy(a, b, 1L << 32);"),
        ("Copy long negative length", "Array.Copy(a, b, -10L);"),
        ("Copy long huge source index", "Array.Copy(a, 1L << 40, b, 1L << 40, 0L);"),
        ("Copy long huge destination index", "Array.Copy(a, 0L, b, -1L << 33, 0L);"),
        ("Copy long huge range length", "Array.Copy(a, 0L, b, 0L, long.MaxValue);"),
        ("ConstrainedCopy destination too short", "Array.ConstrainedCopy(a, 0, b, 1, 3);"),
        ("CopyTo null destination", "a.CopyTo(none, 0);"),
        ("CopyTo negative index", "a.CopyTo(b, -1);"),
        ("CopyTo too short", "a.CopyTo(b, 0);"),
        ("CopyTo index past the end", "b.CopyTo(a, 6);"),
        ("CopyTo of null", "none.CopyTo(b, 0);"),
        ("CopyTo long huge index", "a.CopyTo(b, 1L << 32);"),
        ("Clear null", "Array.Clear(none);"),
        ("Clear range null", "Array.Clear(none, 0, 1);"),
        ("Clear negative index", "Array.Clear(a, -1, 1);"),
        ("Clear negative length", "Array.Clear(a, 0, -1);"),
        ("Clear past the end", "Array.Clear(a, 3, 3);"),
        ("Fill null", "Array.Fill(none, 1);"),
        ("Fill range negative start", "Array.Fill(a, 1, -1, 1);"),
        ("Fill range start past the end", "Array.Fill(a, 1, 6, 0);"),
        ("Fill range negative count", "Array.Fill(a, 1, 0, -1);"),
        ("Fill range count past the end", "Array.Fill(a, 1, 3, 3);"),
        ("IndexOf null", "Array.IndexOf(none, 1);"),
        ("IndexOf start past the end", "Array.IndexOf(a, 1, 6);"),
        ("IndexOf negative start", "Array.IndexOf(a, 1, -1, 1);"),
        ("IndexOf count past the end", "Array.IndexOf(a, 1, 3, 3);"),
        ("LastIndexOf null", "Array.LastIndexOf(none, 1, 0);"),
        ("LastIndexOf start at the end", "Array.LastIndexOf(a, 1, 5);"),
        ("LastIndexOf negative start", "Array.LastIndexOf(a, 1, -1, 0);"),
        ("LastIndexOf negative count", "Array.LastIndexOf(a, 1, 4, -1);"),
        ("LastIndexOf count past the start", "Array.LastIndexOf(a, 1, 2, 4);"),
        ("LastIndexOf of nothing at 1", "Array.LastIndexOf(new int[0], 1, 1);"),
        ("LastIndexOf of nothing counting 1", "Array.LastIndexOf(new int[0], 1, 0, 1);"),
        ("Reverse null", "Array.Reverse(none);"),
        ("Reverse negative index", "Array.Reverse(a, -1, 1);"),
        ("Reverse negative length", "Array.Reverse(a, 0, -1);"),
        ("Reverse past the end", "Array.Reverse(a, 3, 3);"),
        ("Sort null", "Array.Sort(none);"),
        ("Sort negative index", "Array.Sort(a, -1, 1);"),
        ("Sort negative length", "Array.Sort(a, 0, -1);"),
        ("Sort past the end", "Array.Sort(a, 3, 3);"),
        ("BinarySearch null", "Array.BinarySearch(none, 1);"),
        ("BinarySearch range null", "Array.BinarySearch(none, 0, 1, 1);"),
        ("BinarySearch negative index", "Array.BinarySearch(a, -1, 1, 1);"),
        ("BinarySearch past the end", "Array.BinarySearch(a, 3, 3, 1);"),
        ("Resize negative size", "Array.Resize(ref a, -1);"),
        ("Clone of null", "Console.WriteLine(((int[])none.Clone()).Length);"),
    ];

    /// <summary>The program of a failure: arrays a (5 elements), b (3) and none (null), "1", the statement, "not printed".</summary>
    public static string Failure(string statement) => $$"""
        using System;

        class Program
        {
            static int[] none;

            static void Main()
            {
                int[] a = { 1, 2, 3, 4, 5 };
                int[] b = new int[3];
                Console.WriteLine(1);
                {{statement}}
                Console.WriteLine("not printed");
                Console.WriteLine(a[0] + b[0]);
            }
        }
        """;
}
