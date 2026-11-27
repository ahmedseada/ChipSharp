using ChipSharp.Ir;
using ChipSharp.Ir.Optimizations;
using ChipSharp.Tests.Tir;

namespace ChipSharp.Tests.Narrowing;

/// <summary>
/// The value-range analysis on TIR written by hand: loop counters, the branches taken, widening that stops at the
/// function's constants, array lengths, constant tables, the ranges callers pass and callees return, and what it does
/// not follow (a slot whose address is taken, a function whose address is taken).
/// </summary>
public sealed class TirRangeAnalysisTests
{
    private static readonly TirNarrowingOptions Options = new() { MaxLength = 32256 };

    [Fact]
    public void A_counter_to_7_is_0_to_6_in_the_loop_and_0_to_7_in_its_slot()
    {
        var (module, i, body) = CountTo(7);

        var ranges = TirRangeAnalysis.Run(module, Options);

        Assert.Equal(TirRange.Of(0, 7), ranges.RangeOf(i));
        var load = body.Instructions.OfType<TirLoad>().First();
        Assert.Equal(TirRange.Of(0, 6), ranges.RangeOf(load));
        Assert.Equal(TirRange.Of(1, 7), ranges.RangeOf(body.Instructions.OfType<TirBinary>().Single()));
    }

    [Fact]
    public void A_counter_that_grows_until_a_test_inside_the_loop_stops_at_that_test_s_constant()
    {
        // int c = 1; while (true) { c++; if (c >= 1000) break; } return c;
        var module = new TirModule("Demo");
        var main = AddMain(module, TirType.I32);
        var c = main.AddSlot("c", TirType.I32);
        var (entry, loop, done) = (main.AddBlock("entry"), main.AddBlock("loop"), main.AddBlock("done"));
        var b = new TirBuilder(entry);
        b.Store(c, b.Const(TirType.I32, 1));
        b.Jump(loop);
        b.Block = loop;
        b.Store(c, b.Binary(TirBinaryOp.Add, b.Load(c), b.Const(TirType.I32, 1)));
        b.Branch(b.Compare(TirCompareOp.Ge, b.Load(c), b.Const(TirType.I32, 1000)), done, loop);
        b.Block = done;
        var result = b.Load(c);
        b.Return(result);
        TirVerifier.Verify(module);

        var ranges = TirRangeAnalysis.Run(module, Options);

        Assert.Equal(TirRange.Of(0, 1000), ranges.RangeOf(c)); // a slot holds 0 before its first store
        Assert.Equal(TirRange.Of(1000), ranges.RangeOf(result));
    }

    [Fact]
    public void A_counter_nothing_bounds_wraps_around_and_may_be_any_int()
    {
        // int i = 0; do { i++; } while (Next() != 0); return i;
        var module = new TirModule("Demo");
        var next = module.AddExternalFunction("Runtime.Next", TirType.I32);
        var main = AddMain(module, TirType.I32);
        var i = main.AddSlot("i", TirType.I32);
        var (entry, loop, done) = (main.AddBlock("entry"), main.AddBlock("loop"), main.AddBlock("done"));
        var b = new TirBuilder(entry);
        b.Store(i, b.Const(TirType.I32, 0));
        b.Jump(loop);
        b.Block = loop;
        b.Store(i, b.Binary(TirBinaryOp.Add, b.Load(i), b.Const(TirType.I32, 1)));
        b.Branch(b.Compare(TirCompareOp.Ne, b.Call(next), b.Const(TirType.I32, 0)), loop, done);
        b.Block = done;
        b.Return(b.Load(i));

        Assert.Equal(TirRange.Signed(32), TirRangeAnalysis.Run(module, Options).RangeOf(i));
    }

    [Fact]
    public void A_branch_narrows_a_parameter_and_a_byte_widened_from_a_load_on_each_way()
    {
        // export int F(int n, byte* p): if (n < 2) return n; byte b = *p; if (b < 10) return n + b; return b;
        var module = new TirModule("Demo");
        var f = module.AddFunction("Program.F", TirType.I32, ("n", TirType.I32), ("p", TirType.Ptr));
        f.ExportName = "f";
        var b = new TirBuilder(f.AddBlock("entry"));
        var (small, large, low, high) = (f.AddBlock("small"), f.AddBlock("large"), f.AddBlock("low"), f.AddBlock("high"));
        b.Branch(b.Compare(TirCompareOp.Lt, f.Parameters[0], b.Const(TirType.I32, 2)), small, large);
        b.Block = small;
        var inSmall = b.Binary(TirBinaryOp.Add, f.Parameters[0], b.Const(TirType.I32, 0));
        b.Return(inSmall);
        b.Block = large;
        var widened = b.Convert(b.Load(TirType.I8, f.Parameters[1]), TirType.I32, unsigned: true);
        b.Branch(b.Compare(TirCompareOp.Lt, widened, b.Const(TirType.I32, 10)), low, high);
        b.Block = low;
        var sum = b.Binary(TirBinaryOp.Add, f.Parameters[0], b.Const(TirType.I32, 0));
        b.Return(sum);
        b.Block = high;
        b.Return(b.Const(TirType.I32, 0));
        TirVerifier.Verify(TirSamples.WithMain(module));

        var ranges = TirRangeAnalysis.Run(module, Options);

        Assert.Equal(TirRange.Signed(32), ranges.RangeOf(f.Parameters[0]));
        Assert.Equal(TirRange.Of(int.MinValue, 1), ranges.RangeOf(inSmall));
        Assert.Equal(TirRange.Of(2, int.MaxValue), ranges.RangeOf(sum));
        Assert.Equal(TirRange.Of(0, 255), ranges.RangeOf(widened));
    }

