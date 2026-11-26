using ChipSharp.Ir;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Arrays on the heap (see HeapArrays) in TIR: <c>newarray</c> and the view of its elements, the view turned into the
/// array object where a static field or an element holds it and back where one is read, the null tests, and what does
/// not change: an array that stays in its method with a constant size is on the stack.
/// </summary>
public sealed class IlToTirHeapArrayTests
{
    private const string Source = """
        class Program
        {
            static int[] saved;

            static int[] Make(int n) => new int[n];

            static void Keep(int[] values) => saved = values;

            static int Saved() => saved.Length;

            static int Local()
            {
                int[] values = new int[3];
                return values.Length;
            }

            static int First(int[] values) => values[0];

            static bool IsNull(int[] values) => values == null;

            static int[] Row(int[][] rows) => rows[1];

            static void Put(int[][] rows, int[] row) => rows[0] = row;

            static void Main()
            {
                Keep(Make(2));
                System.Console.WriteLine(Saved() + Local() + First(null) + First(new int[1]) + (IsNull(saved) ? 1 : 0));
                int[][] rows = new int[2][];
                Put(rows, Make(1));
                System.Console.WriteLine(Row(rows).Length);
            }
        }
        """;

    private static readonly Lazy<TirModule> Module = new(() => Lowered.Module(Source));

    [Fact]
    public void An_array_of_a_size_known_when_the_program_runs_is_a_newarray_and_the_view_of_its_elements()
    {
        Assert.Equal("""
            function ChipSharp.Array Program.Make(i32 %n)
            IL_0000:
              %0 = newarray int[], %n
              %1 = arraydata i32 %0
              %2 = call ChipSharp.Array ChipSharp.Array.Make(%1, %n)
              return %2

            """.ReplaceLineEndings("\n"), Function("Program.Make"));
    }

    [Fact]
    public void A_static_field_holds_the_array_object_a_root_of_the_collector_and_gives_back_a_view_that_may_be_null()
    {
        Assert.Contains("global ref @Program.saved", TirPrinter.Print(Module.Value), StringComparison.Ordinal);
        Assert.Equal("""
            function void Program.Keep(ChipSharp.Array %values)
            IL_0000:
              %0 = call ref ChipSharp.Array.ToObject(%values)
              %1 = addr @Program.saved
              store ref [%1], %0
              return

            """.ReplaceLineEndings("\n"), Function("Program.Keep"));
        Assert.StartsWith("""
            function i32 Program.Saved()
            IL_0000:
              %0 = addr @Program.saved
              %1 = load ref [%0]
              %2 = call ChipSharp.Array ChipSharp.Array.FromObject(%1)
              %3 = call i32 ChipSharp.Array.LengthOrNull(%2)

            """.ReplaceLineEndings("\n"), Function("Program.Saved"));
    }

    [Fact]
    public void The_view_becomes_its_object_less_the_offset_of_the_elements_and_an_object_its_view_or_null()
    {
        Assert.Equal("""
            function ref ChipSharp.Array.ToObject(ChipSharp.Array %array)
            entry:
              %0 = call ptr ChipSharp.Array.Data(%array)
              %1 = zero ptr
              %2 = eq ptr %0, %1
              branch %2, null, not_null
            null:
              %3 = zero ref
              return %3
            not_null:
              %4 = call ptr ChipSharp.Array.Data(%array)
              %5 = arrayobject %4
              return %5

            """.ReplaceLineEndings("\n"), Function("ChipSharp.Array.ToObject"));
        Assert.Equal("""
            function ChipSharp.Array ChipSharp.Array.FromObject(ref %array)
            entry:
              %0 = zero ref
              %1 = eq ref %array, %0
              branch %1, null, not_null
            null:
              %2 = zero ChipSharp.Array
              return %2
            not_null:
              %3 = arraydata i32 %array
              %4 = arraylength %array
              %5 = call ChipSharp.Array ChipSharp.Array.Make(%3, %4)
              return %5

            """.ReplaceLineEndings("\n"), Function("ChipSharp.Array.FromObject"));
    }

    [Fact]
    public void An_array_that_stays_in_its_method_with_a_constant_size_is_still_on_the_stack()
    {
        var local = Function("Program.Local");

        Assert.Contains("$array_IL_0001: [3 x i32]", local, StringComparison.Ordinal);
        Assert.DoesNotContain("newarray", local, StringComparison.Ordinal);
        Assert.Contains("call i32 ChipSharp.Array.Length(", local, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parameter_some_caller_gives_null_is_indexed_after_the_bounds_check_that_tells_null_apart()
    {
        Assert.Equal("""
            function i32 Program.First(ChipSharp.Array %values)
            IL_0000:
              %0 = const i32 0
              call void ChipSharp.Array.CheckIndexOrNull(%values, %0)
              %1 = call ptr ChipSharp.Array.Data(%values)
              %2 = elementaddr i32 %1, %0
              %3 = load i32 [%2]
              return %3

            """.ReplaceLineEndings("\n"), Function("Program.First"));
        Assert.Equal("""
            function void ChipSharp.Array.CheckIndexOrNull(ChipSharp.Array %array, i32 %index)
            entry:
              %0 = call i32 ChipSharp.Array.Length(%array)
              %1 = ult i32 %index, %0
              branch %1, in_range, not_in_range
            in_range:
              return
            not_in_range:
              %2 = call ptr ChipSharp.Array.Data(%array)
              %3 = zero ptr
              %4 = eq ptr %2, %3
              branch %4, null, out_of_range
            null:
              call void ChipSharp.Runtime.NullReference()
              return
            out_of_range:
              call void ChipSharp.Array.CheckIndex(%array, %index)
              return

            """.ReplaceLineEndings("\n"), Function("ChipSharp.Array.CheckIndexOrNull"));
    }

    [Fact]
    public void An_array_compared_with_null_compares_the_address_of_its_elements()
    {
        Assert.Equal("""
            function bool Program.IsNull(ChipSharp.Array %values)
            IL_0000:
              %0 = zero ptr
              %1 = call ptr ChipSharp.Array.Data(%values)
              %2 = eq ptr %1, %0
              return %2

            """.ReplaceLineEndings("\n"), Function("Program.IsNull"));
    }

    [Fact]
    public void An_element_of_an_array_of_arrays_is_a_ref_loaded_as_a_view_and_stored_as_the_object()
    {
        Assert.Contains("""
              %2 = elementaddr ref %1, %0
              %3 = load ref [%2]
              %4 = call ChipSharp.Array ChipSharp.Array.FromObject(%3)
              return %4
            """.ReplaceLineEndings("\n"), Function("Program.Row"), StringComparison.Ordinal);
        Assert.Contains("""
              %3 = call ref ChipSharp.Array.ToObject(%row)
              store ref [%2], %3
            """.ReplaceLineEndings("\n"), Function("Program.Put"), StringComparison.Ordinal);

        var types = TirPrinter.Print(Module.Value).Split('\n').Where(line => line.StartsWith("type ", StringComparison.Ordinal));
        Assert.Equal(["type int[] = array of i32"], types);
    }

    private static string Function(string name) =>
        TirPrinter.Print(Assert.Single(Module.Value.Functions, function => function.Name == name));
}
