using ChipSharp.Ir;
using ChipSharp.Ir.Optimizations;
using ChipSharp.Tests.Tir;

namespace ChipSharp.Tests.Narrowing;

/// <summary>
/// The rewrite on TIR written by hand: which operations become 8 or 16 bits wide (or 32 for a long), which stay, and that
/// what comes out verifies. Each case is an edge of C#'s semantics: 255 + 1, -128 / -1, shifts by the width or more,
/// unsigned comparisons of negative values, equality of a byte with an sbyte.
/// </summary>
public sealed class TirNarrowingTests
{
    private static readonly TirNarrowingOptions Options = new() { MaxLength = 32256 };

    [Fact]
    public void A_counter_to_7_becomes_a_byte_counted_and_compared_in_8_bits_and_widened_where_it_is_passed()
    {
        var (module, _, _) = TirRangeAnalysisTests.CountTo(7);

        var main = Narrow(module).Functions.Single(f => f.Name == "Program.Main");

        Assert.Equal(Text("""
            function void Program.Main()
              $i: i8
            entry:
              %0 = const i8 0
              store $i, %0
              jump test
            test:
              %1 = load i8 $i
              %2 = const i8 7
              %3 = ult i8 %1, %2
              branch %3, body, done
            body:
              %4 = load i8 $i
              %5 = uconvert i8 %4 to i32
              call void Runtime.Print(%5)
              %6 = const i8 1
              %7 = add i8 %4, %6
              store $i, %7
              jump test
            done:
              return

            """), TirPrinter.Print(main));
    }

    [Fact]
    public void A_byte_plus_one_is_done_in_16_bits_as_256_does_not_fit_8()
    {
        // static int F(byte b) => b + 1;
        var (module, f, b) = Function(TirType.I32, ("b", TirType.I8));
        b.Return(b.Binary(TirBinaryOp.Add, b.Convert(f.Parameters[0], TirType.I32, unsigned: true), b.Const(TirType.I32, 1)));

        Assert.Equal(Text("""
            function i32 Program.F(i8 %b) export f
            entry:
              %0 = uconvert i8 %b to i16
              %1 = const i16 1
              %2 = add i16 %0, %1
              %3 = uconvert i16 %2 to i32
              return %3

            """), TirPrinter.Print(Narrow(module).Functions.Single(fn => fn.Name == "Program.F")));
    }

    [Fact]
    public void A_division_that_may_be_the_smallest_value_by_minus_one_is_not_done_in_8_bits()
    {
        // static int F(sbyte a, sbyte b) => a / b;   (-128 / -1 is 128, which no sbyte holds)
        var (module, f, b) = Function(TirType.I32, ("a", TirType.I8), ("b", TirType.I8));
        b.Return(b.Binary(TirBinaryOp.Div, b.Convert(f.Parameters[0], TirType.I32), b.Convert(f.Parameters[1], TirType.I32)));

        var text = TirPrinter.Print(Narrow(module));

        Assert.Contains("= div i16 ", text);
        Assert.DoesNotContain(" i8 %0, ", text.Split("div")[1].Split('\n')[0]);
    }

    [Fact]
    public void Division_and_remainder_of_values_that_are_never_negative_are_unsigned_and_8_bits()
    {
        // static int F(byte a, byte b) => a / (b | 1) + a % 10;
        var (module, f, b) = Function(TirType.I32, ("a", TirType.I8), ("b", TirType.I8));
        var a = b.Convert(f.Parameters[0], TirType.I32, unsigned: true);
        var divisor = b.Binary(TirBinaryOp.Or, b.Convert(f.Parameters[1], TirType.I32, unsigned: true), b.Const(TirType.I32, 1));
        b.Return(b.Binary(TirBinaryOp.Add, b.Binary(TirBinaryOp.Div, a, divisor), b.Binary(TirBinaryOp.Rem, a, b.Const(TirType.I32, 10))));

        var text = TirPrinter.Print(Narrow(module));

        Assert.Contains("= udiv i8 ", text);
        Assert.Contains("= urem i8 ", text);
        Assert.Contains("= add i16 ", text); // up to 255 + 9
    }

    [Fact]
    public void A_shift_by_an_amount_that_may_reach_8_is_done_in_16_bits_and_one_that_may_overflow_stays()
    {
        // static int F(byte v, int s) => (v >> (s & 15)) + (1 << (s & 31));
        var (module, f, b) = Function(TirType.I32, ("v", TirType.I8), ("s", TirType.I32));
        var value = b.Convert(f.Parameters[0], TirType.I32, unsigned: true);
        var right = b.Binary(TirBinaryOp.Shr, value, b.Binary(TirBinaryOp.And, f.Parameters[1], b.Const(TirType.I32, 15)));
        var left = b.Binary(TirBinaryOp.Shl, b.Const(TirType.I32, 1), b.Binary(TirBinaryOp.And, f.Parameters[1], b.Const(TirType.I32, 31)));
        b.Return(b.Binary(TirBinaryOp.Add, right, left));

        var text = TirPrinter.Print(Narrow(module));

        Assert.Contains("= ushr i16 ", text); // a value that is never negative: the same as shr
        Assert.Contains("= shl i32 ", text);  // 1 << 31 needs all 32 bits
        Assert.DoesNotContain("shr i8", text);
    }