    [Fact]
    public void An_unsigned_check_against_a_length_puts_the_index_between_0_and_the_length_less_one()
    {
        // if ((uint)i < (uint)array.length) ... i; with the length a field the options name
        var array = new TirStructType("Array", ("data", TirType.Ptr), ("length", TirType.I32));
        var options = Options with { LengthFields = [(array, 1)] };
        var module = new TirModule("Demo");
        var f = module.AddFunction("Program.F", TirType.I32, ("a", TirType.Ptr), ("i", TirType.I32));
        f.ExportName = "f";
        var (entry, inside, outside) = (f.AddBlock("entry"), f.AddBlock("inside"), f.AddBlock("outside"));
        var b = new TirBuilder(entry);
        var length = b.Load(TirType.I32, b.FieldAddress(f.Parameters[0], array, 1));
        b.Branch(b.Compare(TirCompareOp.LtUnsigned, f.Parameters[1], length), inside, outside);
        b.Block = inside;
        var index = b.Binary(TirBinaryOp.Add, f.Parameters[1], b.Const(TirType.I32, 0));
        b.Return(index);
        b.Block = outside;
        b.Return(b.Const(TirType.I32, -1));

        var ranges = TirRangeAnalysis.Run(module, options);

        Assert.Equal(TirRange.Of(0, 32256), ranges.RangeOf(length));
        Assert.Equal(TirRange.Of(0, 32255), ranges.RangeOf(index));
        Assert.Equal(TirRange.Signed(32), TirRangeAnalysis.Run(module, Options).RangeOf(length)); // a field like any other
    }

    [Fact]
    public void An_element_of_a_constant_table_is_one_of_its_values()
    {
        var module = new TirModule("Demo");
        var table = module.AddArray("Program.Digits", new TirArrayType(TirType.I8, 3),
            [new TirConst(TirType.I8, 63), new TirConst(TirType.I8, 6), new TirConst(TirType.I8, 91)], isConstant: true);
        var f = module.AddFunction("Program.Digit", TirType.I8, ("i", TirType.I32));
        f.ExportName = "digit";
        var b = new TirBuilder(f.AddBlock("entry"));
        var digit = b.Load(TirType.I8, b.ElementAddress(b.AddressOf(table), TirType.I8, f.Parameters[0]));
        b.Return(digit);

        Assert.Equal(TirRange.Of(6, 91), TirRangeAnalysis.Run(module, Options).RangeOf(digit));
    }

    [Fact]
    public void A_function_only_the_program_calls_gets_the_ranges_its_callers_pass_and_they_get_what_it_returns()
    {
        var (module, twice, calls) = CallTwice(takeAddress: false);

        var ranges = TirRangeAnalysis.Run(module, Options);

        Assert.Equal(TirRange.Of(3, 10), ranges.RangeOf(twice.Parameters[0]));
        Assert.All(calls, call => Assert.Equal(TirRange.Of(6, 20), ranges.RangeOf(call)));
    }

    [Fact]
    public void A_function_whose_address_is_taken_may_be_called_with_anything()
    {
        var (module, twice, calls) = CallTwice(takeAddress: true);

        var ranges = TirRangeAnalysis.Run(module, Options);

        Assert.Equal(TirRange.Signed(32), ranges.RangeOf(twice.Parameters[0]));
        Assert.All(calls, call => Assert.Equal(TirRange.Signed(32), ranges.RangeOf(call)));
    }

