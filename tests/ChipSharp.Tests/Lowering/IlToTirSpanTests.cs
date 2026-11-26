using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend;
using ChipSharp.Ir;
using ChipSharp.Tests.Llvm;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// The TIR that spans become (see Spans): a span is an array value, <c>ChipSharp.Array</c> ({ ptr, i32 }), in locals,
/// parameters and return values; its indexer is the array's bounds check and elementaddr; a stackalloc is a slot of
/// the method; constant data is a constant global with its elements; and MemoryMarshal reads and writes unaligned.
/// </summary>
public sealed class IlToTirSpanTests
{
    [Fact]
    public void A_span_is_an_array_value_and_its_indexer_checks_the_index_like_an_array()
    {
        var module = Lowered.Module("""
            using System;

            class Program
            {
                static int At(ReadOnlySpan<int> values, int index) => values[index];

                static void Main()
                {
                    int[] array = { 1, 2, 3 };
                    Console.WriteLine(At(array, 1));
                }
            }
            """);

        var at = Assert.Single(module.Functions, function => function.Name == "Program.At");
        Assert.Equal([StackArrays.ArrayType, TirType.I32], at.Parameters.Select(parameter => parameter.Type));
        Assert.Equal("""
            function i32 Program.At(ChipSharp.Array %values, i32 %index)
              $values: ChipSharp.Array
            IL_0000:
              store $values, %values
              %0 = addr $values
              %1 = load ChipSharp.Array [%0]
              call void ChipSharp.Array.CheckIndex(%1, %index)
              %2 = call ptr ChipSharp.Array.Data(%1)
              %3 = elementaddr i32 %2, %index
              %4 = load i32 [%3]
              return %4

            """.ReplaceLineEndings("\n"), TirPrinter.Print(at));
    }

    [Fact]
    public void Constant_data_is_a_constant_global_with_its_elements_and_a_span_of_it_is_its_address()
    {
        var module = Lowered.Module("""
            using System;

            class Program
            {
                static ReadOnlySpan<byte> Digits => new byte[] { 0x3F, 0x06, 0x5B };
                static ReadOnlySpan<int> Steps => new int[] { 10, -20, 30 };
                static ReadOnlySpan<double> Halves => new double[] { 0.5, -1.5 };

                static void Main() => Console.WriteLine(Digits[1] + Steps[2] + Halves[0]);
            }
            """);

        Assert.Equal(["constant [3 x i8] = [63, 6, 91]", "constant [3 x i32] = [10, -20, 30]", "constant [2 x f64] = [0.5, -1.5]"],
            module.Globals.Select(global => $"{(global.IsConstant ? "constant" : "global")} {global.Type} = " +
                $"[{string.Join(", ", global.Elements!.Select(Value))}]"));
        Assert.All(module.Globals, global => Assert.StartsWith("<PrivateImplementationDetails>.", global.Name));
        Assert.Contains("call ChipSharp.Array ChipSharp.Array.Make(", Function(module, "Program.get_Steps"));
        Assert.Contains("call ChipSharp.Array ChipSharp.Span.FromPointer(", Function(module, "Program.get_Digits"));
    }

    [Fact]
    public void A_stackalloc_is_a_slot_of_its_elements_zeroed_where_it_runs()
    {
        var main = Function(Lowered.Module("""
            using System;

            struct Pair
            {
                public int A;
                public long B;
            }

            class Program
            {
                static void Main()
                {
                    Span<int> numbers = stackalloc int[] { 4, 5 };
                    Span<Pair> pairs = stackalloc Pair[3];
                    Console.WriteLine(numbers[1] + pairs[2].B);
                }
            }
            """), "Program.Main");

        Assert.Contains("  $stackalloc_IL_0002: [2 x i32]\n", main);
        Assert.Contains("  $stackalloc_IL_001c: [3 x Pair]\n", main);
        Assert.Contains("  %10 = zero [3 x Pair]\n  store $stackalloc_IL_001c, %10\n", main);
    }

    [Fact]
    public void MemoryMarshal_reads_and_writes_unaligned_which_LLVM_reads_at_any_byte()
    {
        var module = Lowered.Module("""
            using System;
            using System.Runtime.InteropServices;

            class Program
            {
                static void Main()
                {
                    Span<byte> bytes = stackalloc byte[8];
                    MemoryMarshal.Write(bytes.Slice(1), 0x01020304);
                    Console.WriteLine(MemoryMarshal.Read<int>(bytes.Slice(1)));
                }
            }
            """);

        Assert.Contains("load unaligned i32 [", Function(module, "ChipSharp.Span.Read<i32>"));
        Assert.Contains("store unaligned i32 [", Function(module, "ChipSharp.Span.Write<i32>"));
        var llvm = Assert.Single(new LlvmCodeGenerator().Generate(module, LlvmTargets.Host).Files).Content;
        Assert.Matches(@"= load i32, ptr %t\d+, align 1\n", llvm);
        Assert.Matches(@"store i32 %t?[\w.]+, ptr %t\d+, align 1\n", llvm);
    }

    [Fact]
    public void A_cast_checks_its_start_only_on_a_target_without_unaligned_access()
    {
        const string source = """
            using System;
            using System.Runtime.InteropServices;

            class Program
            {
                static void Main()
                {
                    Span<byte> bytes = stackalloc byte[8];
                    Console.WriteLine(MemoryMarshal.Cast<byte, int>(bytes).Length + MemoryMarshal.Cast<byte, byte>(bytes).Length);
                }
            }
            """;
        var program = Lowered.Program(Lowered.Compile(source));

        // the alignment each Cast passes (its last argument, a constant): 1 checks nothing
        string[] Alignments(bool unalignedAccess)
        {
            var lines = Function(Lowered.Lower(new IlProgram(program.Assembly, program.Methods)
            {
                UnalignedAccess = unalignedAccess,
            }), "Program.Main").Split('\n');
            return
            [
                .. lines.Where(line => line.Contains("ChipSharp.Span.Cast(", StringComparison.Ordinal))
                    .Select(line => line[(line.LastIndexOf(", ", StringComparison.Ordinal) + 2)..].TrimEnd(')'))
                    .Select(name => lines.Single(line => line.TrimStart().StartsWith(name + " = ", StringComparison.Ordinal))
                        .Split(' ')[^1]),
            ];
        }

        // an int needs 4 on the Pico, a byte nothing; the PC reads an int anywhere
        Assert.Equal(["4", "1"], Alignments(unalignedAccess: false));
        Assert.Equal(["1", "1"], Alignments(unalignedAccess: true));
    }

    private static string Function(TirModule module, string name) =>
        TirPrinter.Print(Assert.Single(module.Functions, function => function.Name == name));

    private static string Value(TirConst constant) => constant.Type.IsFloat
        ? constant.FloatValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        : constant.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
