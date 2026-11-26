namespace ChipSharp.Tests.Heap;

/// <summary>
/// The C# programs with arrays on the heap (see HeapArrays) that the PC and the Pico tests run, each printing what it
/// checks, so that an array the collector lost shows as a wrong number or a stop.
/// </summary>
internal static class HeapArrayPrograms
{
    /// <summary>
    /// Every way an array gets on the heap, and what it does there: returned, kept in a static field and replaced, kept
    /// through the parameters of other methods, of a size known when the program runs (an int, a uint, a byte), of every
    /// element type, null, compared, arrays of arrays (also on the stack, and three deep), arrays of objects, empty ones.
    /// </summary>
    public const string Basics = """
        using System;

        class Program
        {
            static int[] saved;
            static int[][] rows;

            struct Point
            {
                public int X;
                public int Y;
            }

            static int[] Squares(int n)
            {
                int[] values = new int[n];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = i * i;
                }

                return values;
            }

            static int[] Same(int[] values) => values;

            static void Keep(int[] values) => saved = values;

            static void Pass(int[] values) => Keep(values);

            static long Sum(int[] values)
            {
                long total = 0;
                foreach (int value in values)
                {
                    total += value;
                }

                return total;
            }

            static int Count(int[] values) => values == null ? -1 : values.Length;

            static int[] Pick(bool first, int[] a, int[] b) => first ? a : b;

            static Point[] Points(int n)
            {
                Point[] points = new Point[n];
                for (int i = 0; i < n; i++)
                {
                    points[i].X = i;
                    points[i].Y = -i;
                }

                return points;
            }

            static object[] Things(int n)
            {
                object[] things = new object[n];
                for (int i = 0; i < n; i++)
                {
                    things[i] = new object();
                }

                return things;
            }

            static int Five() => 5;

            static void Main()
            {
                Console.WriteLine(Sum(Squares(10)));
                int[] onStack = new int[3];
                onStack[1] = 7;
                Console.WriteLine(Sum(onStack));
                Console.WriteLine(Same(Squares(4))[3]);
                Pass(new int[] { 4, 5, 6 });
                Console.WriteLine(saved[2]);
                int[] one = Squares(2);
                Keep(one);
                one[1] = 99;
                Console.WriteLine(saved[1]);
                Console.WriteLine(saved == one);
                Console.WriteLine(saved != Squares(2));

                int[] none = null;
                Console.WriteLine(Count(none));
                Console.WriteLine(Count(saved));
                Console.WriteLine(none == null);
                Console.WriteLine(Pick(false, saved, null) == null);
                Console.WriteLine(Pick(true, saved, null)[1]);
                Console.WriteLine(none?.Length ?? -2);

                uint size = (uint)Five();
                byte[] bytes = new byte[size];
                bytes[4] = 200;
                Console.WriteLine(bytes.Length + bytes[4]);
                long[] longs = new long[bytes[4] / 50];
                longs[3] = long.MinValue;
                Console.WriteLine(longs[3]);
                bool[] flags = new bool[size];
                flags[2] = true;
                Console.WriteLine(flags[2] && !flags[3]);
                double[] halves = new double[Five()];
                halves[4] = 0.1 + 0.2;
                Console.WriteLine(halves[4]);
                Point[] points = Points(3);
                Console.WriteLine(points[2].X + points[2].Y * 10);

                rows = new int[3][];
                for (int r = 0; r < rows.Length; r++)
                {
                    rows[r] = Squares(r + 2);
                }

                int[][] grid = { new[] { 1, 2 }, new[] { 3, 4, 5 }, Array.Empty<int>() };
                Console.WriteLine(grid[1][2] + grid[2].Length + rows[2][3]);
                grid[2] = rows[0];
                grid[0][1] += 40;
                Console.WriteLine(grid[2][1] + grid[0][1]);
                int[][][] cube = new int[2][][];
                cube[1] = grid;
                Console.WriteLine(cube[1][1][0] + (cube[0] == null ? 100 : 0));
                foreach (int[] row in grid)
                {
                    Console.Write(row.Length);
                }

                Console.WriteLine();

                object[] things = new object[3];
                things[0] = new object();
                things[2] = things[0];
                Console.WriteLine(things[0] == things[2]);
                Console.WriteLine(things[1] == null);
                object[] made = Things(4);
                Console.WriteLine(made.Length + (made[3] != null && made[3] != made[2] ? 1 : 0));

                int[] empty = [];
                int[] zero = new int[0];
                int[] sizedZero = new int[Five() - 5];
                Console.WriteLine(empty.Length + zero.Length + sizedZero.Length);
                Console.WriteLine(zero == empty);
                Console.WriteLine(empty == Array.Empty<int>());
                Keep(sizedZero);
                Console.WriteLine(saved.Length);
                Keep(empty);
                Console.WriteLine(saved == empty);

                int[] squares = Squares(6);
                ref int last = ref squares[5];
                last += 1;
                Console.WriteLine(squares[5]);
            }
        }
        """;

