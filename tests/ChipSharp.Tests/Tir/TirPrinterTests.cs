using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

public sealed class TirPrinterTests
{
    [Fact]
    public void Prints_square_and_a_main_that_calls_it_and_an_external_print()
    {
        var module = new TirModule("Demo");
        var square = module.AddFunction("Program.Square", TirType.I32, ("n", TirType.I32));
        var main = module.AddFunction("Program.Main", TirType.Void);
        var print = module.AddExternalFunction("Runtime.Print", TirType.Void, ("value", TirType.I32));
        module.EntryPoint = main;

        var b = new TirBuilder(square.AddBlock("entry"));
        var n = square.Parameters[0];
        b.Return(b.Binary(TirBinaryOp.Mul, n, n));

        b = new TirBuilder(main.AddBlock("entry"));
        var result = b.Call(square, b.Const(TirType.I32, 42));
        b.Call(print, result);
        b.Return();

        TirVerifier.Verify(module);
        Assert.Equal(Text("""
            module Demo
            entrypoint Program.Main

            function i32 Program.Square(i32 %n)
            entry:
              %0 = mul i32 %n, %n
              return %0

            function void Program.Main()
            entry:
              %0 = const i32 42
              %1 = call i32 Program.Square(%0)
              call void Runtime.Print(%1)
              return

            external function void Runtime.Print(i32 %value)

            """), TirPrinter.Print(module));
    }

    [Fact]
    public void Prints_slots_and_an_if_else()
    {
        var module = new TirModule("Demo");
        var max = TirSamples.AddMax(module);

        Assert.Equal(Text("""
            function i32 Program.Max(i32 %a, i32 %b)
              $result: i32
            entry:
              %0 = gt i32 %a, %b
              branch %0, then, else
            then:
              store $result, %a
              jump join
            else:
              store $result, %b
              jump join
            join:
              %1 = load i32 $result
              return %1

            """), TirPrinter.Print(max));
    }

    [Fact]
    public void Numbers_temporaries_across_the_blocks_of_a_loop()
    {
        var module = new TirModule("Demo");
        var sum = TirSamples.AddSum(module);

        Assert.Equal(Text("""
            function i32 Program.Sum(i32 %n)
              $i: i32
              $sum: i32
            entry:
              %0 = const i32 0
              store $i, %0
              store $sum, %0
              jump test
            test:
              %1 = load i32 $i
              %2 = lt i32 %1, %n
              branch %2, body, done
            body:
              %3 = load i32 $sum
              %4 = load i32 $i
              %5 = add i32 %3, %4
              store $sum, %5
              %6 = const i32 1
              %7 = add i32 %4, %6
              store $i, %7
              jump test
            done:
              %8 = load i32 $sum
              return %8

            """), TirPrinter.Print(sum));
    }

    [Fact]
    public void Prints_nint_and_conversions()
    {
        var module = new TirModule("Demo");
        var mix = module.AddFunction("Program.Mix", TirType.NativeInt,
            ("x", TirType.I32), ("u", TirType.I32), ("d", TirType.F64), ("p", TirType.Bool));
        var (x, u, d, p) = (mix.Parameters[0], mix.Parameters[1], mix.Parameters[2], mix.Parameters[3]);
        var b = new TirBuilder(mix.AddBlock("entry"));
        var signed = b.Convert(x, TirType.NativeInt);
        var zeroExtended = b.Convert(u, TirType.NativeInt, unsigned: true);
        b.Convert(d, TirType.I16);
        b.Convert(u, TirType.F64, unsigned: true);
        b.Convert(p, TirType.I8);
        b.Convert(signed, TirType.I64);
        var sum = b.Binary(TirBinaryOp.Add, signed, zeroExtended);
        b.Return(b.Binary(TirBinaryOp.Sub, sum, b.Const(TirType.NativeInt, -1)));

        Assert.Equal(Text("""
            function nint Program.Mix(i32 %x, i32 %u, f64 %d, bool %p)
            entry:
              %0 = convert i32 %x to nint
              %1 = uconvert i32 %u to nint
              %2 = convert f64 %d to i16
              %3 = uconvert i32 %u to f64
              %4 = convert bool %p to i8
              %5 = convert nint %0 to i64
              %6 = add nint %0, %1
              %7 = const nint -1
              %8 = sub nint %6, %7
              return %8

            """), TirPrinter.Print(mix));
        Assert.Empty(TirVerifier.FindErrors(TirSamples.WithMain(module)));
    }