    [Fact]
    public void An_unsigned_comparison_of_an_sbyte_is_done_where_sign_extension_keeps_the_unsigned_order()
    {
        // static bool F(sbyte x) => (uint)x < 200u;   (-1 is 4294967295, not below 200)
        var (module, f, b) = Function(TirType.Bool, ("x", TirType.I8));
        b.Return(b.Compare(TirCompareOp.LtUnsigned, b.Convert(f.Parameters[0], TirType.I32), b.Const(TirType.I32, 200)));

        var text = TirPrinter.Print(Narrow(module));

        Assert.Contains("= convert i8 %x to i16", text);
        Assert.Contains("= ult i16 ", text);
    }

    [Fact]
    public void A_byte_and_an_sbyte_are_compared_for_equality_in_16_bits_where_255_and_minus_1_differ()
    {
        // static bool F(byte a, sbyte b) => a == b;
        var (module, f, b) = Function(TirType.Bool, ("a", TirType.I8), ("b", TirType.I8));
        b.Return(b.Compare(TirCompareOp.Eq, b.Convert(f.Parameters[0], TirType.I32, unsigned: true),
            b.Convert(f.Parameters[1], TirType.I32)));

        var text = TirPrinter.Print(Narrow(module));

        Assert.Contains("= eq i16 ", text);
        Assert.Contains("= uconvert i8 %a to i16", text);
        Assert.Contains("= convert i8 %b to i16", text);
    }

    [Fact]
    public void A_signed_comparison_of_two_bytes_is_the_unsigned_one_in_8_bits()
    {
        // static bool F(byte a, byte b) => a < b;
        var (module, f, b) = Function(TirType.Bool, ("a", TirType.I8), ("b", TirType.I8));
        b.Return(b.Compare(TirCompareOp.Lt, b.Convert(f.Parameters[0], TirType.I32, unsigned: true),
            b.Convert(f.Parameters[1], TirType.I32, unsigned: true)));

        Assert.Contains("%0 = ult i8 %a, %b", TirPrinter.Print(Narrow(module)));
    }

    [Fact]
    public void A_long_that_fits_an_int_is_multiplied_in_32_bits()
    {
        // static long F(byte b) => (long)b * 1000;
        var (module, f, b) = Function(TirType.I64, ("b", TirType.I8));
        b.Return(b.Binary(TirBinaryOp.Mul, b.Convert(f.Parameters[0], TirType.I64, unsigned: true), b.Const(TirType.I64, 1000)));

        var text = TirPrinter.Print(Narrow(module));

        Assert.Contains("= mul i32 ", text);
        Assert.Contains("= uconvert i32 %2 to i64", text);
    }

    [Fact]
    public void An_addition_to_a_value_from_elsewhere_stays_as_wide_and_a_comparison_of_its_low_bits_is_not_made()
    {
        // static int F(int n) => n < 10 ? n + 1 : 0, called only with 0 to 9: LLVM does not know that range, so cutting n
        // would only hide it
        var module = new TirModule("Demo");
        var f = module.AddFunction("Program.F", TirType.I32, ("n", TirType.I32));
        var (entry, then, otherwise) = (f.AddBlock("entry"), f.AddBlock("then"), f.AddBlock("otherwise"));
        var b = new TirBuilder(entry);
        b.Branch(b.Compare(TirCompareOp.Lt, f.Parameters[0], b.Const(TirType.I32, 10)), then, otherwise);
        b.Block = then;
        b.Return(b.Binary(TirBinaryOp.Add, f.Parameters[0], b.Const(TirType.I32, 1)));
        b.Block = otherwise;
        b.Return(b.Const(TirType.I32, 0));
        var main = module.AddFunction("Program.Main", TirType.I32);
        module.EntryPoint = main;
        var m = new TirBuilder(main.AddBlock("entry"));
        m.Return(m.Call(f, m.Const(TirType.I32, 9)));
        var before = TirPrinter.Print(module);

        Assert.Equal(before, TirPrinter.Print(Narrow(module)));
    }

    [Fact]
    public void A_module_with_nothing_to_narrow_is_printed_as_it_was()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        TirSamples.AddMax(module);
        TirSamples.AddSum(module);
        var before = TirPrinter.Print(module);

        Assert.Equal(before, TirPrinter.Print(Narrow(module)));
    }

    /// <summary>Narrows the module, which must verify after.</summary>
    private static TirModule Narrow(TirModule module)
    {
        TirVerifier.Verify(module);
        TirNarrowing.Run(module, Options);
        TirVerifier.Verify(module);
        return module;
    }

    /// <summary>An exported function Program.F (so its parameters may be anything) and a builder at its entry; the module has a Main.</summary>
    private static (TirModule Module, TirFunction Function, TirBuilder Builder) Function(TirType returnType,
        params (string Name, TirType Type)[] parameters)
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var f = module.AddFunction("Program.F", returnType, parameters);
        f.ExportName = "f";
        return (module, f, new TirBuilder(f.AddBlock("entry")));
    }

    private static string Text(string text) => text.ReplaceLineEndings("\n");
}
