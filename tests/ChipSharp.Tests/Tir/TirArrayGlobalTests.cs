using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

/// <summary>
/// Global arrays (<see cref="TirModule.AddArray"/>), as C#'s arrays in static fields become: a variable or a constant
/// table, starting with an element each or at zero; what building one refuses, how it prints, and that nothing may store
/// into a constant one.
/// </summary>
public sealed class TirArrayGlobalTests
{
    private readonly TirModule _module = new("Test");

    [Fact]
    public void A_global_array_starts_with_its_elements_or_at_zero_and_a_table_is_constant()
    {
        var digits = _module.AddArray("Program.Digits", new TirArrayType(TirType.I8, 3),
            [new TirConst(TirType.I8, 63), new TirConst(TirType.I8, 6), new TirConst(TirType.I8, 91)], isConstant: true);
        var gains = _module.AddArray("Program.Gains", new TirArrayType(TirType.F32, 2),
            [new TirConst(TirType.F32, 1.5), new TirConst(TirType.F32, -0.0)], isConstant: true);
        var counts = _module.AddArray("Program.counts", new TirArrayType(TirType.I32, 2),
            [new TirConst(TirType.I32, 1), new TirConst(TirType.I32, 2)], isConstant: false);
        var buffer = _module.AddArray("Program.buffer", new TirArrayType(TirType.I64, 16), null, isConstant: false);
        var origins = _module.AddArray("Program.Origins",
            new TirArrayType(new TirStructType("Program.Point", ("X", TirType.I32), ("Y", TirType.I32)), 2), null,
            isConstant: true);

        Assert.True(digits.IsConstant);
        Assert.Null(digits.Data);
        Assert.Equal([63L, 6, 91], digits.Elements!.Select(element => element.IntegerValue));
        Assert.False(counts.IsConstant);
        Assert.Null(buffer.Elements);
        Assert.Null(buffer.InitialValue);
        Assert.Equal("""
            module Test

            struct Program.Point { i32 X, i32 Y }
            constant [3 x i8] @Program.Digits = [63, 6, 91]
            constant [2 x f32] @Program.Gains = [1.5, -0]
            global [2 x i32] @Program.counts = [1, 2]
            global [16 x i64] @Program.buffer
            constant [2 x Program.Point] @Program.Origins

            """.ReplaceLineEndings("\n"), TirPrinter.Print(_module));
        Assert.Equal([digits, gains, counts, buffer, origins], _module.Globals);
    }

    [Fact]
    public void A_global_array_needs_one_constant_of_its_element_type_per_element_that_is_in_no_block()
    {
        var type = new TirArrayType(TirType.I32, 2);
        var function = _module.AddFunction("Program.F", TirType.Void);
        var inBlock = new TirBuilder(function.AddBlock("entry")).Const(TirType.I32, 1);

        Assert.Equal("Module 'Test': global array @Program.a is [2 x i32], but it is given 1 elements.",
            Assert.Throws<TirException>(() => _module.AddArray("Program.a", type, [new TirConst(TirType.I32, 1)], true))
                .Message);
        Assert.Equal("Module 'Test': global array @Program.a holds i32, but one of its elements is i64.",
            Assert.Throws<TirException>(() => _module.AddArray("Program.a", type,
                [new TirConst(TirType.I32, 1), new TirConst(TirType.I64, 2)], false)).Message);
        Assert.Equal("Module 'Test': an element of global array @Program.a, '%0 = const i32 1', is an instruction of block "
            + "'entry' of function 'Program.F'; give constants that are in no block.",
            Assert.Throws<TirException>(() => _module.AddArray("Program.a", type, [inBlock, new TirConst(TirType.I32, 2)],
                false)).Message);
        _module.AddGlobal("Program.a", TirType.I32);
        Assert.Equal("Module 'Test' already has a global named @Program.a.",
            Assert.Throws<TirException>(() => _module.AddArray("Program.a", type, null, false)).Message);
    }

    [Fact]
    public void A_constant_array_can_be_read_but_a_store_into_it_fails_and_a_variable_one_takes_stores()
    {
        var type = new TirArrayType(TirType.I32, 4);
        var table = _module.AddArray("Program.Table", type, null, isConstant: true);
        var buffer = _module.AddArray("Program.buffer", type, null, isConstant: false);
        var function = _module.AddFunction("Program.F", TirType.I32);
        _module.EntryPoint = function;
        var b = new TirBuilder(function.AddBlock("entry"));
        var index = b.Const(TirType.I32, 2);
        var read = b.Load(TirType.I32, b.ElementAddress(b.AddressOf(table), TirType.I32, index));
        b.Store(b.ElementAddress(b.AddressOf(buffer), TirType.I32, index), read);
        b.Store(b.ElementAddress(b.AddressOf(table), TirType.I32, index), read);
        b.Return(read);

        Assert.Equal(["Function 'Program.F', block 'entry', 'store i32 [%7], %3': stores into @Program.Table, which is "
            + "constant: nothing may write it."], TirVerifier.FindErrors(_module));
    }
}