    [Fact]
    public void Prints_constants_of_every_type()
    {
        var module = new TirModule("Demo");
        var f = module.AddFunction("Program.Constants", TirType.Void);
        var b = new TirBuilder(f.AddBlock("entry"));

        Assert.Equal("%0 = const bool true", b.Const(true).ToString());
        Assert.Equal("%1 = const bool false", b.Const(TirType.Bool, 0).ToString());
        Assert.Equal("%2 = const i8 -1", b.Const(TirType.I8, 255).ToString());
        Assert.Equal("%3 = const i16 -32768", b.Const(TirType.I16, short.MinValue).ToString());
        Assert.Equal("%4 = const i32 -1", b.Const(TirType.I32, uint.MaxValue).ToString());
        Assert.Equal("%5 = const i64 -9223372036854775808", b.Const(TirType.I64, long.MinValue).ToString());
        Assert.Equal("%6 = const nint 2147483647", b.Const(TirType.NativeInt, int.MaxValue).ToString());
        Assert.Equal("%7 = const f32 0.1", b.Const(TirType.F32, 0.1).ToString());
        Assert.Equal("%8 = const f64 1.5", b.Const(TirType.F64, 1.5).ToString());
        Assert.Equal("%9 = const f64 2", b.Const(TirType.F64, 2).ToString());
    }

    [Theory]
    [InlineData(TirBinaryOp.Add, "add")]
    [InlineData(TirBinaryOp.Sub, "sub")]
    [InlineData(TirBinaryOp.Mul, "mul")]
    [InlineData(TirBinaryOp.Div, "div")]
    [InlineData(TirBinaryOp.DivUnsigned, "udiv")]
    [InlineData(TirBinaryOp.Rem, "rem")]
    [InlineData(TirBinaryOp.RemUnsigned, "urem")]
    [InlineData(TirBinaryOp.And, "and")]
    [InlineData(TirBinaryOp.Or, "or")]
    [InlineData(TirBinaryOp.Xor, "xor")]
    [InlineData(TirBinaryOp.Shl, "shl")]
    [InlineData(TirBinaryOp.Shr, "shr")]
    [InlineData(TirBinaryOp.ShrUnsigned, "ushr")]
    public void Prints_each_binary_operation(TirBinaryOp op, string name)
    {
        var f = new TirModule("Demo").AddFunction("Program.F", TirType.I64, ("a", TirType.I64), ("b", TirType.I64));
        var b = new TirBuilder(f.AddBlock("entry"));

        Assert.Equal($"%0 = {name} i64 %a, %b", b.Binary(op, f.Parameters[0], f.Parameters[1]).ToString());
    }

    [Theory]
    [InlineData(TirCompareOp.Eq, "eq")]
    [InlineData(TirCompareOp.Ne, "ne")]
    [InlineData(TirCompareOp.Lt, "lt")]
    [InlineData(TirCompareOp.Le, "le")]
    [InlineData(TirCompareOp.Gt, "gt")]
    [InlineData(TirCompareOp.Ge, "ge")]
    [InlineData(TirCompareOp.LtUnsigned, "ult")]
    [InlineData(TirCompareOp.LeUnsigned, "ule")]
    [InlineData(TirCompareOp.GtUnsigned, "ugt")]
    [InlineData(TirCompareOp.GeUnsigned, "uge")]
    public void Prints_each_comparison(TirCompareOp op, string name)
    {
        var f = new TirModule("Demo").AddFunction("Program.F", TirType.Bool, ("a", TirType.I16), ("b", TirType.I16));
        var b = new TirBuilder(f.AddBlock("entry"));

        Assert.Equal($"%0 = {name} i16 %a, %b", b.Compare(op, f.Parameters[0], f.Parameters[1]).ToString());
    }

    [Fact]
    public void A_value_outside_the_function_prints_as_a_question_mark()
    {
        var loose = new TirConst(TirType.I32, 7);
        var f = new TirModule("Demo").AddFunction("Program.F", TirType.I32);
        var b = new TirBuilder(f.AddBlock("entry"));

        Assert.Equal("%? = const i32 7", TirPrinter.Print(loose));
        Assert.Equal("%0 = add i32 %?, %?", b.Binary(TirBinaryOp.Add, loose, loose).ToString());
    }

    /// <summary>The expected text with "\n" line endings, whatever the test file was checked out with.</summary>
    private static string Text(string text) => text.ReplaceLineEndings("\n");
}