    /// <summary>
    /// Arrays that live through many collections, each held only one way (a static field, a local, the elements of an
    /// array of arrays on the stack and of one on the heap, the elements of an array of objects, a parameter across a
    /// call that collects, a reference to one of its elements, the view of an empty array on the heap), while
    /// <paramref name="churn"/> short-lived arrays of many lengths fill the heap again and again; then the static field
    /// replaced many times. Prints what each holds.
    /// </summary>
    public static string Survivors(int churn) => $$"""
        using System;

        class Program
        {
            static int[] kept;
            static object[] boxes;

            static int[] Fill(int n, int seed)
            {
                int[] values = new int[n];
                for (int i = 0; i < n; i++)
                {
                    values[i] = seed + i;
                }

                return values;
            }

            static long Churn(int rounds)
            {
                long total = 0;
                for (int i = 0; i < rounds; i++)
                {
                    int[] garbage = Fill(1 + i % 37, i);
                    total += garbage[garbage.Length - 1];
                    if (i % 7 == 0)
                    {
                        byte[] small = new byte[i % 5];
                        total += small.Length;
                    }
                }

                return total;
            }

            static long Sum(int[] values)
            {
                long sum = 0;
                foreach (int value in values)
                {
                    sum += value;
                }

                return sum;
            }

            static int[][] Rows(int count)
            {
                int[][] rows = new int[count][];
                for (int r = 0; r < count; r++)
                {
                    rows[r] = Fill(r + 1, r);
                }

                return rows;
            }

            static long AcrossCall(int[] values, int rounds) => Churn(rounds) + Sum(values);

            static int Zero() => 0;

            static void Main()
            {
                kept = Fill(50, 1000);
                int[] local = Fill(40, 2000);
                int[][] rows = new int[20][];
                for (int r = 0; r < rows.Length; r++)
                {
                    rows[r] = Fill(r + 1, r * 100);
                }

                boxes = new object[10];
                for (int b = 0; b < boxes.Length; b++)
                {
                    boxes[b] = new object();
                }

                int[][] heapRows = Rows(30);
                int[] empty = new int[Zero()];
                ref int inside = ref Fill(10, 7)[9];

                Console.WriteLine(Churn({{churn}}));
                Console.WriteLine(Sum(kept));
                Console.WriteLine(Sum(local));
                long rowsSum = 0;
                foreach (int[] row in rows)
                {
                    rowsSum += Sum(row);
                }

                Console.WriteLine(rowsSum);
                Console.WriteLine(boxes[3] != null && boxes[3] != boxes[4]);
                long heapSum = 0;
                foreach (int[] row in heapRows)
                {
                    heapSum += Sum(row);
                }

                Console.WriteLine(heapSum);
                Console.WriteLine(inside);
                Console.WriteLine(AcrossCall(Fill(25, 5), {{churn / 4}}));

                // the empty array, held so far by its view alone, kept as an object through more collections
                kept = empty;
                Console.WriteLine(Churn({{churn / 4}}));
                Console.WriteLine(kept.Length + (kept == empty ? 10 : 0));

                for (int i = 0; i < {{churn / 10}}; i++)
                {
                    kept = Fill(10 + i % 20, i);
                }

                Console.WriteLine(Sum(kept));
            }
        }
        """;

    /// <summary>
    /// Where .NET throws for arrays on the heap: <paramref name="what"/> is "length of null", "element of null",
    /// "store into null", "negative length" or "index out of range". Prints 1 first.
    /// </summary>
    public static string Failure(string what)
    {
        var statement = what switch
        {
            "length of null" => "Console.WriteLine(Find(0).Length);",
            "element of null" => "Console.WriteLine(Find(0)[0]);",
            "store into null" => "int[][] rows = new int[2][]; rows[1][0] = 5;",
            "negative length" => "Console.WriteLine(new int[n].Length);",
            _ => "Console.WriteLine(Find(3)[3]);",
        };

        return $$"""
            using System;

            class Program
            {
                static int[] Find(int count) => count > 0 ? new int[count] : null;

                static void Main()
                {
                    Console.WriteLine(1);
                    int n = Find(1).Length - 2;
                    {{statement}}
                    Console.WriteLine(2);
                }
            }
            """;
    }

    /// <summary>What .NET writes first for the exception of each of <see cref="Failure"/>'s cases.</summary>
    public static string FailureMessage(string what) => "Unhandled exception. " + (what switch
    {
        "negative length" => "System.OverflowException: Arithmetic operation resulted in an overflow.",
        "index out of range" => "System.IndexOutOfRangeException: Index was outside the bounds of the array.",
        _ => "System.NullReferenceException: Object reference not set to an instance of an object.",
    }) + "\r\n";

    public static IEnumerable<string> Failures =>
        ["length of null", "element of null", "store into null", "negative length", "index out of range"];
}
