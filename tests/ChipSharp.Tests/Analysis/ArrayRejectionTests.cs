using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// The rule of arrays on the stack (see StackArrays): what keeps an array inside the method that makes it is
/// accepted, and everything else is rejected with a message in C# words.
/// </summary>
public sealed class ArrayRejectionTests
{
    private const string Escapes = "an array that leaves the method that creates it (returned, or stored in a field or "
        + "in another array) arrives with M3 (the heap). For now, keep each array in the method that creates it; the "
        + "methods it calls can take it as a parameter.";

    [Theory]
    [InlineData("locals, indexing and Length", """
        int[] values = new int[4];
        int[] same = values;
        same[1] = 5;
        values[2] = same[1] + values.Length;
        Console.WriteLine(values[2]);
        """)]
    [InlineData("passed down, and on again", """
        int[] values = { 1, 2, 3 };
        Console.WriteLine(Outer(values, new int[2]));
        """)]
    [InlineData("foreach", """
        int total = 0;
        foreach (int value in new[] { 4, 5, 6 })
        {
            total += value;
        }

        Console.WriteLine(total);
        """)]
    [InlineData("every element type", """
        sbyte[] a = { -1, 2, 3 }; byte[] b = { 255, 1, 2 }; short[] c = { -300, 1, 2 }; ushort[] d = { 60000, 1, 2 };
        uint[] e = { 4000000000, 1, 2 }; long[] f = { -5, 1, 2 }; ulong[] g = { 5, 1, 2 }; bool[] h = { true, false };
        nint[] i = new nint[2]; nuint[] j = new nuint[2];
        Console.WriteLine(a[0] + b[0] + c[0] + d[0] + e[0] + f[0] + (long)g[0] + i.Length + j.Length);
        Console.WriteLine(h[0]);
        """)]
    [InlineData("empty arrays", """
        int[] none = [];
        int[] zero = new int[0];
        Console.WriteLine(none.Length + zero.Length + Count());
        """)]
    [InlineData("a long index and a compound assignment", """
        long[] values = new long[3];
        long index = 2;
        values[index] += 7;
        values[1]++;
        Console.WriteLine(values[index] + values[1]);
        """)]
    [InlineData("a new array each time round a loop, the old one no longer used", """
        int sum = 0;
        for (int i = 0; i < 3; i++)
        {
            int[] buffer = new int[2];
            buffer[i % 2] = i;
            sum += buffer[0] + Outer(buffer, buffer);
        }

        Console.WriteLine(sum);
        """)]
    public void What_keeps_an_array_in_the_method_that_makes_it_is_accepted(string what, string main)
    {
        var analysis = Check(Program(main, """
            static int Outer(int[] first, int[] second) => Inner(first) + second.Length;

            static int Inner(int[] values)
            {
                int[] own = new int[1];
                values = own;
                return values.Length;
            }

            static int Count(params int[] values) => values.Length;
            """));

        Assert.True(analysis.IsSupported, $"{what}:\n{analysis.Details}");
    }