    [Fact]
    public void A_slot_whose_address_is_taken_is_not_followed()
    {
        var module = new TirModule("Demo");
        var write = module.AddExternalFunction("Runtime.Write", TirType.Void, ("p", TirType.Ptr));
        var main = AddMain(module, TirType.I32);
        var x = main.AddSlot("x", TirType.I32);
        var b = new TirBuilder(main.AddBlock("entry"));
        b.Store(x, b.Const(TirType.I32, 5));
        b.Call(write, b.AddressOf(x));
        var loaded = b.Load(x);
        b.Return(loaded);

        var ranges = TirRangeAnalysis.Run(module, Options);

        Assert.False(ranges.IsTracked(x));
        Assert.Equal(TirRange.Signed(32), ranges.RangeOf(loaded));
    }

    [Fact]
    public void A_way_that_cannot_be_taken_leaves_its_block_unreached_and_its_values_any_value()
    {
        // int i = 3; if (i > 5) return i * 2 (never); return i;
        var module = new TirModule("Demo");
        var main = AddMain(module, TirType.I32);
        var i = main.AddSlot("i", TirType.I32);
        var (entry, never, always) = (main.AddBlock("entry"), main.AddBlock("never"), main.AddBlock("always"));
        var b = new TirBuilder(entry);
        b.Store(i, b.Const(TirType.I32, 3));
        b.Branch(b.Compare(TirCompareOp.Gt, b.Load(i), b.Const(TirType.I32, 5)), never, always);
        b.Block = never;
        var doubled = b.Binary(TirBinaryOp.Mul, b.Load(i), b.Const(TirType.I32, 2));
        b.Return(doubled);
        b.Block = always;
        b.Return(b.Load(i));

        var ranges = TirRangeAnalysis.Run(module, Options);

        Assert.True(ranges.IsReached(always));
        Assert.False(ranges.IsReached(never));
        Assert.Equal(TirRange.Signed(32), ranges.RangeOf(doubled));
    }

    [Fact]
    public void Nint_has_no_range_as_only_the_target_knows_its_width()
    {
        var module = new TirModule("Demo");
        var main = AddMain(module, TirType.I32);
        var b = new TirBuilder(main.AddBlock("entry"));
        var n = b.Convert(b.Const(TirType.I32, -1), TirType.NativeInt, unsigned: true);
        var back = b.Convert(n, TirType.I32);
        b.Return(back);

        var ranges = TirRangeAnalysis.Run(module, Options);

        Assert.Equal(TirRange.Signed(64), ranges.RangeOf(n));
        Assert.Equal(TirRange.Signed(32), ranges.RangeOf(back));
    }

    /// <summary><c>for (int i = 0; i &lt; count; i++) Print(i);</c> in Main.</summary>
    internal static (TirModule Module, TirSlot I, TirBlock Body) CountTo(int count)
    {
        var module = new TirModule("Demo");
        var print = module.AddExternalFunction("Runtime.Print", TirType.Void, ("value", TirType.I32));
        var main = AddMain(module, TirType.Void);
        var i = main.AddSlot("i", TirType.I32);
        var (entry, test, body, done) = (main.AddBlock("entry"), main.AddBlock("test"), main.AddBlock("body"), main.AddBlock("done"));
        var b = new TirBuilder(entry);
        b.Store(i, b.Const(TirType.I32, 0));
        b.Jump(test);
        b.Block = test;
        b.Branch(b.Compare(TirCompareOp.Lt, b.Load(i), b.Const(TirType.I32, count)), body, done);
        b.Block = body;
        var value = b.Load(i);
        b.Call(print, value);
        b.Store(i, b.Binary(TirBinaryOp.Add, value, b.Const(TirType.I32, 1)));
        b.Jump(test);
        b.Block = done;
        b.Return();
        TirVerifier.Verify(module);
        return (module, i, body);
    }

    /// <summary>Main calls Twice(3) and Twice(10) (and takes its address, when asked).</summary>
    private static (TirModule Module, TirFunction Twice, TirCall[] Calls) CallTwice(bool takeAddress)
    {
        var module = new TirModule("Demo");
        var twice = module.AddFunction("Program.Twice", TirType.I32, ("x", TirType.I32));
        var t = new TirBuilder(twice.AddBlock("entry"));
        t.Return(t.Binary(TirBinaryOp.Mul, twice.Parameters[0], t.Const(TirType.I32, 2)));
        var main = AddMain(module, TirType.I32);
        var b = new TirBuilder(main.AddBlock("entry"));
        if (takeAddress)
        {
            b.AddressOf(twice);
        }

        var calls = new[] { b.Call(twice, b.Const(TirType.I32, 3)), b.Call(twice, b.Const(TirType.I32, 10)) };
        b.Return(b.Binary(TirBinaryOp.Add, calls[0], calls[1]));
        TirVerifier.Verify(module);
        return (module, twice, calls);
    }

    private static TirFunction AddMain(TirModule module, TirType returnType)
    {
        var main = module.AddFunction("Program.Main", returnType);
        module.EntryPoint = main;
        return main;
    }
}
