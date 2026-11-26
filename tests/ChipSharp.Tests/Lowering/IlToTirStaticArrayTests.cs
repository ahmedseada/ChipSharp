using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// The TIR that arrays in static fields become (see StaticArrays): a global array per field, constant for a table and
/// variable for a fixed array, starting with the initializer's constants; reading the field is the array value of the
/// global's address; and the initializer's code for it is gone.
/// </summary>
public sealed class IlToTirStaticArrayTests
{
    [Fact]
    public void A_table_is_a_constant_global_a_written_array_a_variable_and_the_field_is_the_globals_array_value()
    {
        var module = Lowered.Module("""
            class Program
            {
                static readonly byte[] Digits = { 0x3F, 0x06, 0x5B, 0x4F };
                static int[] buffer = new int[2];

                static void Main()
                {
                    buffer[1] = Digits[3];
                    System.Console.WriteLine(buffer[1]);
                }
            }
            """);

        // nothing is left to run before Main: the type initializer made the two arrays only
        Assert.Equal("Program.Main", module.EntryPoint!.Name);
        Assert.Equal(["global [2 x i32] @Program.buffer", "constant [4 x i8] @Program.Digits = [63, 6, 91, 79]"],
            Globals(module));
        Assert.DoesNotContain(module.Functions, function => function.Name == "Program..cctor");
        Assert.StartsWith("""
            function void Program.Main()
            IL_0000:
              %0 = addr @Program.buffer
              %1 = const i32 2
              %2 = call ChipSharp.Array ChipSharp.Array.Make(%0, %1)
              %3 = addr @Program.Digits
              %4 = const i32 4
              %5 = call ChipSharp.Array ChipSharp.Array.Make(%3, %4)
              %6 = const i32 3
              call void ChipSharp.Array.CheckIndex(%5, %6)
              %7 = call ptr ChipSharp.Array.Data(%5)
              %8 = elementaddr i8 %7, %6
              %9 = load i8 [%8]
              %10 = uconvert i8 %9 to i32
              %11 = const i32 1
              call void ChipSharp.Array.CheckIndex(%2, %11)
              %12 = call ptr ChipSharp.Array.Data(%2)
              %13 = elementaddr i32 %12, %11
              store i32 [%13], %10

            """.ReplaceLineEndings("\n"), Function(module, "Program.Main"));
    }

    [Fact]
    public void A_static_constructor_that_does_more_still_runs_without_making_the_arrays()
    {
        var module = Lowered.Module("""
            using System;

            class Program
            {
                static int count = 2;
                static readonly int[] Squares = { 0, 1, 4 };
                static readonly int[] Late;

                static Program()
                {
                    Late = new[] { 7, 8 };
                    Console.WriteLine(Squares[count]);
                }

                static void Main() => Console.WriteLine(Late[1] + None.Length);

                static readonly int[] None = [];
            }
            """);

        // an empty array has no global: its value is the empty array, a null data and a length of 0
        Assert.Equal(
        [
            "constant [2 x i32] @Program.Late = [7, 8]",
            "global i32 @Program.count",
            "constant [3 x i32] @Program.Squares = [0, 1, 4]",
        ], Globals(module));
        Assert.Equal("""
            function void Program..cctor()
            IL_0000:
              %0 = const i32 2
              %1 = addr @Program.count
              store i32 [%1], %0
              %2 = addr @Program.Squares
              %3 = const i32 3
              %4 = call ChipSharp.Array ChipSharp.Array.Make(%2, %3)
              %5 = addr @Program.count
              %6 = load i32 [%5]
              call void ChipSharp.Array.CheckIndex(%4, %6)
              %7 = call ptr ChipSharp.Array.Data(%4)
              %8 = elementaddr i32 %7, %6
              %9 = load i32 [%8]
              call void ChipSharp.Runtime.WriteLineInt32(%9)
              return

            """.ReplaceLineEndings("\n"), Function(module, "Program..cctor"));
        Assert.Contains("  %7 = zero ChipSharp.Array\n  %8 = call i32 ChipSharp.Array.Length(%7)\n",
            Function(module, "Program.Main"), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_element_type_is_stored_as_arrays_on_the_stack_store_it()
    {
        var module = Lowered.Module("""
            class Program
            {
                static readonly bool[] Flags = { true, false };
                static readonly float[] Gains = { 1.5f, -0.25f };
                static readonly double[] Steps = { 0.1, -0.0 };
                static readonly nint[] Offsets = { -1, 2 };
                static readonly Color[] Colors = { Color.Blue, Color.Red };
                static Point[] points = new Point[3];
                static readonly Point[] Origins = new Point[2];

                static void Main()
                {
                    points[0].X = Origins[1].X + (Flags[0] ? 1 : 0) + (int)Gains[1] + (int)Steps[0] + (int)Offsets[0]
                        + (int)Colors[0];
                    System.Console.WriteLine(points[0].X);
                }

                struct Point
                {
                    public int X;
                }

                enum Color : short { Red, Blue }
            }
            """);

        // compared in any order
        string[] expected =
        [
            "constant [2 x i8] @Program.Flags = [1, 0]",
            "constant [2 x f32] @Program.Gains = [1.5, -0.25]",
            "constant [2 x f64] @Program.Steps = [0.1, -0]",
            "constant [2 x nint] @Program.Offsets = [-1, 2]",
            "constant [2 x i16] @Program.Colors = [1, 0]",
            "global [3 x Program.Point] @Program.points",
            "constant [2 x Program.Point] @Program.Origins",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), Globals(module).Order(StringComparer.Ordinal));
        Assert.Empty(TirVerifier.FindErrors(module));
    }

    [Fact]
    public void Breaking_the_rule_is_an_error_of_the_translation_too()
    {
        // the program is made here without the whole-program pass, which would have said so first
        var error = Assert.Throws<ChipSharpException>(() => Lowered.Module("""
            class Program
            {
                static int[] table = new int[4];

                static void Main()
                {
                    table = new int[2];
                    System.Console.WriteLine(table.Length);
                }
            }
            """));

        Assert.Equal("Test.dll: Program.Main() at IL_0006: stsfld: an array in a static field that is assigned again, or "
            + "anywhere but in its field initializer or static constructor arrives with M3 (the heap).", error.Message);
    }

    /// <summary>The module's globals, as the printer lists them.</summary>
    private static string[] Globals(TirModule module) =>
    [
        .. TirPrinter.Print(module).Split('\n')
            .Where(line => line.StartsWith("global ", StringComparison.Ordinal)
                || line.StartsWith("constant ", StringComparison.Ordinal)),
    ];

    private static string Function(TirModule module, string name) =>
        TirPrinter.Print(Assert.Single(module.Functions, function => function.Name == name));
}