    [Fact]
    public void Arrays_of_structs_and_enums_and_references_to_elements_that_stay_below_the_array_are_accepted()
    {
        // a reference into an array may go down to callees and come back up from them, but not above the array
        var analysis = Check(Program("""
            Point[] points = new Point[2];
            points[0] = new Point { X = 1 };
            points[1].X = 2;
            points[1].Move(3);
            ref Point first = ref points[0];
            first.X += points.Length;
            Color[] colors = { Color.Blue, Color.Red };
            colors[1]++;
            int[] values = { 3, 1 };
            Swap(ref values[0], ref values[1]);
            ref int largest = ref Largest(values);
            largest = Same(ref values[1]) + Same(ref Pick(true, ref values[0], ref points[1].X));
            Console.WriteLine(first.X + (int)colors[1] + largest);
            """, """
            struct Point
            {
                public int X;

                public void Move(int by) => X += by;
            }

            enum Color : byte { Red, Green, Blue }

            static void Swap(ref int a, ref int b) => (a, b) = (b, a);

            static ref int Largest(int[] values) => ref values[0] > values[1] ? ref values[0] : ref values[1];

            static ref int Pick(bool first, ref int a, ref int b) => ref first ? ref a : ref b;

            static int Same(ref int value) => value;
            """));

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void A_reference_to_an_element_returned_from_the_method_that_makes_the_array_is_rejected()
    {
        // directly, through a ref local, through a method that returns the reference it is given, and from either of two
        var problems = Problems(Program("""
            Console.WriteLine(Direct() + Kept() + Through() + Either(true));
            """, """
            static int shared;

            static ref int Direct()
            {
                int[] values = new int[2];
                return ref values[1];
            }

            static ref int Kept()
            {
                int[] values = new int[2];
                ref int last = ref values[1];
                return ref last;
            }

            static ref int Through()
            {
                int[] values = new int[2];
                return ref Same(ref values[0]);
            }

            static ref int Either(bool own)
            {
                int[] values = new int[2];
                return ref own ? ref values[0] : ref shared;
            }

            static ref int Same(ref int value) => ref value;
            """));

        Assert.Equal(
        [
            $"Program.Direct() at IL_000c (ret): {Escapes}",
            $"Program.Kept() at IL_000c (ret): {Escapes}",
            $"Program.Through() at IL_0011 (ret): {Escapes}",
            $"Program.Either(bool) at IL_0017 (ret): {Escapes}",
        ], problems);
    }

    [Fact]
    public void A_reference_to_an_element_of_an_array_made_again_in_a_loop_is_rejected_while_in_use()
    {
        var problems = Problems(Program("""
            int[] first = new int[1];
            ref int previous = ref first[0];
            for (int i = 0; i < 3; i++)
            {
                int[] current = new int[1];
                current[0] = previous + 1;
                previous = ref current[0];
            }

            Console.WriteLine(previous);
            """));

        Assert.Equal(["Program.Main() at IL_0012 (newarr): an array made in a loop while the one made there before is "
            + "still in use arrives with M3 (the heap). For now, make the array once, before the loop, and reuse it."],
            problems);
    }

    [Fact]
    public void An_array_of_a_struct_that_cannot_compile_says_what_keeps_the_struct_out()
    {
        // a struct that holds an array of itself is no trouble either
        var problems = Problems(Program("""
            Person[] people = new Person[2];
            Node[] nodes = new Node[1];
            Console.WriteLine(people.Length + nodes.Length);
            """, """
            struct Person
            {
                public string Name;
            }

            struct Node
            {
                public Node[] Children;
            }
            """));

        const string Name = "text strings in the field Program.Person.Name arrive with M3 (the heap). For now, keep text "
            + "in locals, parameters, return values and static fields.";
        const string Children = "an array that leaves the method that creates it (returned, or stored in a field or in "
            + "another array) in the field Program.Node.Children arrives with M3 (the heap). For now, keep each array in "
            + "the method that creates it; the methods it calls can take it as a parameter.";
        Assert.Equal(
        [
            $"Program.Main(), local variable V_0 (Program.Node[]): {Children}",
            $"Program.Main() at IL_0001 (newarr): {Name}",
            $"Program.Main() at IL_0007 (newarr): {Children}",
        ], problems);
    }

    [Fact]
    public void A_returned_array_is_rejected_at_the_return_type()
    {
        var problems = Problems(Program("Console.WriteLine(Make().Length + Same(new int[1]).Length);", """
            static int[] Make() => new int[2];

            static int[] Same(int[] values) => values;
            """));

        Assert.Equal(
        [
            $"Program.Make(), return type (int[]): {Escapes}",
            $"Program.Same(int[]), return type (int[]): {Escapes}",
        ], problems);
    }

    [Fact]
    public void An_array_kept_in_a_static_field_by_a_method_is_rejected_where_the_field_is_used()
    {
        var problems = Problems(Program("Keep(new int[2]); Console.WriteLine(saved.Length);", """
            static int[] saved;

            static void Keep(int[] values) => saved = values;
            """));

        // a static field holds an array without a heap only when its initializer gives it one (see StaticArrays)
        const string Assigned = "an array in a static field that is assigned again, or anywhere but in its field "
            + "initializer or static constructor arrives with M3 (the heap). For now, assign it once, where the field "
            + "is declared (static int[] buffer = new int[16];), and change its elements instead.";
        Assert.Equal(
        [
            $"Program.Main() at IL_000b (ldsfld): {Assigned}",
            $"Program.Keep(int[]) at IL_0001 (stsfld): {Assigned}",
        ], problems);
    }

    [Fact]
    public void An_array_stored_in_another_array_is_rejected_as_an_array_of_arrays()
    {
        var problems = Problems(Program("""
            int[][] grid = new int[2][];
            grid[0] = new int[3];
            Console.WriteLine(grid[0].Length);
            """));

        const string References = "arrays of strings, objects or arrays arrive with M3 (the heap). For now, use arrays "
            + "of numbers, bools, structs or enums.";
        Assert.Equal(
        [
            $"Program.Main() at IL_0001 (newarr): {References}",
            $"Program.Main() at IL_000e (stelem.ref): {References}",
            $"Program.Main() at IL_0010 (ldelem.ref): {References}",
        ], problems);
    }

    [Fact]
    public void An_array_passed_to_a_library_method_is_rejected_as_that_library_call()
    {
        var problems = Problems(Program("""
            int[] values = new int[3];
            Array.Clear(values);
            Console.WriteLine(values[0]);
            """));

        Assert.Equal(["Program.Main() at IL_0007 (call): the library method System.Array.Clear(System.Array) is not "
            + "available yet."], problems);
    }

    [Fact]
    public void An_array_whose_size_is_not_a_constant_is_rejected()
    {
        var problems = Problems(Program("Console.WriteLine(Sized(3));", """
            static int Sized(int n)
            {
                int[] values = new int[n];
                return values.Length;
            }
            """));

        Assert.Equal(["Program.Sized(int) at IL_0001 (newarr): an array whose size is known only when the program runs "
            + "arrives with M3 (the heap). For now, give the array a fixed size: a constant, such as new int[16]."],
            problems);
    }

    [Fact]
    public void An_array_made_again_in_a_loop_while_the_one_before_is_in_use_is_rejected()
    {
        // each turn reuses the elements of the newarr: the array of the turn before (in previous) must be done with
        var problems = Problems(Program("""
            int[] previous = new int[1];
            for (int i = 0; i < 3; i++)
            {
                int[] current = new int[1];
                current[0] = previous[0] + 1;
                previous = current;
            }

            Console.WriteLine(previous[0]);
            """));

        Assert.Equal(["Program.Main() at IL_000c (newarr): an array made in a loop while the one made there before is "
            + "still in use arrives with M3 (the heap). For now, make the array once, before the loop, and reuse it."],
            problems);
    }

    [Fact]
    public void Comparing_arrays_or_testing_one_for_null_is_rejected()
    {
        var problems = Problems(Program("Console.WriteLine(Same(new int[1], new int[1]));", """
            static bool Same(int[] a, int[] b)
            {
                if (a != null)
                {
                    return a == b;
                }

                return false;
            }
            """));

        const string Comparisons = "comparing arrays (or an array with null) arrives with M3 (the heap). For now, leave the "
            + "comparison out: an array on the stack is never null.";
        Assert.Equal(
        [
            $"Program.Same(int[], int[]) at IL_0001 (brfalse.s): {Comparisons}",
            $"Program.Same(int[], int[]) at IL_0005 (ceq): {Comparisons}",
        ], problems);
    }

    [Fact]
    public void Arrays_of_unsupported_elements_say_what_the_element_needs()
    {
        // an array of floats is fine now that floats are
        var problems = Problems(Program("""
            float[] weights = { 0.5f, 2f };
            char[] letters = new char[2];
            Console.WriteLine(letters.Length + (int)weights[1]);
            """));

        Assert.Equal(
            ["Program.Main() at IL_0018 (newarr): characters (char) arrive with M3 (the heap). For now, use whole numbers."],
            problems);
    }

    [Fact]
    public void Multi_dimensional_arrays_are_rejected_with_a_hint()
    {
        var problems = Problems(Program("""
            int[,] grid = new int[2, 3];
            grid[1, 2] = 5;
            Console.WriteLine(grid[1, 2]);
            """));

        const string Grids = "multi-dimensional arrays (int[,]) arrive later. For now, use a one-dimensional array "
            + "and compute the index: row * width + column.";
        Assert.Equal(
        [
            $"Program.Main() at IL_0002 (newobj): {Grids}",
            $"Program.Main() at IL_000b (call): {Grids}",
            $"Program.Main() at IL_0012 (call): {Grids}",
        ], problems);
    }

    [Fact]
    public void Checked_conversions_and_type_handles_are_still_rejected_outside_arrays()
    {
        var problems = Problems(Program("Console.WriteLine((long)Narrow(5) + Kind());", """
            static nint Narrow(long value) => checked((nint)value);

            static int Kind() => typeof(int) == typeof(long) ? 1 : 0;
            """));

        Assert.Contains("Program.Narrow(long) at IL_0001 (conv.ovf.i): checked arithmetic arrives later. For now, "
            + "leave out checked: ChipSharp's arithmetic wraps around on overflow, like unchecked C#.", problems);
        Assert.Contains("Program.Kind() at IL_0000 (ldtoken): typeof and type handles arrive later. For now, leave "
            + "them out.", problems);
    }

    [Fact]
    public void The_summary_groups_the_arrays_that_leave_their_method()
    {
        var analysis = Check(Program("Console.WriteLine(First() + Make().Length);", """
            static ref int First()
            {
                int[] values = new int[2];
                return ref values[0];
            }

            static int[] Make() => new int[2];
            """));

        var group = Assert.Single(analysis.Groups);
        Assert.Equal("an array that leaves the method that creates it (returned, or stored in a field or in another "
            + "array): 2 places in Program.First() (IL_000c) and Program.Make() (return type). For now, keep "
            + "each array in the method that creates it; the methods it calls can take it as a parameter.",
            group.Line.Replace(" arrives with M3 (the heap)", "", StringComparison.Ordinal));
        Assert.StartsWith("an array that leaves the method that creates it (returned, or stored in a field or in "
            + "another array) arrives with M3 (the heap): 2 places", group.Line);
    }

    /// <summary>A class Program with <paramref name="main"/> as its Main and <paramref name="members"/> beside it.</summary>
    private static string Program(string main, string members = "") => $$"""
        using System;

        class Program
        {
            {{members.Replace("\n", "\n    ", StringComparison.Ordinal)}}

            static void Main()
            {
                {{main.Replace("\n", "\n        ", StringComparison.Ordinal)}}
            }
        }
        """;
}
